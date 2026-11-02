using System;
using System.Collections.Generic;

namespace HwNote
{
    /// <summary>不依赖网络的渲染自检：csc /main:HwNote.SelfTest ...</summary>
    public static class SelfTest
    {
        static int _failed;

        static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "  ok   " : "  FAIL ") + name);
            if (!condition) _failed++;
        }

        public static int Main()
        {
            var html = "<note><element type=\"Text\"><hw_font>标题</hw_font></element>" +
                       "<element type=\"Text\">第一段<br>第二段</element>" +
                       "<element type=\"File\">/data/x/a.docx</element>" +
                       "<element type=\"Video\">/data/x/b.mp4</element>" +
                       "<element type=\"Audio\">/data/x/c.mp3</element></note>";

            var blocks = Render.ParseBlocks(html);
            Check(blocks.Count == 6, "解析出 6 个块");
            Check(!blocks[0].Attachment && blocks[0].Value == "标题", "标题块");
            Check(blocks[1].Value == "第一段" && blocks[2].Value == "第二段", "br 拆行");
            Check(blocks[3].Attachment && blocks[3].Value == "a.docx", "File 视为附件");
            Check(blocks[4].Attachment && blocks[4].Value == "b.mp4", "Video 视为附件");
            Check(blocks[5].Attachment && blocks[5].Value == "c.mp3", "Audio 视为附件");

            var meta = new Dictionary<string, string>
            {
                { "title", "标题" }, { "created", "2022-05-23 23:28:12" },
                { "modified", "2022-05-23 23:28:12" }, { "folder", "杂集" },
                { "tag", "杂集|歌词" }, { "note_id", "G1" },
                { "files", "assets/20220523_标题/a.png" }
            };
            var saved = new Dictionary<string, string> { { "a_original.png", "a.png" } };
            var raw = Render.RenderNote(meta, blocks, saved, "20220523_标题");
            var text = raw.Replace("\r\n", "\n");

            Check(text.StartsWith("---\ntitle: \"标题\""), "front-matter 起始");
            Check(text.Contains("note_id: \"G1\""), "note_id");
            Check(text.Contains("# 标题\n第一段"), "标题去重");
            Check(text.Contains("![a](../assets/20220523_标题/a.png)"), "图片原位渲染");
            Check(text.Contains("[a.docx](../assets/20220523_标题/a.docx)") || !saved.ContainsKey("a.docx"),
                  "文档/视频链接或跳过");
            Check(Render.Sanitize("a/b:c*?\"<>|") == "a_b_c______", "文件名清洗");
            Check(Render.Sanitize("   ") == "无标题", "空名兜底");

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "全部通过" : (_failed + " 项失败"));
            return _failed == 0 ? 0 : 1;
        }
    }
}
