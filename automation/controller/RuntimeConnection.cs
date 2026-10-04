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
            string inventoryFailure;
            var ports = OwnedLoopbackPorts(out inventoryFailure);
            return DiscoverFromInventory(preferredPort, ports, inventoryFailure,
                delegate(int port) { return Probe(port, true, delegate(string route) { return Read(port, route); }); });
        }

        internal static RuntimeConnection DiscoverFromInventory(int preferredPort, List<int> ports,
            string inventoryFailure, Func<int, RuntimeConnection> probe)
        {
            ports.Sort(delegate(int a, int b) { return a == b ? 0 : a == preferredPort ? -1 : b == preferredPort ? 1 : a.CompareTo(b); });
            RuntimeConnection last = null;
            foreach (int port in ports)
            {
                var result = probe(port);
                if (result.Ready) return result;
                if (last == null || result.Code == "no_targets") last = result;
            }
            if (last == null && !String.IsNullOrEmpty(inventoryFailure))
                return new RuntimeConnection { Port = preferredPort, Code = inventoryFailure,
                    Message = inventoryFailure == "owner_check_unavailable"
                        ? "Codex 接口进程暂未能确认，正在重新检测。"
                        : "本机接口检测暂不可用，正在重新检测。" };
            return last ?? new RuntimeConnection { Port = preferredPort, Code = "no_endpoint",
                Message = "当前会话未开启热换肤连接。下次从桌面或开始菜单的 Codex 入口启动后，可随时启用或关闭皮肤；当前不会自动重启 Codex。" };
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

        private static List<int> OwnedLoopbackPorts(out string failure)
        {
            failure = null;
            var result = new List<int>();
            var trusted = new Dictionary<int, bool>();
            IntPtr table = IntPtr.Zero;
            try
            {
                int size = 0;
                uint firstRead = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
                if ((firstRead != 0 && firstRead != 122) || size < 4 || size > 1048576)
                { failure = "discovery_unavailable"; return result; }
                table = Marshal.AllocHGlobal(size);
                if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0)
                { failure = "discovery_unavailable"; return result; }
                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > (size - 4) / 24)
                { failure = "discovery_unavailable"; return result; }
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
                            {
                                string imagePath = ProcessImagePath(process);
                                if (String.IsNullOrEmpty(imagePath))
                                {
                                    if (process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                                        process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase)) failure = "owner_check_unavailable";
                                }
                                else allowed = IsRegisteredCodexImagePath(imagePath);
                            }
                        }
                        catch { }
                        trusted[owner] = allowed;
                    }
                    if (allowed && port >= 1024 && !result.Contains(port)) result.Add(port);
                }
            }
            catch { failure = "discovery_unavailable"; }
            finally { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); }
            return result;
        }

        internal static bool IsRegisteredCodexImagePath(string path)
        {
            return !String.IsNullOrEmpty(path) && Regex.IsMatch(path,
                @"\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(?:ChatGPT|Codex)\.exe$", RegexOptions.IgnoreCase);
        }

        private static string ProcessImagePath(Process process)
        {
            try { return process.MainModule.FileName; } catch { }
            // Image-path discovery does not need VM read access to the app.
            IntPtr handle = OpenProcess(0x1000, false, process.Id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var path = new System.Text.StringBuilder(32768);
                int length = path.Capacity;
                return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
