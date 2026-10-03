using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexDreamSkinController
{
    internal sealed class RuntimeConnection
    {
        public bool Ready;
        public int Port;
        public string Code;
        public string Message;
        public string BrowserId;

        internal static bool MatchesSession(string recordedBrowserId, int recordedPort, RuntimeConnection current)
        {
            return current != null && current.Ready && !String.IsNullOrEmpty(recordedBrowserId) &&
                recordedPort == current.Port && String.Equals(recordedBrowserId, current.BrowserId, StringComparison.Ordinal);
        }

        internal static RuntimeConnection Discover(int preferredPort)
        {
            var ports = OwnedLoopbackPorts();
            ports.Sort(delegate(int a, int b) { return a == preferredPort ? -1 : b == preferredPort ? 1 : a.CompareTo(b); });
            RuntimeConnection last = null;
            foreach (int port in ports)
            {
                var result = Probe(port, true, delegate(string route) { return Read(port, route); });
                if (result.Ready) return result;
                if (last == null || result.Code == "no_targets") last = result;
            }
            return last ?? new RuntimeConnection { Port = preferredPort, Code = "no_endpoint",
                Message = "当前 Codex 未开放背景接口，无法在此会话热启用。控制器和壁纸库可正常使用。" };
        }

        // The owner is checked before reading an endpoint; response data alone
        // must never make an unrelated local browser eligible for injection.
        internal static RuntimeConnection Probe(int port, bool trustedOwner, Func<string, string> read)
        {
            var result = new RuntimeConnection { Port = port, Code = "invalid_endpoint", Message = "本机接口未通过身份验证。" };
            if (!trustedOwner) { result.Code = "untrusted_owner"; return result; }
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 1048576 };
                var version = json.DeserializeObject(read("/json/version")) as Dictionary<string, object>;
                object socket;
                Uri uri;
                if (version == null || !version.TryGetValue("webSocketDebuggerUrl", out socket) ||
                    !Uri.TryCreate(Convert.ToString(socket), UriKind.Absolute, out uri) || uri.Scheme != "ws" ||
                    uri.Host != "127.0.0.1" || uri.Port != port || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                    uri.Fragment.Length != 0 || !Regex.IsMatch(uri.AbsolutePath, @"^/devtools/browser/[A-Za-z0-9._-]{1,200}$")) return result;
                result.BrowserId = uri.AbsolutePath.Substring("/devtools/browser/".Length);
                var targets = json.DeserializeObject(read("/json/list")) as object[];
                if (targets != null)
                    foreach (object value in targets)
                    {
                        var target = value as Dictionary<string, object>;
                        object typeValue, urlValue, webSocketValue;
                        if (target == null || !target.TryGetValue("type", out typeValue) || !target.TryGetValue("url", out urlValue) ||
                            !target.TryGetValue("webSocketDebuggerUrl", out webSocketValue)) continue;
                        string type = Convert.ToString(typeValue);
                        Uri url, targetSocket;
                        if (type != "page" && type != "webview" && type != "iframe") continue;
                        if (!Uri.TryCreate(Convert.ToString(urlValue), UriKind.Absolute, out url) ||
                            !Uri.TryCreate(Convert.ToString(webSocketValue), UriKind.Absolute, out targetSocket)) continue;
                        bool trustedUrl = url.Scheme == "app" || (url.Scheme == "https" &&
                            (url.Host == "chatgpt.com" || url.Host.EndsWith(".chatgpt.com") || url.Host == "openai.com" || url.Host.EndsWith(".openai.com")));
                        if (!trustedUrl || targetSocket.Scheme != "ws" || targetSocket.Host != "127.0.0.1" ||
                            targetSocket.Port != port || targetSocket.UserInfo.Length != 0 || targetSocket.Query.Length != 0 ||
                            targetSocket.Fragment.Length != 0 || !Regex.IsMatch(targetSocket.AbsolutePath, @"^/devtools/(page|browser)/[A-Za-z0-9._-]{1,200}$")) continue;
                        result.Ready = true;
                        result.Code = "ready";
                        result.Message = "已发现 Codex 页面，可尝试热连接并验证背景。";
                        return result;
                    }
                result.Code = "no_targets";
                result.Message = "端口已开放，但没有可连接的 Codex 页面；不能应用背景。";
            }
            catch { result.Code = "protocol_error"; result.Message = "Codex 接口响应不可用或格式已变化。"; }
            return result;
        }

        private static string Read(int port, string route)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + route);
            request.Proxy = null;
            request.Timeout = 600;
            request.ReadWriteTimeout = 600;
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + count > 1048576) throw new InvalidDataException("Endpoint response too large");
                    memory.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }

        private static List<int> OwnedLoopbackPorts()
        {
            var result = new List<int>();
            var trusted = new Dictionary<int, bool>();
            IntPtr table = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
                if (size < 4 || size > 1048576) return result;
                table = Marshal.AllocHGlobal(size);
                if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return result;
                int count = Marshal.ReadInt32(table);
                for (int index = 0; index < count && 4 + (index + 1) * 24 <= size; index++)
                {
                    IntPtr row = IntPtr.Add(table, 4 + index * 24);
                    if (Marshal.ReadInt32(row, 0) != 2) continue; // MIB_TCP_STATE_LISTEN
                    uint address = unchecked((uint)Marshal.ReadInt32(row, 4));
                    if (!new IPAddress(address).Equals(IPAddress.Loopback)) continue;
                    uint networkPort = unchecked((uint)Marshal.ReadInt32(row, 8));
                    int port = (int)(((networkPort & 255) << 8) | ((networkPort >> 8) & 255));
                    int owner = Marshal.ReadInt32(row, 20);
                    bool allowed;
                    if (!trusted.TryGetValue(owner, out allowed))
                    {
                        allowed = false;
                        try
                        {
                            using (Process process = Process.GetProcessById(owner))
                                allowed = Regex.IsMatch(process.MainModule.FileName,
                                    @"\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(?:ChatGPT|Codex)\.exe$", RegexOptions.IgnoreCase);
                        }
                        catch { }
                        trusted[owner] = allowed;
                    }
                    if (allowed && port >= 1024 && !result.Contains(port)) result.Add(port);
                }
            }
            catch { }
            finally { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); }
            return result;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    }
}
