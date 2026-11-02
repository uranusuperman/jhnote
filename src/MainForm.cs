using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HwNote
{
    /// <summary>主窗口：登录状态 / 导出目录 / 开始取消 / 进度与日志。</summary>
    public class MainForm : Form
    {
        readonly Label _status = new Label { AutoSize = true, Text = "状态：检查中…" };
        readonly Button _login = new Button { Text = "登录", Width = 80 };
        readonly Button _logout = new Button { Text = "退出登录", Width = 90, Enabled = false };
        readonly TextBox _outDir = new TextBox { Width = 420 };
        readonly Button _browse = new Button { Text = "浏览…", Width = 70 };
        readonly CheckBox _noMedia = new CheckBox { Text = "只导文字（不下载附件）", AutoSize = true };
        readonly CheckBox _withHand = new CheckBox { Text = "同时渲染手写笔迹", AutoSize = true, Checked = true };
        readonly Button _start = new Button { Text = "开始导出", Width = 110, Height = 32 };
        readonly Button _cancel = new Button { Text = "取消", Width = 80, Height = 32, Enabled = false };
        readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 560 };
        readonly Label _progress = new Label { AutoSize = true, Text = "就绪" };
        readonly TextBox _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Width = 620, Height = 220, Font = new Font("Consolas", 9f)
        };

        CancellationTokenSource _cts;
        LoginForm _session;
        string _cookie = "";
        int _lastTotal, _lastMedia, _lastFail;

        public MainForm()
        {
            Text = "jhnote - 华为云空间备忘录导出";
            Width = 680;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            var top = 12;
            _status.Location = new Point(16, top);
            _login.Location = new Point(466, top - 6);
            _logout.Location = new Point(552, top - 6);
            Controls.Add(_status);
            Controls.Add(_login);
            Controls.Add(_logout);

            Controls.Add(new Label { Text = "导出到", AutoSize = true, Location = new Point(16, top + 34) });
            _outDir.Location = new Point(80, top + 30);
            _browse.Location = new Point(506, top + 29);
            Controls.Add(_outDir);
            Controls.Add(_browse);

            _noMedia.Location = new Point(80, top + 62);
            Controls.Add(_noMedia);
            _withHand.Location = new Point(290, top + 62);
            Controls.Add(_withHand);

            _start.Location = new Point(80, top + 92);
            _cancel.Location = new Point(200, top + 92);
            Controls.Add(_start);
            Controls.Add(_cancel);

            _bar.Location = new Point(16, top + 136);
            _progress.Location = new Point(16, top + 162);
            Controls.Add(_bar);
            Controls.Add(_progress);

            _log.Location = new Point(16, top + 190);
            Controls.Add(_log);

            _outDir.Text = DefaultOutputDir();

            _login.Click += async (s, e) => await DoLoginAsync();
            _logout.Click += async (s, e) => await DoLogoutAsync();
            _browse.Click += (s, e) => Browse();
            _start.Click += async (s, e) => await StartAsync();
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); };

            Load += async (s, e) => await RefreshStatusAsync();
        }

        static string DefaultOutputDir()
        {
            var docs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "jhnote-export");
            try
            {
                Directory.CreateDirectory(docs);
                return docs;
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "export");
            }
        }

        void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _log.AppendText(line + Environment.NewLine);
        }


        async Task RefreshStatusAsync()
        {
            var cookie = _cookie ?? "";
            if (cookie.Length == 0)
            {
                _status.Text = "状态：未登录";
                _status.ForeColor = Color.Firebrick;
                _login.Text = "登录";
                _logout.Enabled = false;
                return;
            }
            _status.Text = "状态：检查会话中…";
            _status.ForeColor = Color.DimGray;
            _logout.Enabled = true;
            try
            {
                var api = new Api(cookie);
                var count = await Task.Run(() => api.Notes().Count);
                _status.Text = string.Format("状态：已登录（笔记 {0} 条）", count);
                _status.ForeColor = Color.ForestGreen;
                _login.Text = "登录";
            }
            catch (Exception ex)
            {
                _status.Text = "状态：会话失效（" + ex.Message + "）";
                _status.ForeColor = Color.Firebrick;
                _login.Text = "登录";
                _logout.Enabled = true;
            }
        }

        async Task<bool> EnsureSessionAsync(IProgress<ExportProgress> progress, Action<string> log)
        {
            if (_session != null && !_session.IsDisposed && _session.ReadyFired) return true;

            // 残留的未就绪窗口先关掉，避免复用旧会话
            if (_session != null && !_session.IsDisposed)
            {
                try { _session.Close(); } catch { }
                _session = null;
            }

            var tcs = new TaskCompletionSource<bool>();
            _session = new LoginForm(new LoginOptions
            {
                Progress = progress,
                Log = log,
                KeepOpen = true
            });
            _session.Ready += () => tcs.TrySetResult(true);
            _session.FormClosed += (s, e) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            };
            _session.Show();

            var ok = await tcs.Task;
            if (ok) _cookie = _session.Cookie;   // 只放内存，进程结束即失效
            return ok;
        }

        async Task DoLogoutAsync()
        {
            if (_cts != null)
            {
                MessageBox.Show(this, "正在导出，请先点「取消」结束当前任务", "jhnote",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                "退出登录会关闭当前会话（不会退出云端账号）。\n下次导出需要重新扫码。要继续吗？",
                "jhnote", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            if (_session != null && !_session.IsDisposed)
            {
                try { _session.Close(); } catch { }
            }
            _session = null;
            _cookie = "";

            _progress.Text = "未登录";
            AppendLog("已退出登录");
            await RefreshStatusAsync();
        }
        async Task DoLoginAsync()
        {
            _progress.Text = "等待预热中……";
            var ok = await EnsureSessionAsync(null, null);
            if (ok)
            {
                Activate();
                BringToFront();
                _progress.Text = "可以开始导出";
                AppendLog("预热完成，可以开始导出");
            }
            else
            {
                _progress.Text = "登录未完成";
                AppendLog("登录未完成");
            }
            await RefreshStatusAsync();
        }

        void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择导出目录";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _outDir.Text = dialog.SelectedPath;
            }
        }

        async Task StartAsync()
        {
            var outDir = _outDir.Text.Trim();
            if (outDir.Length == 0)
            {
                MessageBox.Show(this, "请选择导出目录", "jhnote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var hand = _withHand.Checked;
            if ((_cookie ?? "").Length == 0 || (hand && (_session == null || _session.IsDisposed)))
            {
                MessageBox.Show(this, "请先登录", "jhnote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            SetBusy(true);
            _log.Clear();

            try
            {
                if (hand)
                {
                    Activate();
                    BringToFront();
                    _progress.Text = "可以开始导出";
                }

                var cookie = _cookie ?? "";
                await Task.Run(() => Exporter.Run(outDir, cookie, _noMedia.Checked, 0, "", ProgressSink(), cts.Token));

                if (cts.IsCancellationRequested) throw new OperationCanceledException();
                if (hand) await RenderHandwritingAsync(outDir, cts);

                MessageBox.Show(this, hand ? "导出已全部完成！！！\n\n" + outDir : "导出完成：\n" + outDir,
                    "jhnote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                AppendLog("已取消");
                _progress.Text = "已取消";
            }
            catch (Exception ex)
            {
                AppendLog("错误：" + ex.Message);
                _progress.Text = "导出失败";
            }
            finally
            {
                SetBusy(false);
                _cts = null;
                await RefreshStatusAsync();
            }
        }

        void SetBusy(bool busy)
        {
            _start.Enabled = !busy;
            _login.Enabled = !busy;
            _cancel.Enabled = busy;
            _logout.Enabled = !busy && (_cookie ?? "").Length > 0;
            if (busy)
            {
                _bar.Value = 0;
                _progress.Text = "准备中…";
            }
        }

        IProgress<ExportProgress> ProgressSink()
        {
            return new Progress<ExportProgress>(p =>
            {
                if (p.Message.Length > 0) AppendLog(p.Message);
                if (p.Total > 0)
                {
                    _lastTotal = p.Total; _lastMedia = p.Media; _lastFail = p.MediaFail;
                    _bar.Value = Math.Min(100, p.Done * 100 / p.Total);
                    _progress.Text = string.Format("{0} / {1}   附件 {2}（失败 {3}）   当前：{4}",
                        p.Done, p.Total, p.Media, p.MediaFail, p.Current);
                }
                if (p.Finished)
                {
                    _lastTotal = p.Total; _lastMedia = p.Media; _lastFail = p.MediaFail;
                    _progress.Text = string.Format("完成：{0} 条，附件 {1} 个，失败 {2}", p.Total, p.Media, p.MediaFail);
                    _bar.Value = 100;
                }
            });
        }

        async Task RenderHandwritingAsync(string outDir, CancellationTokenSource cts)
        {
            _progress.Text = string.Format("完成：{0} 条，附件 {1} 个，失败 {2}；手写笔迹 0/0",
                _lastTotal, _lastMedia, _lastFail);

            var staging = Path.Combine(outDir, ".render_tmp");
            var done = 0;
            var failed = await NotepadRenderer.RenderAsync(
                _session.Core, "手写", staging, outDir, AppendLog,
                (n, total) =>
                {
                    done = n;
                    _progress.Text = string.Format("完成：{0} 条，附件 {1} 个，失败 {2}；手写笔迹 {3}/{4}",
                        _lastTotal, _lastMedia, _lastFail, n, total);
                }, cts.Token);

            try { if (Directory.Exists(staging) && Directory.GetFiles(staging).Length == 0) Directory.Delete(staging); } catch { }

            if (cts.IsCancellationRequested) throw new OperationCanceledException();

            AppendLog(failed == 0 ? "笔迹渲染完成" : ("笔迹渲染失败 " + failed + " 条"));
            AppendLog("导出已全部完成！！！");
            _progress.Text = string.Format("完成：{0} 条，附件 {1} 个，失败 {2}；手写笔迹 {3} 条",
                _lastTotal, _lastMedia, _lastFail, Math.Max(done - failed, 0));
        }
    }
}