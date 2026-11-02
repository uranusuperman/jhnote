using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HwNote
{
    public class Block
    {
        public bool Attachment;
        public string Value;
    }

    /// <summary>HTML 块 → Markdown。</summary>
    public static class Render
    {
        static readonly Regex ElementRx = new Regex(
            "<element\\s+type=\"([^\"]*)\"\\s*>(.*?)</element>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        static readonly Regex TagRx = new Regex("<[^>]+>", RegexOptions.Singleline);

        static readonly string[] AttachTypes =
            { "attachment", "image", "audio", "video", "file", "doc", "document" };
        static readonly string[] ImageExt =
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic" };

        public static List<Block> ParseBlocks(string html)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(html)) return blocks;

            foreach (Match match in ElementRx.Matches(html))
            {
                var type = match.Groups[1].Value.Trim().ToLowerInvariant();
                var inner = match.Groups[2].Value;

                if (AttachTypes.Contains(type))
                {
                    var path = WebUtility.HtmlDecode(TagRx.Replace(inner, "")).Trim().Replace("\\", "/");
                    var name = path.Substring(path.LastIndexOf('/') + 1);
                    if (name.Length > 0) blocks.Add(new Block { Attachment = true, Value = name });
                    continue;
                }

                var text = Regex.Replace(inner, @"<br\s*/?>[ \t]*\r?\n?", "\n");
                text = WebUtility.HtmlDecode(TagRx.Replace(text, ""));
                foreach (var line in text.Split('\n'))
                    blocks.Add(new Block { Value = PreserveLine(line) });
            }

            if (blocks.Count == 0)
            {
                var text = WebUtility.HtmlDecode(TagRx.Replace(html, "\n"));
                foreach (var line in text.Split('\n'))
                    if (line.Trim().Length > 0) blocks.Add(new Block { Value = line.Trim() });
            }
            return blocks;
        }

        /// <summary>
        /// 图片替代文字：去掉 $、[] 等会破坏 Markdown / Obsidian 解析的字符。
        /// 文件名里的 $ 成对出现时会被当成行内公式定界符，导致图片渲染失败。
        /// </summary>
        static string AltText(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in name)
            {
                if ("[]$*_`|<>{}".IndexOf(ch) >= 0) continue;
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
            }
            var text = sb.ToString().Trim();
            if (text.Length > 40) text = text.Substring(0, 40);
            return text.Length == 0 ? "图片" : text;
        }

        /// <summary>
        /// 路径里出现空格等字符时，Markdown 链接会在空格处截断，导致 Obsidian 报“找不到”。
        /// 统一做百分号编码。
        /// </summary>
        public static string EncodePath(string path)
        {
            return path.Replace("%", "%25").Replace(" ", "%20").Replace("#", "%23").Replace("?", "%3F").Replace("(", "%28").Replace(")", "%29");
        }

        /// <summary>
        /// 保留原笔记的对齐：行首与连续空格转成 NBSP，避免 Markdown 压缩或当成代码块；
        /// 行首的“1.”等序号做转义，避免被解析成有序列表而丢掉编号。
        /// </summary>
        static string PreserveLine(string raw)
        {
            var line = raw.Replace("\r", "").TrimEnd();
            if (line.Trim().Length == 0) return "";

            // 行首空格 → NBSP
            var lead = 0;
            while (lead < line.Length && line[lead] == ' ') lead++;
            if (lead > 0) line = new string('\u00A0', lead) + line.Substring(lead);

            // 连续空格 → NBSP（单个空格保持原样）
            var sb = new StringBuilder();
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == ' ' && ((i + 1 < line.Length && line[i + 1] == ' ') || (i > 0 && line[i - 1] == ' ')))
                    sb.Append('\u00A0');
                else
                    sb.Append(ch);
            }
            line = sb.ToString();

            // 行首数字序号转义：1. → 1\.
            return Regex.Replace(line, @"^(\u00A0*)(\d+)\.", "$1$2\\.");
        }
        public static string Sanitize(string name, int maxLength = 60)
        {
            if (name == null) name = "";
            name = Regex.Replace(name.Replace("\r", " ").Replace("\n", " "), "[\\\\/:*?\"<>|\\x00-\\x1f]", "_");
            name = Regex.Replace(name, "\\s+", " ").Trim(' ', '.');
            if (name.Length > maxLength) name = name.Substring(0, maxLength).Trim(' ', '.');
            return name.Length == 0 ? "无标题" : name;
        }

        static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public static string RenderNote(Dictionary<string, string> meta, List<Block> blocks,
                                        Dictionary<string, string> saved, string stem)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in saved)
            {
                var key = System.IO.Path.GetFileNameWithoutExtension(pair.Key);
                if (key.EndsWith("_original", StringComparison.OrdinalIgnoreCase))
                    key = key.Substring(0, key.Length - "_original".Length);
                lookup[pair.Key] = pair.Value;
                lookup[key] = pair.Value;
            }

            var title = meta.ContainsKey("title") ? meta["title"].Trim() : "";
            var body = new List<string>();
            var dropped = false;

            foreach (var block in blocks)
            {
                if (!block.Attachment)
                {
                    if (!dropped && title.Length > 0 &&
                        block.Value.StartsWith(title.Substring(0, Math.Min(30, title.Length)), StringComparison.Ordinal))
                    {
                        dropped = true;
                        continue;
                    }
                    body.Add(block.Value);
                    continue;
                }

                var key = System.IO.Path.GetFileNameWithoutExtension(block.Value);
                if (key.EndsWith("_original", StringComparison.OrdinalIgnoreCase))
                    key = key.Substring(0, key.Length - "_original".Length);
                string file;
                if (!lookup.TryGetValue(block.Value, out file) && !lookup.TryGetValue(key, out file)) continue;

                var relative = EncodePath("../assets/" + stem + "/" + file);
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                body.Add(ImageExt.Contains(ext)
                    ? "![" + AltText(System.IO.Path.GetFileNameWithoutExtension(file)) + "](" + relative + ")"
                    : "[" + AltText(file) + "](" + relative + ")");
            }

            // 去掉首尾空行，并把连续空行压成一个（与原笔记的视觉一致）
            while (body.Count > 0 && body[0].Length == 0) body.RemoveAt(0);
            while (body.Count > 0 && body[body.Count - 1].Length == 0) body.RemoveAt(body.Count - 1);
            var compact = new List<string>();
            foreach (var line in body)
            {
                if (line.Length == 0 && compact.Count > 0 && compact[compact.Count - 1].Length == 0) continue;
                compact.Add(line);
            }
            body = compact;

            if (body.Count == 0) body.Add("");

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("title: " + Quote(title));
            sb.AppendLine("created: " + (meta.ContainsKey("created") && meta["created"].Length > 0 ? meta["created"] : "null"));
            sb.AppendLine("modified: " + (meta.ContainsKey("modified") && meta["modified"].Length > 0 ? meta["modified"] : "null"));
            sb.AppendLine("folder: " + Quote(meta.ContainsKey("folder") ? meta["folder"] : ""));
            sb.AppendLine("tag: " + Quote(meta.ContainsKey("tag") ? meta["tag"] : ""));
            sb.AppendLine("note_id: " + Quote(meta.ContainsKey("note_id") ? meta["note_id"] : ""));
            sb.AppendLine("source: huawei-cloud-notepad");

            var files = new List<string>();
            if (meta.ContainsKey("files")) files.AddRange(meta["files"].Split(new[] { '\u0001' }, StringSplitOptions.RemoveEmptyEntries));
            if (files.Count == 0) sb.AppendLine("attachments: []");
            else
            {
                sb.AppendLine("attachments:");
                foreach (var file in files) sb.AppendLine("  - " + file);
            }
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# " + (title.Length > 0 ? title : "无标题"));
            foreach (var line in body) sb.AppendLine(line);
            return sb.ToString();
        }
    }
}
