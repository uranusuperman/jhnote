using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace HwNote
{
    public class AuthException : Exception
    {
        public AuthException(string message) : base(message) { }
    }

    /// <summary>华为云空间备忘录 HTTP 接口（.NET Framework 内置 JSON）。</summary>
    public class Api
    {
        public const string BaseUrl = "https://cloud.huawei.com";
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                          "(KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36";

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        readonly string _cookie;

        public Api(string cookie)
        {
            if (string.IsNullOrEmpty(cookie)) throw new AuthException("缺少 cookie");
            _cookie = cookie;
        }

        public static string TraceId() { return Guid.NewGuid().ToString("N"); }

        public static Dictionary<string, object> Dict(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static List<object> List(object value)
        {
            var result = new List<object>();
            if (value == null) return result;
            if (value is string) return result;
            var sequence = value as System.Collections.IEnumerable;
            if (sequence != null)
            {
                foreach (var item in sequence) result.Add(item);
                return result;
            }
            return result;
        }

        public static string Str(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value);
        }

        public static long Num(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToInt64(value); } catch { return 0; }
        }

        HttpWebRequest Request(string path, string method)
        {
            var request = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            request.Method = method;
            request.UserAgent = UA;
            request.Accept = "application/json, text/plain, */*";
            request.Referer = BaseUrl + "/home";
            request.Headers["Cookie"] = _cookie;
            request.Headers["x-hw-client-mode"] = "frontend";
            request.Headers["x-hw-device-category"] = "Web";
            request.Headers["x-hw-device-type"] = "7";
            request.Headers["x-hw-os-brand"] = "Web";
            request.Timeout = 30000;
            return request;
        }

        public Dictionary<string, object> Post(string path, Dictionary<string, object> payload)
        {
            var body = Encoding.UTF8.GetBytes(Json.Serialize(payload));
            var request = Request(path, "POST");
            request.ContentType = "application/json;charset=UTF-8";
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();
                    var type = (response.ContentType ?? "").ToLowerInvariant();
                    if (type.IndexOf("json", StringComparison.Ordinal) < 0)
                        throw new AuthException("登录状态失效（服务器返回登录页），请重新 login");
                    try { return Json.Deserialize<Dictionary<string, object>>(text); }
                    catch (Exception) { throw new AuthException("接口返回异常，可能需要重新 login"); }
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && (int)response.StatusCode == 403)
                    throw new AuthException("会话失效或无权限（403），请重新 login");
                throw new Exception("网络错误：" + ex.Message);
            }
        }

        // ---- 业务接口 ----------------------------------------------------
        public List<Dictionary<string, object>> Notes()
        {
            var data = Post("/notepad/simplenote/query", new Dictionary<string, object>
            {
                { "guids", "" }, { "traceId", TraceId() }
            });
            var list = new List<Dictionary<string, object>>();
            foreach (var item in List(Dict(data["rspInfo"])["noteList"]))
                list.Add(Dict(item));
            return list;
        }

        public Dictionary<string, Dictionary<string, string>> Tags()
        {
            var result = new Dictionary<string, Dictionary<string, string>>();
            var cursor = "0";
            while (true)
            {
                var data = Post("/notepad/notetag/query", new Dictionary<string, object>
                {
                    { "startCursor", cursor }, { "count", 100 }, { "traceId", TraceId() }
                });
                var rsp = Dict(data["rspInfo"]);
                var items = new List<object>();
                items.AddRange(List(rsp.ContainsKey("noteList") ? rsp["noteList"] : null));
                items.AddRange(List(rsp.ContainsKey("backLoglist") ? rsp["backLoglist"] : null));
                if (items.Count == 0) break;

                foreach (var it in items)
                {
                    Dictionary<string, object> raw;
                    try { raw = Json.Deserialize<Dictionary<string, object>>(Str(Dict(it), "data")); }
                    catch { continue; }
                    var content = Dict(raw.ContainsKey("content") ? raw["content"] : null);
                    var uuid = Str(content, "uuid");
                    if (uuid.Length == 0) continue;

                    var folder = "";
                    try
                    {
                        var d3 = Json.Deserialize<Dictionary<string, object>>(Str(content, "data3"));
                        folder = Str(d3, "mFolderName");
                    }
                    catch { }
                    result[uuid] = new Dictionary<string, string>
                    {
                        { "name", Str(content, "name") }, { "folder", folder }
                    };
                }

                var next = Str(data, "startCursor");
                if (next.Length == 0 || next == cursor) break;
                cursor = next;
            }
            return result;
        }

        public string DetailHtml(string guid, string kind, out List<Dictionary<string, object>> attachments)
        {
            var data = Post("/notepad/note/query", new Dictionary<string, object>
            {
                { "ctagNoteInfo", "" }, { "startCursor", "0" },
                { "guid", guid }, { "kind", kind }, { "traceId", TraceId() }
            });
            var rsp = Dict(data["rspInfo"]);
            attachments = new List<Dictionary<string, object>>();
            foreach (var item in List(rsp.ContainsKey("attachments") ? rsp["attachments"] : null))
                attachments.Add(Dict(item));

            Dictionary<string, object> detail;
            var raw = rsp.ContainsKey("data") ? rsp["data"] : null;
            if (raw is Dictionary<string, object>) detail = (Dictionary<string, object>)raw;
            else
            {
                try { detail = Json.Deserialize<Dictionary<string, object>>(Convert.ToString(raw)); }
                catch { return ""; }
            }
            var content = Dict(detail.ContainsKey("content") ? detail["content"] : null);
            return Str(content, "html_content");
        }

        /// <summary>附件下载地址（网关要求路径整体做 URL 编码）。</summary>
        public static string AssetUrl(string kind, string guid, string assetId, string versionId)
        {
            var path = string.Format("/v2/dataSync/callback/v1/1001/kind/{0}/record/{1}/assets/{2}/revisions/{3}",
                                     kind, guid, assetId, versionId);
            return BaseUrl + "/proxy/v1/download/" + Uri.EscapeDataString(path);
        }
        /// <summary>探测附件下载返回码（不发完整请求体，用于验证会话是否已登记）。</summary>
        public int Probe(string kind, string guid, string assetId, string versionId)
        {
            var url = AssetUrl(kind, guid, assetId, versionId);
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe"),
                    "-s -o NUL -w \"%{http_code}\" --max-time 25 -H \"Cookie: " + _cookie +
                    "\" -H \"User-Agent: " + UA + "\" \"" + url + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    var text = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(30000);
                    int code;
                    return int.TryParse(text, out code) ? code : -1;
                }
            }
            catch { return -1; }
        }

        public byte[] Download(string kind, string guid, string assetId, string versionId)
        {
            var url = AssetUrl(kind, guid, assetId, versionId);

            // 系统自带 curl：对 %2F 编码路径的处理比 .NET Uri 可靠
            var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (File.Exists(curl))
            {
                string tmp;
                try { tmp = Path.Combine(Path.GetTempPath(), "jhnote_" + Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllBytes(tmp, new byte[0]); }
                catch { tmp = Path.Combine(Environment.CurrentDirectory, "jhnote_" + Guid.NewGuid().ToString("N") + ".tmp"); }
                try
                {
                    var args = "-s -S -o \"" + tmp + "\" -w \"%{http_code}\" -H \"Cookie: " + _cookie +
                               "\" -H \"User-Agent: " + UA + "\" \"" + url + "\"";
                    var psi = new ProcessStartInfo(curl, args)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        var code = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(60000);
                        var bytes = File.ReadAllBytes(tmp);
                        if (code == "200" && bytes.Length > 0) return bytes;
                        if (code == "403") throw new AuthException("附件下载被拒（会话未登记），请重新 login");
                        throw new Exception("附件下载失败[" + url + "]：HTTP " + code);
                    }
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }

            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.Cookie] = _cookie;
                client.Headers[HttpRequestHeader.UserAgent] = UA;
                client.Headers["x-hw-client-mode"] = "frontend";
                return client.DownloadData(url);
            }
        }
    }
}
