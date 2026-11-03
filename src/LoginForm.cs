using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace HwNote
{
    /// <summary>登录窗口的行为配置：默认只登录，按需附带导出 / 渲染笔迹。</summary>
    public class LoginOptions
    {
        public string ExportDir = "";
        public string Match = "手写";
        public bool NoMedia;
        public IProgress<ExportProgress> Progress;
        public Action<string> Log;
        public bool KeepOpen;
        public string RenderOutDir = "";
        public string RenderRoot = "";
        public bool RenderHand = true;
    }

    /// <summary>内嵌 WebView2 的登录窗口：登录 + 预热，可按配置继续导出或渲染笔迹。</summary>
    public class LoginForm : Form
    {
        class WarmNote
        {
            public string Guid, Kind, Title;
            public bool IsImage;
        }

        readonly Panel _panel = new Panel { Dock = DockStyle.Fill };
        CoreWebView2Controller _controller;
        Timer _timer;
        bool _busy;

        public string Cookie { get; private set; }
        public string ExportResult { get; private set; }

        readonly LoginOptions _options;
        readonly string _profileDir = Path.Combine(Paths.DataDir, "webview");

        public bool ReadyFired { get; private set; }
        public event Action Ready;
        public CoreWebView2 Core { get { return _controller == null ? null : _controller.CoreWebView2; } }

        public LoginForm(LoginOptions options = null)
        {
            _options = options ?? new LoginOptions();
            Text = "jhnote 登录 - 华为云空间";
            Width = 1000;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;

            if (_options.KeepOpen)
            {
                // 会话窗口靠右放，主界面保持可见
                Width = 900;
                Height = 720;
                StartPosition = FormStartPosition.Manual;
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 24, 48);
            }

            Controls.Add(_panel);
            Shown += (sender, args) => FitWebView();
            _panel.Resize += (sender, args) => FitWebView();
            Load += async (sender, args) => await InitAsync();
        }

        void FitWebView()
        {
            if (_controller == null) return;
            var w = Math.Max(_panel.ClientSize.Width, 200);
            var h = Math.Max(_panel.ClientSize.Height, 200);
            _controller.Bounds = new Rectangle(0, 0, w, h);
        }

        void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Console.WriteLine(message);
            if (_options.Log != null) _options.Log(message);
        }

        async Task InitAsync()
        {
            try
            {
                Directory.CreateDirectory(_profileDir);
                var env = await CoreWebView2Environment.CreateAsync(null, _profileDir, null);
                _controller = await env.CreateCoreWebView2ControllerAsync(_panel.Handle);
                FitWebView();
                _controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _controller.CoreWebView2.Navigate("https://cloud.huawei.com/home");

                _timer = new Timer { Interval = 1000 };
                _timer.Tick += async (sender, args) => await PollAsync();
                _timer.Start();
                Console.WriteLine("请在窗口内登录（推荐扫码），登录完成后会自动继续…");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("✗ 无法初始化 WebView2：" + ex.Message);
                ShowInitError(ex);
                Close();
            }
        }

        /// <summary>初始化失败时把原因显示出来，避免“点登录就闪退”。</summary>
        void ShowInitError(Exception ex)
        {
            try
            {
                MessageBox.Show(this,
                    "无法初始化 WebView2，登录窗口打不开。\n\n"
                    + "错误：" + ex.Message + "\n\n"
                    + "浏览器数据目录：\n" + _profileDir + "\n\n"
                    + "常见原因：\n"
                    + "1) 已有另一个 jhnote 实例在运行（同一数据目录只允许一个）\n"
                    + "2) 程序所在目录或数据目录没有写入权限\n"
                    + "3) 杀毒软件拦截了 WebView2 的浏览器进程\n\n"
                    + "可尝试：关闭所有 jhnote / msedgewebview2 进程后重试；\n"
                    + "或把 jhnote.exe 和两个 DLL 复制到普通目录（如 D:\\jhnote）再运行。",
                    "jhnote 无法启动浏览器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        async Task PollAsync()
        {
            if (_busy || _controller == null) return;
            _busy = true;
            try
            {
                var cookies = await _controller.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.huawei.com");
                var ready = cookies.Any(c => c.Name == "loginID")
                         && cookies.Any(c => c.Name == "isToken" || c.Name == "token")
                         && cookies.Any(c => c.Name == "userId")
                         && cookies.Any(c => c.Name == "isLogin");
                var source = _controller.CoreWebView2.Source ?? "";
                if (!ready || source.IndexOf("/home", StringComparison.OrdinalIgnoreCase) < 0) return;

                _timer.Stop();
                Cookie = BuildCookie(cookies);

                // 预热：打开一条带附件的笔记，让下载网关登记会话
                Log("已获取登录会话，正在登记下载权限…");
                var warmed = false;
                foreach (var note in ChooseWarmNotes())
                {
                    Log("  预热笔记：" + note.Title + (note.IsImage ? "（带图片）" : "（带附件）"));
                    await OpenNoteAsync(note);
                    await Task.Delay(3000);
                    if (await WarmUpAsync(note)) { warmed = true; break; }
                }
                Log(warmed ? "  下载权限登记成功" : "  警告：未能登记下载权限，导出附件可能全部失败");

                // 只渲染笔迹（render 命令）
                if (!string.IsNullOrEmpty(_options.RenderOutDir))
                {
                    var failed = await NotepadRenderer.RenderAsync(
                        _controller.CoreWebView2, _options.Match, _options.RenderOutDir, _options.RenderRoot, Log);
                    ExportResult = _options.RenderOutDir;
                    Text = failed == 0 ? "渲染完成" : ("渲染失败 " + failed + " 条");
                    await Task.Delay(1500);
                    Close();
                    return;
                }

                // 只登录（会话交给主界面，导出由主界面触发）
                if (_options.KeepOpen)
                {
                    ReadyFired = true;
                    Log("预热完成，可以开始导出");
                    var handler = Ready;
                    if (handler != null) handler();
                    return;
                }

                // 一次完成：导出（可选渲染笔迹）
                if (!string.IsNullOrEmpty(_options.ExportDir))
                {
                    Log("预热完成，开始导出…");
                    Text = "正在导出（可稍等）…";
                    var progress = new Progress<ExportProgress>(p =>
                    {
                        if (p.Total > 0)
                            Text = string.Format("导出 {0}/{1}，附件 {2}", p.Done, p.Total, p.Media);
                        if (_options.Progress != null) _options.Progress.Report(p);
                    });
                    await Task.Run(() => Exporter.Run(_options.ExportDir, Cookie, _options.NoMedia, 0, "", progress));
                    ExportResult = _options.ExportDir;

                    if (_options.RenderHand)
                    {
                        Text = "导出完成，正在渲染手写笔迹…";
                        var staging = Path.Combine(_options.ExportDir, ".render_tmp");
                        var failed = await NotepadRenderer.RenderAsync(
                            _controller.CoreWebView2, _options.Match, staging, _options.ExportDir, Log);
                        try { if (Directory.Exists(staging) && Directory.GetFiles(staging).Length == 0) Directory.Delete(staging); } catch { }
                        Text = failed == 0 ? "全部完成" : ("完成，笔迹失败 " + failed + " 条");
                    }
                    else
                    {
                        Text = "导出完成";
                    }
                    await Task.Delay(2000);
                    Close();
                    return;
                }

                // 纯登录：保存最新 cookie 后关闭
                cookies = await _controller.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.huawei.com");
                Cookie = BuildCookie(cookies);
                Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("轮询异常：" + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        static string BuildCookie(IEnumerable<CoreWebView2Cookie> cookies)
        {
            return string.Join("; ", cookies
                .Where(c => c.Domain != null && c.Domain.Contains("huawei.com"))
                .Select(c => c.Name + "=" + c.Value));
        }

        /// <summary>优先挑带图片的笔记，其次任何带附件的，最多 3 条候选。</summary>
        List<WarmNote> ChooseWarmNotes()
        {
            try
            {
                var api = new Api(Cookie);
                var serializer = new JavaScriptSerializer();
                var images = new List<WarmNote>();
                var others = new List<WarmNote>();

                foreach (var item in api.Notes())
                {
                    Dictionary<string, object> note;
                    try { note = serializer.Deserialize<Dictionary<string, object>>(Api.Str(item, "data")); }
                    catch { continue; }
                    if (Api.Num(note, "has_attachment") == 0) continue;

                    var candidate = new WarmNote
                    {
                        Guid = Api.Str(note, "guid"),
                        Kind = Api.Str(item, "kind").Length > 0 ? Api.Str(item, "kind") : "newnote",
                        Title = Api.Str(note, "title").Split('\n')[0].Trim(),
                        IsImage = Api.Num(note, "has_image") == 1
                    };
                    if (candidate.IsImage) images.Add(candidate); else others.Add(candidate);
                    if (images.Count >= 3) break;
                }

                var list = new List<WarmNote>();
                list.AddRange(images);
                list.AddRange(others);
                return list.Count > 3 ? list.GetRange(0, 3) : list;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  选择预热笔记失败：" + ex.Message);
                return new List<WarmNote>();
            }
        }

        async Task OpenNoteAsync(WarmNote note)
        {
            var url = "https://cloud.huawei.com/home#/notepad/note/allNote/" + note.Kind + "/" + note.Guid;
            _controller.CoreWebView2.Navigate(url);
            await Task.Delay(3000);
        }

        async Task<bool> WarmUpAsync(WarmNote note)
        {
            try
            {
                var api = new Api(Cookie);
                List<Dictionary<string, object>> attachments;
                api.DetailHtml(note.Guid, note.Kind, out attachments);
                if (attachments.Count == 0)
                {
                    Log("  该笔记没有附件，换下一条");
                    return false;
                }

                var att = attachments[0];
                var assetId = Api.Str(att, "assetId");
                var versionId = Api.Str(att, "versionId");
                var path = string.Format(
                    "/v2/dataSync/callback/v1/1001/kind/{0}/record/{1}/assets/{2}/revisions/{3}",
                    note.Kind, note.Guid, assetId, versionId);
                var url = "https://cloud.huawei.com/proxy/v1/download/" + Uri.EscapeDataString(path);

                // 用 <img> 真正发一次请求，让网关把这个会话登记下来
                var expression = "(function(){return new Promise(function(res){" +
                    "var i=new Image();i.onload=function(){res('load')};i.onerror=function(){res('error')};" +
                    "i.src='" + url.Replace("'", "\\'") + "';setTimeout(function(){res('timeout')},12000);});})()";
                var payload = "{\"expression\":\"" +
                    expression.Replace("\\", "\\\\").Replace("\"", "\\\"") +
                    "\",\"awaitPromise\":true,\"returnByValue\":true}";
                var result = await _controller.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", payload);
                Log("  页面加载附件：" + result);

                // 再用 .NET 侧探测一次，确认会话已登记
                for (var i = 0; i < 5; i++)
                {
                    var code = api.Probe(note.Kind, note.Guid, assetId, versionId);
                    Log("  探测下载返回码：" + code);
                    if (code == 200) return true;
                    await Task.Delay(2000);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log("  预热失败：" + ex.Message);
                return false;
            }
        }
    }
}
