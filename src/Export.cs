using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HwNote
{
    /// <summary>导出编排：列表 → 详情 → 附件 → Markdown。</summary>
    public static class Exporter
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly Dictionary<string, string> FolderAlias = new Dictionary<string, string>
        {
            { "My_notes", "我的笔记" }, { "personal", "个人" },
            { "work", "工作" }, { "travel", "旅行" }
        };

        public static string CleanName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Contains("|"))
            {
                var parts = name.Split(new[] { '|' }, 2);
                return CleanName(parts[0]) + "|" + CleanName(parts[1]);
            }
            if (name.StartsWith("sys-def-")) name = name.Substring(8);
            return FolderAlias.ContainsKey(name) ? FolderAlias[name] : name;
        }

        public static string Stamp(long ms)
        {
            if (ms <= 0) return "";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        static string Sniff(byte[] blob, string fallback)
        {
            if (blob.Length >= 8 && blob[0] == 0x89 && blob[1] == 0x50) return ".png";
            if (blob.Length >= 3 && blob[0] == 0xFF && blob[1] == 0xD8) return ".jpg";
            if (blob.Length >= 12 && blob[0] == 'R' && blob[1] == 'I' && blob[2] == 'F' && blob[3] == 'F') return ".webp";
            if (blob.Length >= 6 && blob[0] == 'G' && blob[1] == 'I' && blob[2] == 'F') return ".gif";
            if (blob.Length >= 2 && blob[0] == 'B' && blob[1] == 'M') return ".bmp";
            return fallback;
        }

        /// <summary>同一附件的多个版本里挑一个（优先 _original）。</summary>
        static List<Dictionary<string, object>> PickAttachments(List<Dictionary<string, object>> raw)
        {
            var groups = new Dictionary<string, List<Dictionary<string, object>>>();
            foreach (var att in raw)
            {
                var usage = Api.Str(att, "usage").Replace("\\", "/");
                var name = usage.Substring(usage.LastIndexOf('/') + 1);
                if (name.Length == 0) continue;
                var stem = Path.GetFileNameWithoutExtension(name);
                var original = stem.EndsWith("_original", StringComparison.OrdinalIgnoreCase);
                var key = original ? stem.Substring(0, stem.Length - "_original".Length) : stem;

                var item = new Dictionary<string, object>
                {
                    { "base", name },
                    { "ext", Path.GetExtension(name).ToLowerInvariant() },
                    { "orig", original },
                    { "asset", Api.Str(att, "assetId") },
                    { "version", Api.Str(att, "versionId") },
                    { "size", Api.Num(att, "resourceLength") }
                };
                if (!groups.ContainsKey(key)) groups[key] = new List<Dictionary<string, object>>();
                groups[key].Add(item);
            }

            var picked = new List<Dictionary<string, object>>();
            foreach (var versions in groups.Values)
            {
                var pool = versions.Where(v => (bool)v["orig"]).ToList();
                if (pool.Count == 0) pool = versions;
                pool.Sort((a, b) => ((long)b["size"]).CompareTo((long)a["size"]));
                picked.Add(pool[0]);
            }
            return picked;
        }

        static void Log(IProgress<ExportProgress> progress, string message)
        {
            Console.WriteLine(message);
            if (progress != null)
                progress.Report(new ExportProgress { Message = message });
        }

        public static void Run(string outDir, string cookie, bool noMedia, int limit, string match,
                               IProgress<ExportProgress> progress = null,
                               System.Threading.CancellationToken token = default(System.Threading.CancellationToken))
        {
            var api = new Api(cookie);
            var root = Path.GetFullPath(outDir);
            var notesDir = Path.Combine(root, "notes");
            var assetsDir = Path.Combine(root, "assets");
            Directory.CreateDirectory(notesDir);
            Directory.CreateDirectory(assetsDir);

            Log(progress, "· 读取分类…");
            var tags = api.Tags();
            Log(progress, "· 读取列表…");
            var items = api.Notes();
            if (!string.IsNullOrEmpty(match))
            {
                items = items.Where(it =>
                {
                    try { return TitleOf(it).IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0; }
                    catch { return false; }
                }).ToList();
            }
            if (limit > 0) items = items.Take(limit).ToList();
            Log(progress, string.Format("  笔记 {0} 条，分类 {1} 个", items.Count, tags.Count));

            var rows = new List<string[]>();
            var handwriting = new List<Dictionary<string, string>>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int ok = 0, media = 0, mediaFail = 0;

            for (var i = 0; i < items.Count; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                var item = items[i];
                Dictionary<string, object> note;
                try { note = Json.Deserialize<Dictionary<string, object>>(Api.Str(item, "data")); }
                catch { continue; }
                if (Api.Num(note, "delete_flag") != 0) continue;

                var guid = Api.Str(note, "guid");
                if (guid.Length == 0) guid = Api.Str(item, "guid");
                var kind = Api.Str(item, "kind");
                if (kind.Length == 0) kind = "newnote";
                var title = Api.Str(note, "title").Split('\n')[0].Trim();
                if (title.Length == 0) title = "无标题";

                var tagId = Api.Str(note, "tag_id");
                var folder = "未分类";
                var tag = "";
                if (tagId.Length > 0 && tags.ContainsKey(tagId))
                {
                    tag = CleanName(tags[tagId]["name"]);
                    var f = CleanName(tags[tagId]["folder"]);
                    if (f.Length > 0) folder = f;
                }

                List<Dictionary<string, object>> attachments;
                string html;
                try { html = api.DetailHtml(guid, kind, out attachments); }
                catch (AuthException) { throw; }
                catch (Exception ex) { Console.WriteLine("  ! {0} 详情失败: {1}", Short(title), ex.Message); continue; }

                var blocks = Render.ParseBlocks(html);
                var picked = PickAttachments(attachments);

                var stem = Render.Sanitize(title);
                var unique = stem;
                var n = 1;
                while (used.Contains(unique)) { n++; unique = stem + "_" + n; }
                used.Add(unique);

                var created = Stamp(Api.Num(note, "created"));
                var modified = Stamp(Api.Num(note, "modified"));
                var date = created.Length > 0 ? created.Substring(0, 10).Replace("-", "") : "";
                var stemFull = date.Length > 0 ? date + "_" + unique : unique;

                var saved = new Dictionary<string, string>();
                var savedFiles = new List<string>();
                var hasStrokes = false;
                if (picked.Count > 0 && !noMedia)
                {
                    var folderPath = Path.Combine(assetsDir, stemFull);
                    Directory.CreateDirectory(folderPath);
                    var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in picked)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        var name = Render.Sanitize(Path.GetFileNameWithoutExtension((string)p["base"]), 50);
                        try
                        {
                            var blob = api.Download(kind, guid, (string)p["asset"], (string)p["version"]);
                            var ext = Sniff(blob, (string)p["ext"]);
                            // 华为手写笔迹文件以 HWGRAFFI 开头
                            if (blob.Length > 8 && Encoding.ASCII.GetString(blob, 0, 8) == "HWGRAFFI")
                                hasStrokes = true;
                            if (ext.Length == 0) ext = ".bin";
                            var file = name + ext;
                            var k = 1;
                            while (usedFiles.Contains(file)) { k++; file = name + "_" + k + ext; }
                            usedFiles.Add(file);
                            File.WriteAllBytes(Path.Combine(folderPath, file), blob);
                            saved[(string)p["base"]] = file;
                            savedFiles.Add("assets/" + stemFull + "/" + file);
                            media++;
                        }
                        catch (Exception ex) { mediaFail++; Log(progress, "    附件失败: " + ex.Message); }
                    }
                }

                var meta = new Dictionary<string, string>
                {
                    { "title", title }, { "created", created }, { "modified", modified },
                    { "folder", folder }, { "tag", tag }, { "note_id", guid },
                    { "files", string.Join("\u0001", savedFiles) }
                };
                var mdName = stemFull + ".md";
                var markdown = Render.RenderNote(meta, blocks, saved, stemFull);

                // 如果这条笔记已经有渲染好的手写笔迹，md 里优先用它，避免重新导出后被缩略图覆盖
                var longImage = Path.Combine(root, "assets", stemFull, "手写笔迹.png");
                if (!File.Exists(longImage))
                    longImage = Path.Combine(root, "assets", stemFull, "手写原稿.png");
                if (File.Exists(longImage))
                {
                    var imageMatch = System.Text.RegularExpressions.Regex.Match(
                        markdown, @"!\[[^\]]*\]\(\.\./assets/[^)]+\)");
                    if (imageMatch.Success)
                    {
                        markdown = markdown.Substring(0, imageMatch.Index)
                            + "![手写笔迹](" + Render.EncodePath("../assets/" + stemFull + "/" + Path.GetFileName(longImage)) + ")"
                            + markdown.Substring(imageMatch.Index + imageMatch.Length);
                    }
                }

                File.WriteAllText(Path.Combine(notesDir, mdName), markdown, new UTF8Encoding(false));

                if (hasStrokes)
                {
                    handwriting.Add(new Dictionary<string, string>
                    {
                        { "guid", guid }, { "kind", kind }, { "title", title }
                    });
                }

                var chars = blocks.Where(b => !b.Attachment).Sum(b => b.Value.Length);
                rows.Add(new[]
                {
                    guid, title, created, modified, folder, tag,
                    chars.ToString(CultureInfo.InvariantCulture),
                    picked.Count.ToString(CultureInfo.InvariantCulture),
                    "notes/" + mdName
                });
                ok++;
                if (progress != null) progress.Report(new ExportProgress
                {
                    Total = items.Count, Done = i + 1, Current = title,
                    Media = media, MediaFail = mediaFail
                });
                if ((i + 1) % 25 == 0)
                    Log(progress, string.Format("  [{0}/{1}] 导出 {2}，附件 {3}", i + 1, items.Count, ok, media));
            }

            WriteCsv(Path.Combine(root, "index.csv"), rows);
            try
            {
                File.WriteAllText(Path.Combine(root, "handwriting.json"),
                    new JavaScriptSerializer().Serialize(handwriting), new UTF8Encoding(false));
            }
            catch { }

            File.WriteAllText(Path.Combine(root, "export_report.md"),
                string.Format("# 导出报告\n\n- 笔记：{0}（成功 {1}）\n- 附件：{2} 个，失败 {3}\n- 生成时间：{4}\n",
                    items.Count, ok, media, mediaFail,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), new UTF8Encoding(false));

            Log(progress, string.Format("· 完成：导出 {0} 条，附件 {1} 个（失败 {2}）→ {3}", ok, media, mediaFail, root));
            if (progress != null) progress.Report(new ExportProgress
            {
                Total = items.Count, Done = items.Count, Current = "完成",
                Media = media, MediaFail = mediaFail, Finished = true
            });
        }

        static string TitleOf(Dictionary<string, object> item)
        {
            var note = Json.Deserialize<Dictionary<string, object>>(Api.Str(item, "data"));
            return Api.Str(note, "title");
        }

        static string Short(string text)
        {
            return text.Length <= 18 ? text : text.Substring(0, 18);
        }

        static void WriteCsv(string path, List<string[]> rows)
        {
            var header = "note_id,title,created,modified,folder,tag,chars,attachments,md";
            var sb = new StringBuilder();
            sb.AppendLine(header);
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(Escape)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string Escape(string value)
        {
            if (value == null) return "";
            return value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }
}
