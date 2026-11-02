using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace HwNote
{
    public class RenderTarget
    {
        public string Guid;
        public string Kind;
        public string Title;
        public string Path;
    }

    /// <summary>在已登录的 WebView2 会话里渲染笔记画布（登录流程和独立 render 命令共用）。</summary>
    public static class NotepadRenderer
    {
        public static async Task<int> RenderAsync(CoreWebView2 core, string match, string outDir,
                                                 string exportRoot, Action<string> log,
                                                 Action<int, int> onProgress = null,
                                                 System.Threading.CancellationToken token = default(System.Threading.CancellationToken))
        {
            var failed = 0;
            var targets = BuildTargetsFromFile(exportRoot, outDir);
            if (targets == null) targets = await BuildTargetsAsync(core, match, outDir);
            if (targets == null || targets.Count == 0)
            {
                log("没有匹配「" + match + "」且有附件的笔记");
                return 0;
            }

            log("待渲染 " + targets.Count + " 条 -> " + outDir);
            var index = 0;
            foreach (var t in targets)
            {
                if (token.IsCancellationRequested) break;
                index++;
                var url = "https://cloud.huawei.com/home#/notepad/note/allNote/" + t.Kind + "/" + t.Guid;
                core.Navigate(url);
                await Task.Delay(6000);

                var href = await HrefAsync(core);
                if (!IsNoteUrl(href))
                {
                    await Task.Delay(3000);
                    core.Navigate(url);
                    await Task.Delay(6000);
                    href = await HrefAsync(core);
                }
                if (!IsNoteUrl(href))
                {
                    log("  ! 未进入笔记：" + href);
                    failed++;
                    continue;
                }

                var saved = await CaptureAsync(core, t.Path);
                var patched = saved && !string.IsNullOrEmpty(exportRoot) && PatchMarkdown(t, exportRoot);
                log("手写笔迹 [" + t.Guid.Substring(0, 6) + "] -> " +
                    (saved ? "OK" : "失败") + (patched ? "，已替换 md 图片" : ""));
                if (!saved) failed++;
                if (onProgress != null) onProgress(index, targets.Count);
            }
            return failed;
        }

        static async Task<string> HrefAsync(CoreWebView2 core)
        {
            try
            {
                return new JavaScriptSerializer().Deserialize<string>(
                    await core.ExecuteScriptAsync("location.href")) ?? "";
            }
            catch { return ""; }
        }

        static bool IsNoteUrl(string href)
        {
            return !string.IsNullOrEmpty(href)
                && href.IndexOf("notepad/note", StringComparison.OrdinalIgnoreCase) >= 0
                && href.IndexOf("login", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>导出时记录了真正含 HWGRAFFI 笔迹的笔记，优先用这份清单。</summary>
        static List<RenderTarget> BuildTargetsFromFile(string exportRoot, string outDir)
        {
            if (string.IsNullOrEmpty(exportRoot)) return null;
            var file = Path.Combine(exportRoot, "handwriting.json");
            if (!File.Exists(file)) return null;
            try
            {
                var json = File.ReadAllText(file);
                var items = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(json);
                if (items == null || items.Count == 0) return null;

                var targets = new List<RenderTarget>();
                foreach (var it in items)
                {
                    var guid = Api.Str(it, "guid");
                    var kind = Api.Str(it, "kind");
                    var title = Api.Str(it, "title");
                    if (guid.Length == 0) continue;
                    targets.Add(MakeTarget(guid, kind, title, outDir));
                }
                return targets.Count > 0 ? targets : null;
            }
            catch { return null; }
        }
        static RenderTarget MakeTarget(string guid, string kind, string title, string outDir)
        {
            if (string.IsNullOrEmpty(kind)) kind = "newnote";
            return new RenderTarget
            {
                Guid = guid,
                Kind = kind,
                Title = title,
                Path = Path.Combine(outDir, Render.Sanitize(title) + "_" +
                    (guid.Length > 6 ? guid.Substring(0, 6) : guid) + ".png")
            };
        }
        static async Task<List<RenderTarget>> BuildTargetsAsync(CoreWebView2 core, string match, string outDir)
        {
            for (var i = 0; i < 20; i++)
            {
                var list = await TryFetchAsync(core, match, outDir);
                if (list != null) return list;
                await Task.Delay(2000);
            }
            return null;
        }

        static async Task<List<RenderTarget>> TryFetchAsync(CoreWebView2 core, string match, string outDir)
        {
            var targets = new List<RenderTarget>();
            var expression =
                "fetch('/notepad/simplenote/query',{method:'POST',credentials:'include'," +
                "headers:{'Content-Type':'application/json'},body:JSON.stringify({guids:'',traceId:'r'+Date.now()})})" +
                ".then(r=>r.text()).catch(e=>'')";
            var payload = "{\"expression\":\"" + JsonEscape(expression) +
                          "\",\"awaitPromise\":true,\"returnByValue\":true}";
            var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", payload);
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw);
            var result = Api.Dict(root.ContainsKey("result") ? root["result"] : null);
            var text = Api.Str(result, "value");
            if (text.Length == 0 || !text.TrimStart().StartsWith("{")) return null;

            Dictionary<string, object> data;
            try { data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }
            catch { return null; }

            var rsp = Api.Dict(data.ContainsKey("rspInfo") ? data["rspInfo"] : null);
            var items = Api.List(rsp.ContainsKey("noteList") ? rsp["noteList"] : null);
            foreach (var item in items)
            {
                var it = Api.Dict(item);
                Dictionary<string, object> note;
                try { note = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Api.Str(it, "data")); }
                catch { continue; }
                if (Api.Num(note, "has_attachment") == 0) continue;

                var title = Api.Str(note, "title").Split('\n')[0].Trim();
                if (title.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var guid = Api.Str(note, "guid");
                var kind = Api.Str(it, "kind");
                if (kind.Length == 0) kind = "newnote";
                targets.Add(MakeTarget(guid, kind, title, outDir));
            }
            return targets;
        }

        static string JsonEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        static bool PatchMarkdown(RenderTarget t, string exportRoot)
        {
            try
            {
                var notesDir = Path.Combine(exportRoot, "notes");
                if (!Directory.Exists(notesDir)) return false;

                var needle = "note_id: \"" + t.Guid + "\"";
                foreach (var md in Directory.GetFiles(notesDir, "*.md"))
                {
                    var text = File.ReadAllText(md);
                    if (text.IndexOf(needle, StringComparison.Ordinal) < 0) continue;

                    // 长图直接放进该笔记自己的 assets 目录，和附件放一起
                    var stem = Path.GetFileNameWithoutExtension(md);
                    var assetsDir = Path.Combine(exportRoot, "assets", stem);
                    Directory.CreateDirectory(assetsDir);
                    var dest = Path.Combine(assetsDir, "手写笔迹.png");
                    try
                    {
                        if (File.Exists(t.Path))
                        {
                            if (File.Exists(dest)) File.Delete(dest);
                            File.Move(t.Path, dest);
                        }
                    }
                    catch { }

                    var rel = Render.EncodePath("../assets/" + stem + "/手写笔迹.png");
                    var replaced = System.Text.RegularExpressions.Regex.Replace(
                        text, @"!\[[^\]]*\]\(\.\./assets/[^)]*\)", "![手写笔迹](" + rel + ")");
                    if (replaced != text) File.WriteAllText(md, replaced);
                    return replaced != text;
                }
            }
            catch { }
            return false;
        }

        static async Task<bool> CaptureAsync(CoreWebView2 core, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var js = "(function(){var cs=document.querySelectorAll('canvas');" +
                     "if(!cs.length)return '';var best=cs[0];" +
                     "for(var i=1;i<cs.length;i++){if(cs[i].width*cs[i].height>best.width*best.height)best=cs[i];}" +
                     "if(best.width<50||best.height<50)return '';" +
                     "try{return best.toDataURL('image/png');}catch(e){return '';}})()";
            var raw = await core.ExecuteScriptAsync(js);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    var dataUrl = new JavaScriptSerializer().Deserialize<string>(raw);
                    if (!string.IsNullOrEmpty(dataUrl) && dataUrl.StartsWith("data:image"))
                    {
                        File.WriteAllBytes(path, Convert.FromBase64String(dataUrl.Substring(dataUrl.IndexOf(",") + 1)));
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
