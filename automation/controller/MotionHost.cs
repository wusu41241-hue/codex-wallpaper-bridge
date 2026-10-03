using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexDreamSkinController
{
    // A small local media source for Codex's wallpaper layer. Video files are
    // streamed from their original Workshop location. Scene/web wallpapers are
    // rendered by Wallpaper Engine in an off-screen pop-out and captured live.
    internal sealed class MotionHost
    {
        internal const int Port = 47866;
        private const string WindowName = "CodexDreamSkinMotion";
        private readonly ControllerService service;
        private readonly string token;
        private readonly object sourceLock = new object();
        private MotionSource source;
        private IntPtr sceneWindow = IntPtr.Zero;
        private uint sceneOwnerPid;
        private byte[] latestFrame;
        private int frameVersion;
        private int sceneClients;
        private int dirty = 1;
        private volatile bool shuttingDown;
        private DateTime lastClientUtc = DateTime.UtcNow;
        private DateTime lastFrameRequestUtc = DateTime.MinValue;
        private DateTime lastSceneLaunchUtc = DateTime.MinValue;
        private DateTime sceneOpenedUtc = DateTime.MinValue;

        private MotionHost(ControllerService service)
        {
            this.service = service;
            string path = Path.Combine(service.WallpaperControlRoot, "motion-token.txt");
            token = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
            if (!Regex.IsMatch(token, "^[a-f0-9]{64}$")) throw new InvalidOperationException("Motion host token is missing");
        }

        internal static int Run(ControllerService service, string[] args)
        {
            int parentPid = 0;
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (args[index] == "--parent-pid") Int32.TryParse(args[index + 1], out parentPid);
            }
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            using (Mutex mutex = new Mutex(false, "Local\\CodexDreamSkin.MotionHost." + sid))
            {
                bool owns = false;
                try
                {
                    try { owns = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns) return 0;
                    return new MotionHost(service).RunLoop(parentPid);
                }
                catch (Exception error) { service.Log("motion host: " + error.Message); return 2; }
                finally { if (owns) { try { mutex.ReleaseMutex(); } catch { } } }
            }
        }

        private int RunLoop(int parentPid)
        {
            string active = Path.Combine(service.StateRoot, "active-theme");
            Directory.CreateDirectory(active);
            using (FileSystemWatcher watcher = new FileSystemWatcher(active, "theme.json"))
            {
                FileSystemEventHandler changed = delegate { Interlocked.Exchange(ref dirty, 1); };
                RenamedEventHandler renamed = delegate { Interlocked.Exchange(ref dirty, 1); };
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.EnableRaisingEvents = true;
                TcpListener listener = new TcpListener(IPAddress.Loopback, Port);
                try
                {
                    listener.Start(8);
                    IsolateSocket(listener.Server);
                    service.Log("motion host listening on loopback");
                    // Loading/capturing a complex wallpaper can block the render
                    // loop. Keep health and frame requests responsive independently.
                    var acceptThread = new Thread(delegate() {
                        while (!shuttingDown)
                        {
                            try {
                                TcpClient client = listener.AcceptTcpClient();
                                ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                            }
                            catch (SocketException) { if (shuttingDown) return; }
                            catch (ObjectDisposedException) { return; }
                        }
                    });
                    acceptThread.IsBackground = true;
                    acceptThread.Start();
                    DateTime lastParentCheck = DateTime.UtcNow;
                    DateTime lastCapture = DateTime.MinValue;
                    while (true)
                    {
                        if (Interlocked.Exchange(ref dirty, 0) != 0) ReloadSource();
                        MotionSource current = CurrentSource();
                        if (current != null && (Volatile.Read(ref sceneClients) > 0 || (DateTime.UtcNow - lastFrameRequestUtc).TotalSeconds < 2) &&
                            (DateTime.UtcNow - lastCapture).TotalMilliseconds >= 95)
                        {
                            EnsureSceneWindow();
                            CaptureSceneFrame();
                            lastCapture = DateTime.UtcNow;
                        }
                        if (sceneWindow != IntPtr.Zero && Volatile.Read(ref sceneClients) == 0 &&
                            (DateTime.UtcNow - lastClientUtc).TotalSeconds > 30) CloseSceneWindow();
                        if ((DateTime.UtcNow - lastParentCheck).TotalSeconds >= 5)
                        {
                            lastParentCheck = DateTime.UtcNow;
                            if (parentPid > 0 && !InjectorAlive(parentPid))
                            {
                                int replacement = RecordedInjectorPid();
                                if (replacement > 0 && replacement != parentPid && InjectorAlive(replacement)) parentPid = replacement;
                                else return 0;
                            }
                        }
                        Thread.Sleep(35);
                    }
                }
                finally { shuttingDown = true; listener.Stop(); CloseSceneWindow(); }
            }
        }

        private static bool InjectorAlive(int pid)
        {
            try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited && process.ProcessName.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        private int RecordedInjectorPid()
        {
            try
            {
                string text = File.ReadAllText(Path.Combine(service.StateRoot, "state.json"), Encoding.UTF8);
                Dictionary<string, object> state = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                object value;
                int pid;
                return state != null && state.TryGetValue("injectorPid", out value) && Int32.TryParse(Convert.ToString(value), out pid) ? pid : 0;
            }
            catch { return 0; }
        }

        private MotionSource CurrentSource() { lock (sourceLock) return source; }

        private void ReloadSource()
        {
            try
            {
                string file = Path.Combine(service.StateRoot, "active-theme", "theme.json");
                Dictionary<string, object> theme = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                object motionValue;
                Dictionary<string, object> motion = theme != null && theme.TryGetValue("motion", out motionValue)
                    ? motionValue as Dictionary<string, object> : null;
                string kind = motion != null && motion.ContainsKey("kind") ? Convert.ToString(motion["kind"]) : "";
                string path = motion != null && motion.ContainsKey("source") ? Convert.ToString(motion["source"]) : "";
                MotionSource next = null;
                if ((kind == "video" || kind == "scene" || kind == "web") && WallpaperCatalog.IsAllowedSource(path))
                {
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if ((kind == "video" && (extension == ".mp4" || extension == ".webm" || extension == ".m4v")) ||
                        (kind != "video" && Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase)))
                    {
                        object propertiesValue;
                        var properties = motion.TryGetValue("properties", out propertiesValue) ? propertiesValue as Dictionary<string, object> : null;
                        next = new MotionSource { Kind = kind, Path = Path.GetFullPath(path), Properties = PropertiesJson(properties) };
                    }
                }
                MotionSource previous = CurrentSource();
                if (previous != null && (next == null || previous.Kind != next.Kind || previous.Properties != next.Properties || !String.Equals(previous.Path, next.Path, StringComparison.OrdinalIgnoreCase)))
                    CloseSceneWindow();
                lock (sourceLock) source = next;
                if (next != null) service.Log("motion source: " + next.Kind);
            }
            catch (IOException) { Interlocked.Exchange(ref dirty, 1); }
            catch (Exception error) { service.Log("motion source invalid: " + error.GetType().Name); }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 10000;
                try
                {
                    NetworkStream stream = client.GetStream();
                    StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 2048, true);
                    string request = reader.ReadLine();
                    if (String.IsNullOrEmpty(request) || request.Length > 1024) return;
                    string[] parts = request.Split(' ');
                    if (parts.Length != 3 || parts[2] != "HTTP/1.1") return;
                    string host = "";
                    string range = "";
                    for (int index = 0; index < 32; index++)
                    {
                        string line = reader.ReadLine();
                        if (line == null || line.Length > 2048) return;
                        if (line.Length == 0) break;
                        if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line.Substring(5).Trim();
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
                    }
                    if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) { Reply(stream, 403, "Forbidden"); return; }
                    string route = parts[1].Split('?')[0];
                    if (parts[0] == "GET" && route == "/health") { Reply(stream, 200, "codex-dream-skin-motion/1"); return; }
                    Match credential = Regex.Match(parts[1], @"(?:\?|&)t=([a-f0-9]{64})(?:&|$)");
                    if (!credential.Success || !String.Equals(credential.Groups[1].Value, token, StringComparison.Ordinal))
                    { Reply(stream, 403, "Forbidden"); return; }
                    if (parts[0] == "OPTIONS" && (route == "/video" || route == "/scene" || route == "/frame"))
                    { ReplyOptions(stream); return; }
                    MotionSource current = CurrentSource();
                    if (current == null) { Reply(stream, 404, "No active motion theme"); return; }
                    if (parts[0] == "GET" && route == "/frame") { ServeFrame(stream, current); return; }
                    if ((parts[0] == "GET" || parts[0] == "HEAD") && route == "/video" && current.Kind == "video")
                    { ServeVideo(stream, current, range, parts[0] == "HEAD"); return; }
                    if (parts[0] == "GET" && route == "/scene" && current.Kind != "video")
                    { ServeScene(stream, current); return; }
                    Reply(stream, 404, "Not found");
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (Exception error) { service.Log("motion request: " + error.GetType().Name); }
            }
        }

        private static void ServeVideo(NetworkStream stream, MotionSource current, string range, bool head)
        {
            using (FileStream file = new FileStream(current.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long start = 0;
                long end = file.Length - 1;
                Match match = Regex.Match(range ?? "", @"^bytes=(\d+)-(\d*)$", RegexOptions.IgnoreCase);
                bool partial = match.Success;
                if (partial)
                {
                    start = Int64.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (match.Groups[2].Success && match.Groups[2].Value.Length > 0)
                        end = Math.Min(end, Int64.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                    if (start > end || start >= file.Length) { Reply(stream, 416, "Range not satisfiable"); return; }
                }
                string mime = Path.GetExtension(current.Path).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";
                StringBuilder header = new StringBuilder();
                header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                header.Append("Content-Type: ").Append(mime).Append("\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\n");
                header.Append("Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Private-Network: true\r\nCross-Origin-Resource-Policy: cross-origin\r\n");
                if (partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(file.Length).Append("\r\n");
                header.Append("Content-Length: ").Append(end - start + 1).Append("\r\nConnection: close\r\n\r\n");
                byte[] prefix = Encoding.ASCII.GetBytes(header.ToString());
                stream.Write(prefix, 0, prefix.Length);
                if (head) return;
                file.Position = start;
                byte[] buffer = new byte[65536];
                long remaining = end - start + 1;
                while (remaining > 0)
                {
                    int count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count <= 0) break;
                    stream.Write(buffer, 0, count);
                    remaining -= count;
                }
            }
        }

        private void ServeScene(NetworkStream stream, MotionSource expected)
        {
            Interlocked.Increment(ref sceneClients);
            lastClientUtc = DateTime.UtcNow;
            try
            {
                byte[] prefix = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=dreamframe\r\n" +
                    "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Private-Network: true\r\n" +
                    "Cross-Origin-Resource-Policy: cross-origin\r\nConnection: close\r\n\r\n");
                stream.Write(prefix, 0, prefix.Length);
                int sent = -1;
                while (CurrentSource() == expected)
                {
                    int version = Volatile.Read(ref frameVersion);
                    byte[] frame = latestFrame;
                    if (frame == null || sent == version) { Thread.Sleep(55); continue; }
                    byte[] header = Encoding.ASCII.GetBytes("--dreamframe\r\nContent-Type: image/jpeg\r\nContent-Length: " + frame.Length + "\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.WriteByte(13); stream.WriteByte(10);
                    sent = version;
                }
            }
            finally { Interlocked.Decrement(ref sceneClients); lastClientUtc = DateTime.UtcNow; }
        }

        private void ServeFrame(NetworkStream stream, MotionSource expected)
        {
            lastFrameRequestUtc = DateTime.UtcNow;
            lastClientUtc = DateTime.UtcNow;
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            byte[] frame = latestFrame;
            while (frame == null && CurrentSource() == expected && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(40);
                frame = latestFrame;
            }
            if (frame == null || CurrentSource() != expected) { Reply(stream, 404, "Wallpaper frame is not ready"); return; }
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nCache-Control: no-store\r\nContent-Length: " + frame.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(frame, 0, frame.Length);
        }

        private void EnsureSceneWindow()
        {
            if (sceneWindow != IntPtr.Zero && IsWindow(sceneWindow))
            {
                if (!ParkSceneWindow()) CloseSceneWindow();
                return;
            }
            if ((DateTime.UtcNow - lastSceneLaunchUtc).TotalSeconds < 5) return;
            lastSceneLaunchUtc = DateTime.UtcNow;
            MotionSource current = CurrentSource();
            if (current == null || !File.Exists(WallpaperCatalog.EngineExecutable)) return;
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = WallpaperCatalog.EngineExecutable;
            Point parking = CaptureParkingPoint(PhysicalDesktopBounds());
            info.Arguments = "-control openWallpaper -file \"" + current.Path.Replace("\"", "") + "\" -playInWindow " +
                WindowName + " -width 1600 -height 900 -x " + parking.X.ToString(CultureInfo.InvariantCulture) +
                " -y " + parking.Y.ToString(CultureInfo.InvariantCulture) + " -borderless";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process control = Process.Start(info)) control.WaitForExit(5000);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                IntPtr candidate = FindWindow(null, WindowName);
                if (candidate != IntPtr.Zero)
                {
                    try {
                        uint pid;
                        GetWindowThreadProcessId(candidate, out pid);
                        using (Process owner = Process.GetProcessById((int)pid))
                        {
                            if (!String.Equals(owner.MainModule.FileName, WallpaperCatalog.EngineExecutable, StringComparison.OrdinalIgnoreCase)) return;
                        }
                        sceneWindow = candidate;
                        sceneOwnerPid = pid;
                        if (!ParkSceneWindow())
                        {
                            service.Log("wallpaper capture window could not be isolated; closing it");
                            CloseSceneWindow();
                            return;
                        }
                        sceneOpenedUtc = DateTime.UtcNow;
                        ProcessStartInfo mute = new ProcessStartInfo(WallpaperCatalog.EngineExecutable,
                            "-control applyProperties -location " + WindowName + " -properties RAW~(" + current.Properties + ")~END");
                        mute.UseShellExecute = false; mute.CreateNoWindow = true; mute.WindowStyle = ProcessWindowStyle.Hidden;
                        using (Process control = Process.Start(mute)) control.WaitForExit(3000);
                        return;
                    }
                    catch { }
                }
                Thread.Sleep(100);
            }
        }

        private void CaptureSceneFrame()
        {
            if (sceneWindow == IntPtr.Zero || !IsWindow(sceneWindow)) return;
            if (!ParkSceneWindow()) { CloseSceneWindow(); return; }
            if ((DateTime.UtcNow - sceneOpenedUtc).TotalMilliseconds < 500) return;
            RECT rectangle;
            if (!GetWindowRect(sceneWindow, out rectangle)) return;
            int width = rectangle.Right - rectangle.Left;
            int height = rectangle.Bottom - rectangle.Top;
            if (width < 160 || height < 90 || width > 3840 || height > 2160) return;
            using (Bitmap bitmap = new Bitmap(width, height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                bool captured;
                try { captured = PrintWindow(sceneWindow, dc, 2); }
                finally { graphics.ReleaseHdc(dc); }
                if (!captured) return;
                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Jpeg);
                    latestFrame = memory.ToArray();
                    Interlocked.Increment(ref frameVersion);
                }
            }
        }

        private void CloseSceneWindow()
        {
            if (sceneWindow == IntPtr.Zero) return;
            sceneWindow = IntPtr.Zero;
            sceneOwnerPid = 0;
            latestFrame = null;
            if (!File.Exists(WallpaperCatalog.EngineExecutable)) return;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(WallpaperCatalog.EngineExecutable,
                    "-control closeWallpaper -location " + WindowName);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process control = Process.Start(info)) control.WaitForExit(5000);
            }
            catch { }
        }

        internal static Point CaptureParkingPoint(Rectangle desktop)
        {
            if (desktop.Width <= 0 || desktop.Height <= 0) throw new InvalidOperationException("Desktop bounds are unavailable");
            return new Point(checked(desktop.Right + 128), desktop.Top);
        }

        private static Rectangle PhysicalDesktopBounds()
        {
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }

        // Wallpaper Engine may clamp/restore the CLI coordinates. Enforce native
        // placement after creation and before every frame, including monitor/DPI changes.
        private bool ParkSceneWindow()
        {
            if (sceneWindow == IntPtr.Zero || FindWindow(null, WindowName) != sceneWindow) return false;
            uint owner;
            GetWindowThreadProcessId(sceneWindow, out owner);
            if (owner == 0 || owner != sceneOwnerPid) return false;
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                Rectangle desktop = new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
                Point parking = CaptureParkingPoint(desktop);
                RECT before;
                if (!GetWindowRect(sceneWindow, out before)) return false;
                long style = GetWindowLongPtr(sceneWindow, -20).ToInt64();
                long desired = (style | 0x80L | 0x08000000L) & ~0x40000L; // TOOLWINDOW, NOACTIVATE, not APPWINDOW
                bool changedStyle = desired != style || (style & 0x8L) != 0;
                if (changedStyle) SetWindowLongPtr(sceneWindow, -20, new IntPtr(desired));
                if (changedStyle || before.Left != parking.X || before.Top != parking.Y)
                {
                    if (!SetWindowPos(sceneWindow, new IntPtr(-2), parking.X, parking.Y, 0, 0, 0x1 | 0x10 | 0x20)) return false;
                }
                RECT after;
                if (!GetWindowRect(sceneWindow, out after)) return false;
                long actualStyle = GetWindowLongPtr(sceneWindow, -20).ToInt64();
                return after.Left >= desktop.Right && (actualStyle & 0x80L) != 0 &&
                    (actualStyle & 0x08000000L) != 0 && (actualStyle & (0x40000L | 0x8L)) == 0;
            }
            catch { return false; }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }

        private static void Reply(NetworkStream stream, int status, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            string label = status == 200 ? "OK" : status == 403 ? "Forbidden" : status == 416 ? "Range Not Satisfiable" : "Not Found";
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + label + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }

        private static void ReplyOptions(NetworkStream stream)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\n" +
                "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Range\r\nAccess-Control-Allow-Private-Network: true\r\n" +
                "Content-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
        }

        internal static string PropertiesJson(Dictionary<string, object> preset)
        {
            var properties = new Dictionary<string, object>();
            if (preset != null)
                foreach (var entry in preset)
                    if (entry.Value != null && !(entry.Value is Dictionary<string, object>) && !(entry.Value is object[]))
                        properties[entry.Key] = entry.Value;
            properties["volume"] = 0;
            string json = new JavaScriptSerializer().Serialize(properties).Replace(")~END", "\\u0029~END");
            if (json.Length > 20000) throw new InvalidOperationException("Wallpaper preset settings are too large.");
            return json;
        }

        internal static void IsolateSocket(Socket socket)
        {
            // .NET Framework can pass inheritable Winsock handles to WPE. If
            // the media host exits, the child then leaves a listener with a dead
            // owner PID. Clear inheritance before launching any child process.
            if (!SetHandleInformation(socket.Handle, 1, 0))
                throw new InvalidOperationException("Could not isolate the local media socket: " + Marshal.GetLastWin32Error());
        }

        private sealed class MotionSource { public string Kind; public string Path; public string Properties; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }
}
