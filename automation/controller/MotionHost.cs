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
        private readonly string WindowName = "CodexDreamSkinMotion-" + Guid.NewGuid().ToString("N").Substring(0,8);
        private string enginePath;
        private readonly ControllerService service;
        private readonly string token;
        private readonly object sourceLock = new object();
        private MotionSource source;
        private IntPtr sceneWindow = IntPtr.Zero;
        private uint sceneOwnerPid;
        private FrameSnapshot latestFrame;
        private CaptureBuffer captureBuffer;
        private readonly CaptureModePolicy captureMode = new CaptureModePolicy();
        private readonly string frameEpoch = Guid.NewGuid().ToString("N");
        private long frameSequence;
        private long captureTicksTotal;
        internal const int TargetFramesPerSecond = 30;
        private int sceneClients;
        private int dirty = 1;
        private volatile bool shuttingDown;
        private volatile bool safetySuspended;
        private int activeRequests;
        private DateTime lastClientUtc = DateTime.UtcNow;
        private DateTime lastFrameRequestUtc = DateTime.MinValue;
        private DateTime lastSceneLaunchUtc = DateTime.MinValue;
        private DateTime sceneOpenedUtc = DateTime.MinValue;
        private DateTime lastEngineCheckUtc = DateTime.MinValue;
        private bool scenePropertiesApplied;

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
                                if (Interlocked.Increment(ref activeRequests) > 8) { Interlocked.Decrement(ref activeRequests); client.Close(); continue; }
                                ThreadPool.QueueUserWorkItem(delegate { try { HandleClient(client); } finally { Interlocked.Decrement(ref activeRequests); } });
                            }
                            catch (SocketException) { if (shuttingDown) return; }
                            catch (ObjectDisposedException) { return; }
                        }
                    });
                    acceptThread.IsBackground = true;
                    acceptThread.Start();
                    DateTime lastParentCheck = DateTime.UtcNow;
                    long nextCaptureAt = 0;
                    while (true)
                    {
                        if (Interlocked.Exchange(ref dirty, 0) != 0) ReloadSource();
                        MotionSource current = CurrentSource();
                        bool captureActive = !safetySuspended && !File.Exists(Path.Combine(service.StateRoot, "paused")) &&
                            current != null && current.Kind != "video" && (Volatile.Read(ref sceneClients) > 0 || (DateTime.UtcNow - lastFrameRequestUtc).TotalSeconds < 2);
                        long nowTicks = Stopwatch.GetTimestamp();
                        if (captureActive && nowTicks >= nextCaptureAt)
                        {
                            long startedAt = nowTicks;
                            EnsureSceneWindow();
                            CaptureSceneFrame();
                            nextCaptureAt = startedAt + Math.Max(1, Stopwatch.Frequency / TargetFramesPerSecond);
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
                        Thread.Sleep(captureActive ? CaptureDelayMilliseconds(nextCaptureAt, Stopwatch.GetTimestamp()) : 35);
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
            string requestedPath = "";
            try
            {
                string file = Path.Combine(service.StateRoot, "active-theme", "theme.json");
                Dictionary<string, object> theme = WallpaperSafety.ReadProject(file);
                object motionValue;
                Dictionary<string, object> motion = theme != null && theme.TryGetValue("motion", out motionValue)
                    ? motionValue as Dictionary<string, object> : null;
                string kind = motion != null && motion.ContainsKey("kind") ? Convert.ToString(motion["kind"]) : "";
                string path = motion != null && motion.ContainsKey("source") ? Convert.ToString(motion["source"]) : "";
                requestedPath = path;
                MotionSource next = null;
                if ((kind == "video" || kind == "scene" || kind == "web") && WallpaperCatalog.IsAllowedSource(path))
                {
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if ((kind == "video" && (extension == ".mp4" || extension == ".webm" || extension == ".m4v")) ||
                        (kind != "video" && Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase)))
                    {
                        object propertiesValue;
                        var properties = motion.TryGetValue("properties", out propertiesValue) ? propertiesValue as Dictionary<string, object> : null;
                        next = new MotionSource { Kind = kind, Path = Path.GetFullPath(path),
                            Properties = PropertiesJson(WallpaperSafety.NormalizeProperties(path, properties)) };
                        WallpaperSafety.PropertyBatches(next.Properties);
                    }
                }
                if (motion != null && next == null) throw new InvalidOperationException("动态壁纸类型或本地资源路径未通过安全检查。");
                UpdateValidatedSource(next);
            }
            catch (IOException) { Interlocked.Exchange(ref dirty, 1); }
            catch (Exception error) { SuspendSourceForSafety("invalid-source", error.Message, requestedPath); }
        }

        // ReloadSource applies the fixed library whitelist and supported-file
        // checks before this transition. Keeping the transition independent of
        // file parsing lets its playback/cache behavior be verified in memory.
        internal void UpdateValidatedSource(MotionSource next)
        {
            MotionSource previous = CurrentSource();
            if (SameSource(previous, next)) return;
            bool wasSuspended = safetySuspended;
            if (previous != null) CloseSceneWindow();
            if (!wasSuspended && safetySuspended) return;
            lock (sourceLock) source = next;
            safetySuspended = next != null && WallpaperSafety.BlockedMessage(service.StateRoot,next.Path) != null;
            lastSceneLaunchUtc = DateTime.MinValue;
            if (safetySuspended) {
                File.WriteAllText(Path.Combine(service.StateRoot,"paused"),"paused by persistent wallpaper safety guard",new UTF8Encoding(false));
            }
            if (next != null) service.Log("motion source: " + next.Kind);
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
                    int headerBudget = 8192;
                    string request = ReadBoundedLine(stream, 1024, ref headerBudget);
                    if (String.IsNullOrEmpty(request) || request.Length > 1024) return;
                    string[] parts = request.Split(' ');
                    if (parts.Length != 3 || parts[2] != "HTTP/1.1") return;
                    string host = "";
                    string range = "";
                    for (int index = 0; index < 32; index++)
                    {
                        string line = ReadBoundedLine(stream, 2048, ref headerBudget);
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
                    if (parts[0] == "GET" && route == "/metrics") { ServeMetrics(stream); return; }
                    if (safetySuspended || File.Exists(Path.Combine(service.StateRoot,"paused"))) { Reply(stream,503,"Wallpaper playback paused"); return; }
                    MotionSource current = CurrentSource();
                    if (current == null) { Reply(stream, 404, "No active motion theme"); return; }
                    if (parts[0] == "GET" && route == "/frame")
                    {
                        Match after = Regex.Match(parts[1], @"(?:\?|&)after=([^&]*)(?:&|$)");
                        if (after.Success && !IsFrameId(after.Groups[1].Value)) { Reply(stream, 400, "Invalid frame identifier"); return; }
                        ServeFrame(stream, current, after.Success ? after.Groups[1].Value : null); return;
                    }
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
                string sent = null;
                while (!shuttingDown && !safetySuspended && !File.Exists(Path.Combine(service.StateRoot,"paused")) && CurrentSource() == expected)
                {
                    FrameSnapshot snapshot = Volatile.Read(ref latestFrame);
                    if (!UsableFrame(snapshot, expected, sent)) { Thread.Sleep(10); continue; }
                    byte[] frame = snapshot.Bytes;
                    byte[] header = Encoding.ASCII.GetBytes("--dreamframe\r\nContent-Type: image/jpeg\r\nContent-Length: " + frame.Length + "\r\n\r\n");
                    if (CurrentSource() != expected) return;
                    stream.Write(header, 0, header.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.WriteByte(13); stream.WriteByte(10);
                    sent = snapshot.Id;
                }
            }
            finally { Interlocked.Decrement(ref sceneClients); lastClientUtc = DateTime.UtcNow; }
        }

        private void ServeFrame(NetworkStream stream, MotionSource expected, string after)
        {
            lastFrameRequestUtc = DateTime.UtcNow;
            lastClientUtc = DateTime.UtcNow;
            long firstFrameDeadline = Stopwatch.GetTimestamp() + 6 * Stopwatch.Frequency;
            long updatedFrameDeadline = 0;
            FrameSnapshot snapshot;
            while (true)
            {
                if (shuttingDown || CurrentSource() != expected) { Reply(stream, 404, "Wallpaper frame is not ready"); return; }
                snapshot = Volatile.Read(ref latestFrame);
                if (UsableFrame(snapshot, expected, after)) break;
                long nowTicks = Stopwatch.GetTimestamp();
                if (snapshot != null && snapshot.Source == expected && snapshot.Id == after)
                {
                    if (updatedFrameDeadline == 0) updatedFrameDeadline = nowTicks + Stopwatch.Frequency * 45 / 1000;
                    if (nowTicks >= updatedFrameDeadline) { ReplyNoContent(stream); return; }
                    Thread.Sleep(3);
                }
                else
                {
                    if (nowTicks >= firstFrameDeadline) { Reply(stream, 404, "Wallpaper frame is not ready"); return; }
                    Thread.Sleep(20);
                }
            }
            if (CurrentSource() != expected) { Reply(stream, 404, "Wallpaper frame is not ready"); return; }
            byte[] frame = snapshot.Bytes;
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nCache-Control: no-store\r\nX-Dream-Frame-Id: " + snapshot.Id + "\r\nContent-Length: " + frame.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(frame, 0, frame.Length);
        }

        private void ServeMetrics(NetworkStream stream)
        {
            FrameSnapshot snapshot = Volatile.Read(ref latestFrame);
            byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "totalFrames", Interlocked.Read(ref frameSequence) },
                { "captureMsLast", snapshot == null ? 0 : snapshot.CaptureTicks * 1000.0 / Stopwatch.Frequency },
                { "captureMsTotal", Interlocked.Read(ref captureTicksTotal) * 1000.0 / Stopwatch.Frequency },
                { "frameBytes", snapshot == null ? 0 : snapshot.Bytes.Length },
                { "width", snapshot == null ? 0 : snapshot.Width },
                { "height", snapshot == null ? 0 : snapshot.Height },
                { "captureMode", snapshot == null ? "none" : snapshot.ClientArea ? "client" : "window" },
                { "targetFps", TargetFramesPerSecond }
                ,{ "safetySuspended", safetySuspended }
            }));
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }

        private void EnsureSceneWindow()
        {
            if (safetySuspended) return;
            if (sceneWindow != IntPtr.Zero && IsWindow(sceneWindow))
            {
                if ((DateTime.UtcNow-lastEngineCheckUtc).TotalSeconds >= 1) {
                    lastEngineCheckUtc=DateTime.UtcNow;
                    if (!EngineRunning() || !OwnedSceneWindow(sceneWindow,sceneOwnerPid)) { SuspendForSafety("engine-exited", "Wallpaper Engine 或渲染窗口意外退出，已停止该壁纸并禁止自动重试。"); return; }
                }
                if (!ParkSceneWindow()) SuspendForSafety("isolation-failed", "壁纸辅助窗口隔离检查失败，已停止自动重试。");
                return;
            }
            if (sceneWindow != IntPtr.Zero) { SuspendForSafety("renderer-exited", "壁纸窗口意外退出，已停止对该壁纸重复启动。"); return; }
            if ((DateTime.UtcNow - lastSceneLaunchUtc).TotalSeconds < 5) return;
            lastSceneLaunchUtc = DateTime.UtcNow;
            MotionSource current = CurrentSource();
            if (current == null) return;
            enginePath = WallpaperCatalog.EngineExecutable;
            if (!WallpaperSafety.IsRegularLocalFile(enginePath)) { SuspendForSafety("engine-unavailable", "Wallpaper Engine 程序未通过本地文件检查，已停止命令。"); return; }
            if (!EngineRunning()) { SuspendForSafety("engine-unavailable", "Wallpaper Engine 未运行，已停止自动启动命令。"); return; }
            if (HasPreviousHelper()) { SuspendSourceForSafety("orphan-window", "检测到旧皮肤辅助窗口仍在运行，已暂停新窗口启动，防止叠加壁纸。", ""); return; }
            if (FindWindow(null,WindowName) != IntPtr.Zero) { SuspendForSafety("window-collision", "壁纸辅助窗口标识冲突，已停止启动。"); return; }
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = enginePath;
            Point parking = CaptureParkingPoint(PhysicalDesktopBounds());
            info.Arguments = "-control openWallpaper -file " + WallpaperSafety.QuoteArgument(current.Path) + " -playInWindow " +
                WallpaperSafety.QuoteArgument(WindowName) + " -width 1600 -height 900 -x " + parking.X.ToString(CultureInfo.InvariantCulture) +
                " -y " + parking.Y.ToString(CultureInfo.InvariantCulture) + " -borderless";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            if (!RunEngineCommand(info, 5000)) { SuspendForSafety("open-command-failed", "Wallpaper Engine 打开命令失败或超时，已停止重试。"); return; }
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
                            if (!String.Equals(owner.MainModule.FileName, enginePath, StringComparison.OrdinalIgnoreCase)) { SuspendForSafety("window-identity-failed", "壁纸窗口归属未通过验证，已停止重试。"); return; }
                        }
                        sceneWindow = candidate;
                        sceneOwnerPid = pid;
                        captureMode.Reset();
                        if (!ParkSceneWindow())
                        {
                            service.Log("wallpaper capture window could not be isolated; closing it");
                            SuspendForSafety("isolation-failed", "壁纸辅助窗口未能安全停靠，已停止重试。");
                            return;
                        }
                        sceneOpenedUtc = DateTime.UtcNow;
                        scenePropertiesApplied=false;
                        lastEngineCheckUtc=DateTime.UtcNow;
                        return;
                    }
                    catch { SuspendForSafety("window-identity-failed", "壁纸窗口的进程身份无法确认，已停止重试。"); return; }
                }
                Thread.Sleep(100);
            }
            SuspendForSafety("window-timeout", "壁纸窗口未在规定时间内就绪，已停止自动重试。");
        }

        private void CaptureSceneFrame()
        {
            if (sceneWindow == IntPtr.Zero || !IsWindow(sceneWindow)) return;
            if (!ParkSceneWindow()) { SuspendForSafety("isolation-failed", "壁纸辅助窗口失去安全隔离，已停止捕获与重试。"); return; }
            // A native window can exist while the wallpaper is still loading.
            // Do not send settings or print messages during its startup phase.
            if ((DateTime.UtcNow - sceneOpenedUtc).TotalSeconds < 2) return;
            MotionSource expected = CurrentSource();
            if (expected == null) return;
            RECT rectangle = new RECT();
            bool clientOnly = captureMode.PreferClient(sceneWindow) && GetClientRect(sceneWindow, out rectangle);
            if (!clientOnly && !GetWindowRect(sceneWindow, out rectangle)) return;
            int width = rectangle.Right - rectangle.Left;
            int height = rectangle.Bottom - rectangle.Top;
            if (width < 160 || height < 90 || width > 3840 || height > 2160) return;
            long startedAt = Stopwatch.GetTimestamp();
            if (captureBuffer == null) captureBuffer = new CaptureBuffer();
            captureBuffer.EnsureDimensions(width, height);
            if (!PrintCapture(clientOnly ? 3U : 2U))
            {
                // Some Wallpaper Engine renderers do not support client-only
                // capture. Remember this for the window so steady frames do
                // not repeatedly allocate client and full-window buffers.
                if (!clientOnly) return;
                captureMode.ClientUnsupported(sceneWindow);
                clientOnly = false;
                if (!GetWindowRect(sceneWindow, out rectangle)) return;
                width = rectangle.Right - rectangle.Left;
                height = rectangle.Bottom - rectangle.Top;
                if (width < 160 || height < 90 || width > 3840 || height > 2160) return;
                captureBuffer.EnsureDimensions(width, height);
                if (!PrintCapture(2U)) return;
            }
            byte[] bytes = captureBuffer.EncodeJpeg();
            if (!scenePropertiesApplied) {
                foreach (string properties in WallpaperSafety.PropertyBatches(expected.Properties)) {
                    if (!OwnedSceneWindow(sceneWindow,sceneOwnerPid) || !EngineRunning()) { SuspendForSafety("renderer-exited", "设置壁纸前检测到渲染窗口失效，已停止命令。"); return; }
                    ProcessStartInfo update = new ProcessStartInfo(enginePath,
                        "-control applyProperties -location " + WallpaperSafety.QuoteArgument(WindowName) + " -properties RAW~(" + properties + ")~END");
                    if (!RunEngineCommand(update,3000)) { SuspendForSafety("properties-command-failed", "壁纸设置命令失败，已停止自动重试。"); return; }
                }
                scenePropertiesApplied=true;
            }
            long elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
            if (CurrentSource() != expected) return;
            long sequence = Interlocked.Increment(ref frameSequence);
            Interlocked.Add(ref captureTicksTotal, elapsedTicks);
            Volatile.Write(ref latestFrame, new FrameSnapshot(bytes, frameEpoch + "-" + sequence.ToString(CultureInfo.InvariantCulture), expected,
                width, height, Stopwatch.GetTimestamp(), elapsedTicks, clientOnly));
        }

        private bool PrintCapture(uint flags)
        {
            IntPtr dc = captureBuffer.Graphics.GetHdc();
            try { return PrintWindow(sceneWindow, dc, flags); }
            finally { captureBuffer.Graphics.ReleaseHdc(dc); }
        }

        private void CloseSceneWindow()
        {
            IntPtr previousWindow = sceneWindow;
            uint previousOwner = sceneOwnerPid;
            captureMode.Reset();
            Volatile.Write(ref latestFrame, null);
            if (captureBuffer != null) { captureBuffer.Dispose(); captureBuffer = null; }
            if (sceneWindow == IntPtr.Zero) return;
            sceneWindow = IntPtr.Zero;
            sceneOwnerPid = 0;
            if (safetySuspended) return;
            if (!OwnedSceneWindow(previousWindow,previousOwner) || !EngineRunning()) {
                if (!shuttingDown) SuspendSourceForSafety("unsafe-close", "原壁纸窗口无法安全关闭，已暂停后续壁纸切换。", "");
                return;
            }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(enginePath,
                    "-control closeWallpaper -location " + WallpaperSafety.QuoteArgument(WindowName));
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                if (!RunEngineCommand(info, 5000) && !shuttingDown) SuspendSourceForSafety("close-command-failed", "壁纸关闭命令失败，已停止后续窗口命令。", "");
            }
            catch { }
        }

        private bool EngineRunning()
        {
            try {
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(enginePath)))
                    using (process) if (!process.HasExited && String.Equals(process.MainModule.FileName, enginePath, StringComparison.OrdinalIgnoreCase)) return true;
            } catch { }
            return false;
        }

        private bool OwnedSceneWindow(IntPtr window, uint expectedOwner)
        {
            if (window == IntPtr.Zero || !IsWindow(window) || FindWindow(null, WindowName) != window) return false;
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == 0 || owner != expectedOwner) return false;
            try { using (Process process = Process.GetProcessById((int)owner))
                return !process.HasExited && String.Equals(process.MainModule.FileName, enginePath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal static bool RunEngineCommand(ProcessStartInfo info, int timeout)
        {
            info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
            try {
            info.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(info.FileName));
            using (Process command = Process.Start(info)) {
                if (command == null) return false;
                if (!command.WaitForExit(timeout)) {
                    // Only the temporary command process is cancelled, never the
                    // running desktop renderer. Every timeout stops further commands.
                    try { command.Kill(); command.WaitForExit(1000); } catch { }
                    return false;
                }
                return command.ExitCode == 0;
            } } catch { return false; }
        }

        private void SuspendForSafety(string code, string message)
        {
            var current = CurrentSource();
            SuspendSourceForSafety(code,message,current == null ? "" : current.Path);
        }

        private void SuspendSourceForSafety(string code, string message, string failedSource)
        {
            if (safetySuspended) return;
            safetySuspended = true;
            captureMode.Reset(); Volatile.Write(ref latestFrame, null);
            if (captureBuffer != null) { captureBuffer.Dispose(); captureBuffer = null; }
            sceneWindow = IntPtr.Zero; sceneOwnerPid = 0;
            try {
            WallpaperSafety.RecordFailure(service.StateRoot,failedSource,code,message);
            service.Log("wallpaper safety: " + code);
            } catch { service.Log("wallpaper safety record could not be saved; commands remain suspended"); }
            // An isolation or ownership failure must not issue a close command
            // against an ambiguous native window or create another instance.
        }

        internal static string ReadBoundedLine(Stream stream, int maxLength, ref int budget)
        {
            var bytes = new List<byte>();
            while (true) {
                if (--budget < 0) throw new InvalidDataException("Request header too large");
                int value = stream.ReadByte();
                if (value < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                if (value == 10) break;
                if (bytes.Count >= maxLength) throw new InvalidDataException("Request line too large");
                if (value != 13) bytes.Add((byte)value);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
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
            string label = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 403 ? "Forbidden" : status == 416 ? "Range Not Satisfiable" : status == 503 ? "Service Unavailable" : "Not Found";
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + label + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }

        private static void ReplyNoContent(NetworkStream stream)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nCache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
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
            var properties = new SortedDictionary<string, object>(StringComparer.Ordinal);
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

        internal static int CaptureDelayMilliseconds(long nextCaptureAt, long nowTicks)
        {
            return (int)Math.Min(35, Math.Max(0, Math.Ceiling((nextCaptureAt - nowTicks) * 1000.0 / Stopwatch.Frequency)));
        }

        internal static bool SameSource(MotionSource previous, MotionSource next)
        {
            return ReferenceEquals(previous, next) || previous != null && next != null &&
                previous.Kind == next.Kind && previous.Properties == next.Properties &&
                String.Equals(previous.Path, next.Path, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsFrameId(string value) { return value != null && Regex.IsMatch(value, "^[a-f0-9]{32}-[1-9][0-9]{0,18}$"); }

        internal static bool UsableFrame(FrameSnapshot frame, MotionSource expected, string after)
        {
            return frame != null && frame.Source == expected && frame.Id != after;
        }

        internal sealed class MotionSource { public string Kind; public string Path; public string Properties; }

        internal sealed class FrameSnapshot
        {
            internal readonly byte[] Bytes;
            internal readonly string Id;
            internal readonly MotionSource Source;
            internal readonly int Width, Height;
            internal readonly long CapturedAtTicks, CaptureTicks;
            internal readonly bool ClientArea;
            internal FrameSnapshot(byte[] bytes, string id, MotionSource source, int width, int height, long capturedAtTicks, long captureTicks, bool clientArea = false)
            {
                Bytes = bytes; Id = id; Source = source; Width = width; Height = height;
                CapturedAtTicks = capturedAtTicks; CaptureTicks = captureTicks;
                ClientArea = clientArea;
            }
        }

        internal sealed class CaptureModePolicy
        {
            private IntPtr unsupportedWindow;
            internal bool PreferClient(IntPtr window) { return window != IntPtr.Zero && window != unsupportedWindow; }
            internal void ClientUnsupported(IntPtr window) { unsupportedWindow = window; }
            internal void Reset() { unsupportedWindow = IntPtr.Zero; }
        }

        internal sealed class CaptureBuffer : IDisposable
        {
            internal Bitmap Bitmap { get; private set; }
            internal Graphics Graphics { get; private set; }
            private readonly MemoryStream memory = new MemoryStream(512 * 1024);
            private readonly ImageCodecInfo jpegCodec;
            private readonly EncoderParameters encoderParameters;
            private bool disposed;

            internal CaptureBuffer()
            {
                foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                    if (codec.FormatID == ImageFormat.Jpeg.Guid) { jpegCodec = codec; break; }
                if (jpegCodec == null) throw new InvalidOperationException("JPEG encoder is unavailable");
                encoderParameters = new EncoderParameters(1);
                encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            }

            internal void EnsureDimensions(int width, int height)
            {
                if (disposed) throw new ObjectDisposedException("CaptureBuffer");
                if (Bitmap != null && Bitmap.Width == width && Bitmap.Height == height) return;
                if (Graphics != null) Graphics.Dispose();
                if (Bitmap != null) Bitmap.Dispose();
                Bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                Graphics = System.Drawing.Graphics.FromImage(Bitmap);
            }

            internal byte[] EncodeJpeg()
            {
                if (disposed) throw new ObjectDisposedException("CaptureBuffer");
                if (Bitmap == null) throw new InvalidOperationException("Capture dimensions have not been configured");
                memory.Position = 0;
                memory.SetLength(0);
                Bitmap.Save(memory, jpegCodec, encoderParameters);
                return memory.ToArray();
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (Graphics != null) Graphics.Dispose();
                if (Bitmap != null) Bitmap.Dispose();
                Graphics = null; Bitmap = null;
                encoderParameters.Dispose();
                memory.Dispose();
            }
        }
        private bool HasPreviousHelper()
        {
            var owners=new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(enginePath)))
                using (process) try { if (!process.HasExited && String.Equals(process.MainModule.FileName,enginePath,StringComparison.OrdinalIgnoreCase)) owners.Add((uint)process.Id); } catch { }
            bool found=false;
            EnumWindows(delegate(IntPtr window,IntPtr parameter) {
                uint owner;GetWindowThreadProcessId(window,out owner);
                if (!owners.Contains(owner)) return true;
                var caption=new StringBuilder(80);GetWindowText(window,caption,caption.Capacity);
                if (caption.ToString().StartsWith("CodexDreamSkinMotion",StringComparison.Ordinal)) { found=true;return false; }
                return true;
            },IntPtr.Zero);
            return found;
        }
        private delegate bool WindowEnumerator(IntPtr window,IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumerator callback,IntPtr parameter);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rectangle);
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
