using System;
using System.Collections.Generic;
using System.IO;
using System.Text;


namespace HwNote
{
    public static class Paths
    {
        static string _dataDir;

        static IEnumerable<string> Candidates()
        {
            var env = Environment.GetEnvironmentVariable("HNNOTE_HOME");
            if (!string.IsNullOrEmpty(env)) yield return env;
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jhnote");
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        }

        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                foreach (var candidate in Candidates())
                {
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        _dataDir = candidate;
                        return candidate;
                    }
                    catch { }
                }
                _dataDir = Path.Combine(Path.GetTempPath(), "jhnote");
                Directory.CreateDirectory(_dataDir);
                return _dataDir;
            }
        }
    }

    public static class Program
    {
        public const string Version = "0.1.0";

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        /// <summary>winexe 没有控制台；命令行调用时挂到父控制台上，保证还能看到输出。</summary>
        static void AttachToConsole()
        {
            try
            {
                if (!AttachConsole(-1)) AllocConsole();
                var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
                Console.SetError(stderr);
            }
            catch { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try
            {
                // 云空间只接受 TLS 1.2：.NET Framework 的 TLS 1.3 会让部分服务端直接断开
                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                System.Net.ServicePointManager.Expect100Continue = false;
            }
            catch { }

            if (args.Length == 0)
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                System.Windows.Forms.Application.Run(new MainForm());
                return 0;
            }

            AttachToConsole();
            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args);

            try
            {
                switch (command)
                {
                    case "export": return ExportCommand(options, false);
                    case "all": return ExportCommand(options, true);
                    case "render": return RenderCommand(options);
                    default: Help(); return 0;
                }
            }
            catch (AuthException ex)
            {
                Console.Error.WriteLine("✗ " + ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("✗ " + ex.Message);
                return 1;
            }
        }

        static void PrepareUi()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        }

        /// <summary>会话只存在内存里：登录窗口关闭即失效，所以每次运行都要先登录。</summary>
        static int ExportCommand(Dictionary<string, string> options, bool renderHand)
        {
            var outDir = Path.GetFullPath(Option(options, "out", "export"));
            var match = Option(options, "match", "手写");
            var noMedia = options.ContainsKey("no-media");
            Console.WriteLine("登录后将导出到 " + outDir + (renderHand ? "，并渲染手写笔迹" : ""));

            PrepareUi();
            using (var form = new LoginForm(new LoginOptions
            {
                ExportDir = outDir,
                Match = match,
                NoMedia = noMedia,
                RenderHand = renderHand
            }))
            {
                System.Windows.Forms.Application.Run(form);
                return Report(form);
            }
        }

        static int RenderCommand(Dictionary<string, string> options)
        {
            var outDir = Path.GetFullPath(Option(options, "out", "render"));
            var match = Option(options, "match", "手写");
            var exportRoot = Option(options, "export");
            Console.WriteLine("渲染窗口即将打开，请扫码登录…");

            PrepareUi();
            using (var form = new LoginForm(new LoginOptions
            {
                Match = match,
                RenderOutDir = outDir,
                RenderRoot = exportRoot
            }))
            {
                System.Windows.Forms.Application.Run(form);
                return Report(form);
            }
        }

        static int Report(LoginForm form)
        {
            if (string.IsNullOrEmpty(form.ExportResult))
            {
                Console.Error.WriteLine("未完成登录或处理");
                return 2;
            }
            Console.WriteLine("完成：" + form.ExportResult);
            return 0;
        }
        static Dictionary<string, string> ParseOptions(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                var key = args[i].Substring(2);
                var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                result[key] = value;
            }
            return result;
        }

        static string Option(Dictionary<string, string> options, string key, string fallback = "")
        {
            return options.ContainsKey(key) ? options[key] : fallback;
        }

        static void Help()
        {
            Console.WriteLine("jhnote " + Version + " - 华为云空间备忘录 → Markdown");
            Console.WriteLine("（会话只保留在内存中，每次运行都需要扫码登录）");
            Console.WriteLine();
            Console.WriteLine("  jhnote.exe                            打开图形界面");
            Console.WriteLine("  jhnote.exe export --out DIR           登录后导出（--no-media / --match 关键字）");
            Console.WriteLine("  jhnote.exe all --out DIR              登录 + 导出 + 渲染手写笔迹");
            Console.WriteLine("  jhnote.exe render --out DIR [--export DIR]   只渲染手写笔迹");
        }
    }
}
