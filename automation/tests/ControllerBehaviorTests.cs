using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodexDreamSkinController;

internal sealed class FakeService : ControllerService
{
    internal readonly List<string> Calls = new List<string>();
    internal readonly Queue<ControllerState> States = new Queue<ControllerState>();
    internal Action<string, string[]> BeforeRun;
    internal int Waits;
    internal ControllerState State = new ControllerState { CdpReady=true, CodexRunning=true, InjectorRunning=true, InjectorSessionMatches=true, ActiveThemeId="A", ActiveThemeName="A" };
    internal int Failure;
    internal FakeService(string root) : base(PrepareRoot(root)) { }
    private static string PrepareRoot(string root) {
        Directory.CreateDirectory(Path.Combine(root, "control"));
        File.WriteAllText(Path.Combine(root, "control", "apply-saved-theme.ps1"), "# test double");
        return root;
    }
    public override bool PrerequisitesReady { get { return true; } }
    public override void EnsureRecoveryAgent() { }
    public override bool IsMediaHostReady() { return true; }
    public override ControllerState GetState() { if (States.Count > 0) State = States.Dequeue(); return State; }
    protected override void WaitForRuntime(int milliseconds) { Waits++; }
    protected override ProcessResult RunPowerShell(string script, string[] args, int timeout) {
        Calls.Add(Path.GetFileName(script) + " " + String.Join("|", args));
        if (BeforeRun != null) BeforeRun(script, args);
        if (Path.GetFileName(script) == "start-dream-skin.ps1" && Failure == 0) {
            State.InjectorRunning=true; State.InjectorSessionMatches=true; State.CdpReady=true; State.CodexRunning=true;
        }
        return new ProcessResult { ExitCode=Failure, Error="test failure", Output="" };
    }
}

internal static class ControllerBehaviorTests
{
    static int count;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
    static string root;
    static FakeService NewService(string name) { return new FakeService(Path.Combine(root,name)); }
    static string WriteProject(string projectsRoot, string name, object metadata, params string[] resources) {
        string directory = Path.Combine(projectsRoot, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(metadata));
        foreach(string resource in resources) File.WriteAllText(Path.Combine(directory, resource), "portable test fixture");
        return directory;
    }
    static void SaveTestTheme(FakeService service, string id, string name) {
        string directory = Path.Combine(service.SavedThemesRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "theme.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { id=id, name=name }));
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length>0 && args[0]=="--hold-pipes"){Thread.Sleep(10000);return 0;}
        if(args.Length>1 && args[0]=="--exit-code"){return Int32.Parse(args[1]);}
        if(args.Length==0) throw new ArgumentException("Pass an isolated test-state directory.");
        root=args[0]; Directory.CreateDirectory(root);
        string safetyRoot=Path.Combine(root,"safety-inputs");Directory.CreateDirectory(safetyRoot);
        string safeProject=Path.Combine(safetyRoot,"project.json");
        File.WriteAllText(safeProject,"{\"general\":{\"properties\":{\"enabled\":{\"type\":\"bool\"},\"speed\":{\"type\":\"slider\",\"min\":0,\"max\":2},\"choice\":{\"type\":\"combo\",\"options\":[{\"value\":\"1\"}]},\"tint\":{\"type\":\"color\"},\"label\":{\"type\":\"text\"},\"caption\":{\"type\":\"textinput\"}}}}");
        var normalized=WallpaperSafety.NormalizeProperties(safeProject,new Dictionary<string,object>{{"enabled",true},{"speed",1.5},{"choice","1"},{"tint","0.1 0.2 0.3"},{"caption","文字"},{"label","editor-only"},{"unknown",42}});
        Check(normalized.Count==5 && !normalized.ContainsKey("label") && !normalized.ContainsKey("unknown"),"only typed project-defined writable properties reach native commands");
        foreach(var invalid in new[]{
            new Dictionary<string,object>{{"enabled","false"}},new Dictionary<string,object>{{"speed",Double.NaN}},
            new Dictionary<string,object>{{"speed",99}},new Dictionary<string,object>{{"choice","invalid"}},
            new Dictionary<string,object>{{"tint","-1 1 1"}},new Dictionary<string,object>{{"caption","line\ncommand"}},
            new Dictionary<string,object>{{"caption",new string('x',513)}},new Dictionary<string,object>{{"-control",true}}
        }) {
            bool rejected=false;try{WallpaperSafety.NormalizeProperties(safeProject,invalid);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"unsafe preset type, range, text or property name is rejected before native invocation");
        }
        Check(WallpaperSafety.IsRegularLocalFile(safeProject) && !WallpaperSafety.IsRegularLocalFile(@"\\server\share\project.json") &&
            !WallpaperSafety.IsRegularLocalFile(safeProject+":extra"),"wallpaper resources cannot use remote paths or alternate data streams");
        Check(WallpaperSafety.QuoteArgument(@"F:\wallpaper folder\")=="\"F:\\wallpaper folder\\\\\"", "native path arguments correctly escape a trailing backslash");
        bool unsafeArgument=false;try{WallpaperSafety.QuoteArgument("asset\r-command");}catch(InvalidOperationException){unsafeArgument=true;}
        Check(unsafeArgument,"native argument control characters cannot inject another command");
        var manyProperties=new Dictionary<string,object>();for(int n=0;n<120;n++)manyProperties["p"+n]=new string('a',40);
        var batches=WallpaperSafety.PropertyBatches(MotionHost.PropertiesJson(manyProperties));
        Check(batches.Count>1 && batches.All(batch=>System.Text.Encoding.UTF8.GetByteCount(batch)<=1536 && batch.Contains("\"volume\":0")),
            "large presets are split into bounded settings messages that remain muted and valid JSON");
        int recoveredProperties=0;foreach(string batch in batches)recoveredProperties+=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(batch).Count-1;
        Check(recoveredProperties==120,"bounded settings batches preserve every validated property exactly once");
        string thisExe=Process.GetCurrentProcess().MainModule.FileName;
        Check(MotionHost.RunEngineCommand(new ProcessStartInfo(thisExe,"--exit-code 0"),2000) &&
            !MotionHost.RunEngineCommand(new ProcessStartInfo(thisExe,"--exit-code 7"),2000),"native command failures cannot be mistaken for successful wallpaper loading");
        var commandDeadline=Stopwatch.StartNew();
        Check(!MotionHost.RunEngineCommand(new ProcessStartInfo(thisExe,"--hold-pipes"),80) && commandDeadline.ElapsedMilliseconds<2000,
            "a stuck temporary command is cancelled without an unbounded retry or desktop renderer termination");
        int lineBudget=20;bool oversizedLine=false;
        using(var oversized=new MemoryStream(System.Text.Encoding.ASCII.GetBytes(new string('x',30))))try{MotionHost.ReadBoundedLine(oversized,8,ref lineBudget);}catch(InvalidDataException){oversizedLine=true;}
        Check(oversizedLine,"local request lines are bounded while reading rather than after allocation");
        var safetyService=NewService("persistent-safety");File.WriteAllText(Path.Combine(safetyService.WallpaperControlRoot,"motion-token.txt"),new string('a',64));
        var safetyHost=(MotionHost)typeof(MotionHost).GetConstructor(BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(ControllerService)},null).Invoke(new object[]{safetyService});
        var blockedSource=new MotionHost.MotionSource {Kind="scene",Path=safeProject,Properties=MotionHost.PropertiesJson(null)};
        safetyHost.UpdateValidatedSource(blockedSource);
        Directory.CreateDirectory(Path.Combine(safetyService.StateRoot,"active-theme"));
        File.WriteAllText(Path.Combine(safetyService.StateRoot,"active-theme","theme.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"motion",new Dictionary<string,object>{{"source",safeProject}}}}));
        typeof(MotionHost).GetMethod("SuspendForSafety",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(safetyHost,new object[]{"test-failed","Native failure was contained"});
        Check(File.Exists(Path.Combine(safetyService.StateRoot,"paused")) && WallpaperSafety.BlockedMessage(safetyService.StateRoot,safeProject)!=null,
            "a wallpaper failure persists a safety pause before another service can retry");
        safetyService.State.Paused=true;
        Check(!safetyService.StartSkin(false,true).Success && safetyService.Calls.Count==0,"enabling a quarantined source cannot restart native wallpaper commands");
        string differentSource=Path.Combine(safetyRoot,"different.mp4");
        safetyHost.UpdateValidatedSource(new MotionHost.MotionSource {Kind="video",Path=differentSource,Properties=MotionHost.PropertiesJson(null)});
        Check(WallpaperSafety.BlockedMessage(safetyService.StateRoot,differentSource)==null &&
            !(bool)typeof(MotionHost).GetField("safetySuspended",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(safetyHost),
            "a validated different wallpaper can resume without erasing the failed-source quarantine");
        Check(WallpaperSafety.BlockedMessage(safetyService.StateRoot,safeProject)!=null,"switching away and back cannot erase a failed wallpaper's quarantine");
        WallpaperSafety.RecordFailure(safetyService.StateRoot,differentSource,"second-failure","Second source failed");
        Check(WallpaperSafety.BlockedMessage(safetyService.StateRoot,safeProject)!=null && WallpaperSafety.BlockedMessage(safetyService.StateRoot,differentSource)!=null,
            "failures from multiple wallpapers remain quarantined after later failures");
        File.WriteAllText(Path.Combine(safetyService.WallpaperControlRoot,"motion-safety.json"),"{}");
        Check(WallpaperSafety.BlockedMessage(safetyService.StateRoot,differentSource)!=null,"malformed safety records fail closed rather than enabling native retries");
        bool nonfiniteBatch=false;try{WallpaperSafety.PropertyBatches("{\"speed\":1e100}");}catch(InvalidOperationException){nonfiniteBatch=true;}
        Check(nonfiniteBatch,"the native message boundary rejects unsafe numbers even without the schema validator");
        var orphanFlags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(MotionHost).GetField("enginePath",orphanFlags).SetValue(safetyHost,thisExe);
        using(var orphanForm=new Form {Text="CodexDreamSkinMotion-test"}) {
            IntPtr orphanHandle=orphanForm.Handle;
            Check((bool)typeof(MotionHost).GetMethod("HasPreviousHelper",orphanFlags).Invoke(safetyHost,new object[0]),
                "a remaining owned helper window prevents another native wallpaper layer from opening");
        }
        var unsafeCloseService=NewService("unsafe-close");
        File.WriteAllText(Path.Combine(unsafeCloseService.WallpaperControlRoot,"motion-token.txt"),new string('b',64));
        var unsafeCloseHost=(MotionHost)typeof(MotionHost).GetConstructor(orphanFlags,null,new[]{typeof(ControllerService)},null).Invoke(new object[]{unsafeCloseService});
        unsafeCloseHost.UpdateValidatedSource(blockedSource);
        typeof(MotionHost).GetField("sceneWindow",orphanFlags).SetValue(unsafeCloseHost,new IntPtr(1));
        unsafeCloseHost.UpdateValidatedSource(new MotionHost.MotionSource {Kind="video",Path=differentSource,Properties=MotionHost.PropertiesJson(null)});
        Check(WallpaperSafety.BlockedMessage(unsafeCloseService.StateRoot,differentSource)!=null &&
            ReferenceEquals(typeof(MotionHost).GetField("source",orphanFlags).GetValue(unsafeCloseHost),blockedSource),
            "an ambiguous old window stops the next source transition and all later native commands");
        var rawBoundary=WallpaperSafety.PropertyBatches("{\"caption\":\")~END -control closeWallpaper\"}").Single();
        Check(!rawBoundary.Contains(")~END") && new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(rawBoundary)["caption"].Equals(")~END -control closeWallpaper"),
            "text cannot terminate the native RAW JSON envelope or lose its original value");
        var now = new DateTime(2026,10,3,12,0,0,DateTimeKind.Utc);
        var noPort = RuntimeConnection.DiscoverFromInventory(9335,new List<int>(),null,port=>{throw new Exception("unexpected probe");});
        Check(noPort.Code=="no_endpoint", "a confirmed empty port inventory reports an unopened session");
        var unavailableInventory = RuntimeConnection.DiscoverFromInventory(9335,new List<int>(),"discovery_unavailable",port=>null);
        Check(unavailableInventory.Code=="discovery_unavailable", "port enumeration failure is not mislabeled as an unopened interface");
        var unreadableOwner = RuntimeConnection.DiscoverFromInventory(9335,new List<int>(),"owner_check_unavailable",port=>null);
        Check(unreadableOwner.Code=="owner_check_unavailable", "unreadable Codex image ownership remains a retryable detection condition");
        var preferredFirst = new List<int>();
        var readyInventory = RuntimeConnection.DiscoverFromInventory(9335,new List<int>{9444,9335},"owner_check_unavailable",port=>{
            preferredFirst.Add(port);return new RuntimeConnection {Ready=true,Port=port,Code="ready",BrowserId="same-session"};
        });
        Check(readyInventory.Ready && readyInventory.Port==9335 && preferredFirst.Count==1,
            "a verified preferred interface wins over incomplete unrelated ownership checks");
        var pendingTargets = RuntimeConnection.DiscoverFromInventory(9335,new List<int>{9335},null,port=>new RuntimeConnection{Port=port,Code="no_targets"});
        Check(pendingTargets.Code=="no_targets", "a listening interface awaiting a renderer is distinct from no interface");
        Check(RuntimeConnection.IsRegisteredCodexImagePath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.930.2377.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe") &&
            !RuntimeConnection.IsRegisteredCodexImagePath(@"C:\Other\ChatGPT.exe"), "limited image lookup keeps the official Codex path boundary");
        var recovery = new RecoveryPolicy();
        var recoveryState = new ControllerState { CodexRunning=true, CdpReady=true, CodexSessionKey="first", InjectorSessionMatches=true };
        Check(recovery.Decide(recoveryState,true,now)=="recover", "a stopped injector is recovered in the existing Codex session");
        recovery.RecordAttempt(false,now);
        Check(recovery.Decide(recoveryState,true,now.AddSeconds(1))=="retry-wait", "failed recovery backs off instead of restarting in a loop");
        Check(recovery.Decide(recoveryState,true,now.AddSeconds(5))=="recover", "failed recovery can retry after the bounded delay");
        recovery.RecordAttempt(false,now.AddSeconds(5));
        Check(recovery.RetryAt==now.AddSeconds(15), "repeated failures increase the delay");
        recoveryState.CodexSessionKey="after-update";
        Check(recovery.Decide(recoveryState,true,now.AddSeconds(6))=="recover" && recovery.Failures==0,
            "a new browser session after an update is attempted immediately");
        recoveryState.InjectorRunning=true;
        recovery.RecordAttempt(true,now);
        Check(recovery.Decide(recoveryState,true,now)=="active", "healthy playback is left running");
        Check(recovery.Decide(recoveryState,true,now.AddMinutes(5))=="verify", "healthy playback is periodically checked without reapplying the skin");
        recoveryState.CodexSessionKey="new-browser";
        Check(recovery.Decide(recoveryState,true,now)=="verify", "a changed browser identity triggers fresh renderer verification");
        recovery.RecordAttempt(true,now);
        recoveryState.InjectorSessionMatches=false;
        Check(recovery.Decide(recoveryState,true,now)=="recover", "a living old injector must be rebound after an update");
        recoveryState.InjectorSessionMatches=true;
        Check(recovery.Decide(recoveryState,false,now)=="recover", "a stopped wallpaper media host is recovered");
        recoveryState.Paused=true;
        Check(recovery.Decide(recoveryState,false,now)=="paused", "closing the skin prevents automatic re-enabling");
        recoveryState.Paused=false; recoveryState.CdpReady=false;
        Check(recovery.Decide(recoveryState,false,now)=="waiting-interface", "ordinary Codex startup waits without restarting it");
        recoveryState.CodexRunning=false;
        Check(recovery.Decide(recoveryState,false,now)=="waiting-codex", "recovery does not launch Codex after the user closes it");
        var recoveryOff=NewService("recovery-off"); recoveryOff.State.CdpReady=false;
        Check(!recoveryOff.RecoverConnection().Success && recoveryOff.Calls.Count==0,
            "automatic recovery never launches a Codex process without a live trusted interface");
        var recoveryPaused=NewService("recovery-paused"); recoveryPaused.State.Paused=true;
        Check(recoveryPaused.RecoverConnection().Success && recoveryPaused.Calls.Count==0,
            "automatic recovery preserves an explicitly paused skin");
        var readonlyCheck=NewService("readonly-check");
        Check(readonlyCheck.VerifyCurrentConnection().Success && readonlyCheck.Calls.Count==1 && !readonlyCheck.Calls[0].Contains("-ApplyCurrentTheme"),
            "background health verification does not reload a healthy skin");
        var oldSession=NewService("old-session"); oldSession.State.InjectorSessionMatches=false;
        Check(oldSession.StartSkin(false,true).Success && oldSession.Calls.Single().StartsWith("start-dream-skin.ps1"),
            "a live process bound to a previous browser session is restarted instead of being refreshed");
        var backgroundRepair=NewService("background-repair");
        Check(backgroundRepair.RecoverConnection().Success && backgroundRepair.Calls.Single().Contains("-PreservePaused"),
            "background repair preserves a pause requested while reconnecting");
        foreach(var desktop in new [] {
            new Rectangle(0,0,1920,1080), new Rectangle(-2560,-500,4480,1940),
            new Rectangle(0,0,3840,2160), new Rectangle(-3840,0,11520,2160)
        }) {
            var point=MotionHost.CaptureParkingPoint(desktop);
            Check(!desktop.IntersectsWith(new Rectangle(point,new Size(1624,964))),
                "wallpaper helper stays outside virtual desktop "+desktop);
        }
        bool missingDesktopRejected=false;
        try { MotionHost.CaptureParkingPoint(Rectangle.Empty); } catch(InvalidOperationException) { missingDesktopRejected=true; }
        Check(missingDesktopRejected,"missing display geometry never falls back to visible screen coordinates");
        long framePeriod = Stopwatch.Frequency / MotionHost.TargetFramesPerSecond;
        Check(MotionHost.CaptureDelayMilliseconds(framePeriod, Stopwatch.Frequency / 100) >= 23 &&
            MotionHost.CaptureDelayMilliseconds(framePeriod, Stopwatch.Frequency / 100) <= 24,
            "capture work consumes the frame period instead of adding a full delay after every frame");
        Check(MotionHost.CaptureDelayMilliseconds(framePeriod, framePeriod * 2) == 0,
            "a slow capture starts the next frame without an additional pacing stall");
        var buffer = new MotionHost.CaptureBuffer();
        buffer.EnsureDimensions(160,90);
        var bitmapBefore = buffer.Bitmap; var graphicsBefore = buffer.Graphics;
        buffer.Graphics.Clear(Color.Red);
        byte[] firstJpeg = buffer.EncodeJpeg();
        buffer.EnsureDimensions(160,90);
        buffer.Graphics.Clear(Color.Blue);
        byte[] secondJpeg = buffer.EncodeJpeg();
        Check(ReferenceEquals(bitmapBefore,buffer.Bitmap) && ReferenceEquals(graphicsBefore,buffer.Graphics),
            "steady capture dimensions reuse the GDI bitmap and graphics objects");
        using(var firstMemory=new MemoryStream(firstJpeg))
        using(var firstImage=new Bitmap(firstMemory))
        using(var secondMemory=new MemoryStream(secondJpeg))
        using(var secondImage=new Bitmap(secondMemory)) {
            Check(firstImage.Width==160 && firstImage.Height==90 && firstImage.GetPixel(80,45).R>200 && secondImage.GetPixel(80,45).B>200,
                "published JPEG bytes retain their pixels after the reusable encoder captures a later frame");
        }
        buffer.EnsureDimensions(320,180);
        Check(!ReferenceEquals(bitmapBefore,buffer.Bitmap) && buffer.Bitmap.Width==320 && buffer.Bitmap.Height==180,
            "a resized capture receives a new buffer with matching dimensions");
        buffer.Dispose(); buffer.Dispose();
        bool disposedBufferRejected=false;
        try { buffer.EnsureDimensions(160,90); } catch(ObjectDisposedException) { disposedBufferRejected=true; }
        Check(disposedBufferRejected && buffer.Bitmap==null && buffer.Graphics==null,
            "closing capture releases reusable resources and cannot revive a disposed buffer");
        var capturePolicy = new MotionHost.CaptureModePolicy();
        var firstWindow = new IntPtr(1234); var replacementWindow = new IntPtr(1235);
        Check(capturePolicy.PreferClient(firstWindow),"a new capture window gets one client-only capability attempt");
        capturePolicy.ClientUnsupported(firstWindow);
        Check(!capturePolicy.PreferClient(firstWindow) && capturePolicy.PreferClient(replacementWindow),
            "a failed client capture is cached for its window while a different window can try client capture");
        using(var fallbackBuffer = new MotionHost.CaptureBuffer()) {
            fallbackBuffer.EnsureDimensions(176,129);
            var fallbackBitmap = fallbackBuffer.Bitmap; var fallbackGraphics = fallbackBuffer.Graphics;
            for(int index=0;index<20;index++) fallbackBuffer.EnsureDimensions(capturePolicy.PreferClient(firstWindow)?160:176,capturePolicy.PreferClient(firstWindow)?90:129);
            Check(ReferenceEquals(fallbackBitmap,fallbackBuffer.Bitmap) && ReferenceEquals(fallbackGraphics,fallbackBuffer.Graphics),
                "cached full-window capture keeps the same GDI resources on all subsequent frames");
        }
        capturePolicy.Reset();
        Check(capturePolicy.PreferClient(firstWindow),"window closure resets an unsupported client-capture decision even when a handle is reused");
        var mediaService=NewService("media-protocol");
        File.WriteAllText(Path.Combine(mediaService.WallpaperControlRoot,"motion-token.txt"),new string('a',64));
        var mediaHost=(MotionHost)typeof(MotionHost).GetConstructor(BindingFlags.NonPublic|BindingFlags.Instance,null,new []{typeof(ControllerService)},null).Invoke(new object[]{mediaService});
        var mediaSource=new MotionHost.MotionSource {Kind="scene",Path="F:\\test-wallpaper\\project.json",Properties=MotionHost.PropertiesJson(null)};
        var identicalSource=new MotionHost.MotionSource {Kind="scene",Path="f:\\TEST-wallpaper\\project.json",Properties=MotionHost.PropertiesJson(null)};
        Check(MotionHost.SameSource(mediaSource,identicalSource) && !MotionHost.SameSource(mediaSource,new MotionHost.MotionSource {Kind="scene",Path=mediaSource.Path,Properties="changed"}),
            "unchanged source identity survives theme refresh while changed preset properties require replacement");
        string frameId=Guid.NewGuid().ToString("N")+"-1";
        var snapshot=new MotionHost.FrameSnapshot(firstJpeg,frameId,mediaSource,160,90,Stopwatch.GetTimestamp(),Stopwatch.Frequency/100);
        Check(MotionHost.UsableFrame(snapshot,mediaSource,null) && !MotionHost.UsableFrame(snapshot,identicalSource,null) && !MotionHost.UsableFrame(snapshot,mediaSource,frameId),
            "frame selection rejects retired sources and an already received frame identifier");
        typeof(MotionHost).GetField("source",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(mediaHost,mediaSource);
        typeof(MotionHost).GetField("latestFrame",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(mediaHost,snapshot);
        byte[] frameReply=MediaRequest(mediaHost,"/frame?t="+new string('a',64));
        string frameReplyText=System.Text.Encoding.ASCII.GetString(frameReply);
        Check(frameReplyText.StartsWith("HTTP/1.1 200 OK") && frameReplyText.Contains("X-Dream-Frame-Id: "+frameId) && frameReply.Skip(frameReply.Length-firstJpeg.Length).SequenceEqual(firstJpeg),
            "frame responses publish the identifier and JPEG bytes from the same snapshot");
        var watch = Stopwatch.StartNew();
        string unchangedReply=System.Text.Encoding.ASCII.GetString(MediaRequest(mediaHost,"/frame?t="+new string('a',64)+"&after="+frameId));
        Check(unchangedReply.StartsWith("HTTP/1.1 204 No Content") && watch.Elapsed.TotalMilliseconds<500,
            "polling an unchanged frame returns a short no-content response instead of resending the JPEG");
        string badFrameReply=System.Text.Encoding.ASCII.GetString(MediaRequest(mediaHost,"/frame?t="+new string('a',64)+"&after=bad"));
        Check(badFrameReply.StartsWith("HTTP/1.1 400 Bad Request"),"invalid frame identifiers cannot enter the response headers");
        string healthReply=System.Text.Encoding.ASCII.GetString(MediaRequest(mediaHost,"/health"));
        Check(healthReply.EndsWith("codex-dream-skin-motion/1"),"the existing media health response remains unchanged");
        File.WriteAllText(Path.Combine(mediaService.StateRoot,"paused"),"paused");
        Check(System.Text.Encoding.ASCII.GetString(MediaRequest(mediaHost,"/frame?t="+new string('a',64))).StartsWith("HTTP/1.1 503 Service Unavailable"),
            "a valid media token cannot continue fetching frames after the skin is paused");
        File.Delete(Path.Combine(mediaService.StateRoot,"paused"));
        string rejectedMetrics=System.Text.Encoding.ASCII.GetString(MediaRequest(mediaHost,"/metrics"));
        string metricsReply=System.Text.Encoding.ASCII.GetString(MediaRequest(mediaHost,"/metrics?t="+new string('a',64)));
        Check(rejectedMetrics.StartsWith("HTTP/1.1 403 Forbidden") && metricsReply.Contains("\"captureMsLast\":10") && metricsReply.Contains("\"width\":160") && metricsReply.Contains("\"captureMode\":\"window\"") && !metricsReply.Contains(mediaSource.Path) && !metricsReply.Contains(new string('a',64)),
            "performance metrics require the local token and expose numeric diagnostics without source paths or credentials");
        var processProbe=new ProcessProbeService(Path.Combine(root,"process-probe"));
        var isolatedListener=new TcpListener(IPAddress.Loopback,0);
        isolatedListener.Start();
        int isolatedPort=((IPEndPoint)isolatedListener.LocalEndpoint).Port;
        MotionHost.IsolateSocket(isolatedListener.Server);
        using(var child=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--hold-pipes") {
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
        })) {
            isolatedListener.Stop();
            var rebound=new TcpListener(IPAddress.Loopback,isolatedPort);
            try { rebound.Start(); Check(!child.HasExited,"child processes cannot retain the media listener after its parent releases it"); }
            finally { rebound.Stop(); if(!child.HasExited)child.Kill(); }
        }
        string pipeScript=Path.Combine(root,"inherited-pipes.ps1");
        string testExe=Process.GetCurrentProcess().MainModule.FileName.Replace("'","''");
        File.WriteAllText(pipeScript,"$i=New-Object Diagnostics.ProcessStartInfo\r\n$i.FileName='"+testExe+"'\r\n$i.Arguments='--hold-pipes'\r\n$i.UseShellExecute=$false\r\n$i.CreateNoWindow=$true\r\n$i.WindowStyle='Hidden'\r\n$p=[Diagnostics.Process]::Start($i)\r\nWrite-Output 'pipe-marker'\r\nexit 0\r\n");
        watch=Stopwatch.StartNew();
        var pipeResult=processProbe.Invoke(pipeScript);
        Check(pipeResult.ExitCode==0 && pipeResult.Output.Contains("pipe-marker") && watch.Elapsed.TotalSeconds<8,
            "command returns after its script exits even if a background GUI holds stdout open");
        string version="{\"webSocketDebuggerUrl\":\"ws://127.0.0.1:9444/devtools/browser/browser-1\"}";
        string targets="[{\"type\":\"webview\",\"url\":\"app://codex/index.html\",\"webSocketDebuggerUrl\":\"ws://127.0.0.1:9444/devtools/page/page-1\"}]";
        var emptyEndpoint=RuntimeConnection.Probe(9444,true,route=>route=="/json/version"?version:"[]");
        Check(!emptyEndpoint.Ready && emptyEndpoint.Code=="no_targets","listening port with no Codex page is not ready");
        var usable=RuntimeConnection.Probe(9444,true,route=>route=="/json/version"?version:targets);
        Check(usable.Ready && usable.Port==9444,"valid Codex endpoint works on a discovered nondefault port");
        Check(usable.BrowserId=="browser-1", "recovery tracks the browser identity rather than a fixed package version");
        Check(RuntimeConnection.MatchesSession("browser-1",9444,usable), "matching recorded browser and port can reuse the current injector");
        Check(!RuntimeConnection.MatchesSession("previous-browser",9444,usable), "a new browser identity invalidates an old injector binding");
        Check(!RuntimeConnection.MatchesSession("browser-1",9335,usable), "a changed port invalidates an old injector binding");
        Check(RuntimeConnection.Probe(9444,true,route=>route=="/json/version"?version:targets.Replace("app://codex/","app://-/")).Ready,
            "real Codex app://- page is accepted before the ChatGPT webview loads");
        Check(!RuntimeConnection.Probe(9444,true,route=>route=="/json/version"?version:targets.Replace("app://codex/index.html","https://example.com/")).Ready,
            "unrelated browser page is rejected");
        Check(!RuntimeConnection.Probe(9444,true,route=>route=="/json/version"?version:targets.Replace("127.0.0.1","192.0.2.1")).Ready,
            "nonloopback target socket is rejected");
        int reads=0;
        Check(!RuntimeConnection.Probe(9444,false,route=>{reads++;return version;}).Ready && reads==0,"untrusted listener is not queried");
        Check(RuntimeConnection.Probe(9444,true,route=>"bad-json").Code=="protocol_error","changed endpoint format is reported explicitly");
        var b = new ThemeItem { Id="B", Name="Theme B", Directory=Path.Combine(root,"themes","B"), Source="saved" };
        var saved=NewService("saved");
        Check(saved.ApplyTheme(b,false).Success,"saved theme application succeeds");
        Check(saved.Calls[0].Contains(b.Directory),"saved theme uses selected absolute directory");
        Check(saved.Calls[1].Contains("-ApplyCurrentTheme"),"saved theme is enabled and verified live");

        var paused=NewService("paused");
        string pause=Path.Combine(root,"paused","paused"); File.WriteAllText(pause,"paused");
        Check(paused.ApplyTheme(b,false).Success && !File.Exists(pause),"applying while paused resumes the selected skin");

        var native=NewService("native"); native.State=new ControllerState();
        Check(native.StartNative().Success,"native appearance launcher succeeds");
        Check(native.Calls.Single().Contains("-StartPaused"),"native launcher starts with skin disabled");

        var unavailable=NewService("unavailable"); unavailable.State.CdpReady=false;
        Check(!unavailable.ApplyTheme(b,false).Success && unavailable.Calls.Count==0,"running Codex without CDP is never restarted");
        Check(unavailable.StartNative().Success && unavailable.Calls.Count==0,"native launcher leaves existing Codex untouched");
        Check(!unavailable.ConnectCurrent().Success && unavailable.Calls.Count==0,"unavailable hot connection does not launch or restart Codex");
        var hot=NewService("hot");
        Check(hot.ConnectCurrent().Success && hot.Calls.Single().StartsWith("verify-dream-skin.ps1") && !hot.Calls[0].Contains("-ApplyCurrentTheme"),
            "an already healthy hot connection is verified without reloading the wallpaper");
        var alreadyActive=NewService("already-active");
        Check(alreadyActive.StartSkin(false,true).Success && alreadyActive.StartSkin(false,true).Success &&
            alreadyActive.Calls.Count==2 && alreadyActive.Calls.All(x=>x.StartsWith("verify-dream-skin.ps1") && !x.Contains("-ApplyCurrentTheme")),
            "repeated enabling keeps the healthy watcher and playing wallpaper intact");
        var staleRenderer=NewService("stale-renderer");
        staleRenderer.BeforeRun=delegate(string script,string[] commandArgs){staleRenderer.Failure=staleRenderer.Calls.Count==1?2:0;};
        Check(staleRenderer.StartSkin(false,true).Success && staleRenderer.Calls.Count==2 &&
            !staleRenderer.Calls[0].Contains("-ApplyCurrentTheme") && staleRenderer.Calls[1].Contains("-ApplyCurrentTheme"),
            "a failed readonly renderer check repairs the skin without restarting Codex");
        var startingEndpoint=NewService("starting-endpoint");
        var readyState=startingEndpoint.State;
        startingEndpoint.States.Enqueue(new ControllerState {CodexRunning=true,ConnectionCode="no_endpoint"});
        startingEndpoint.States.Enqueue(new ControllerState {CodexRunning=true,ConnectionCode="no_targets"});
        startingEndpoint.States.Enqueue(readyState);
        Check(startingEndpoint.StartSkin(false,true).Success && startingEndpoint.Waits==2 && startingEndpoint.Calls.Single().StartsWith("verify-dream-skin.ps1"),
            "transient endpoint and target registration are retried before an unavailable-interface error");
        var resumingEndpoint=NewService("resuming-endpoint");
        File.WriteAllText(Path.Combine(resumingEndpoint.StateRoot,"paused"),"paused");
        var resumedState=resumingEndpoint.State;
        resumingEndpoint.States.Enqueue(new ControllerState {CodexRunning=true,Paused=true,ConnectionCode="no_endpoint"});
        resumingEndpoint.States.Enqueue(resumedState);
        Check(resumingEndpoint.StartSkin(false,true).Success && resumingEndpoint.Waits==1 && resumingEndpoint.Calls.Single().Contains("-ApplyCurrentTheme") &&
            !File.Exists(Path.Combine(resumingEndpoint.StateRoot,"paused")),
            "manual resume retries a briefly absent endpoint even while the stored skin state is paused");
        var neverOpened=NewService("never-opened"); neverOpened.State.CdpReady=false; neverOpened.State.ConnectionCode="no_endpoint";
        Check(!neverOpened.StartSkin(false,true).Success && neverOpened.Waits==6 && neverOpened.Calls.Count==0,
            "a genuinely unopened interface gets bounded retries without launching or restarting Codex");
        var connectionCache=new ConnectionProbeService(Path.Combine(root,"connection-cache"));
        Check(!connectionCache.GetState().CdpReady && connectionCache.Probes==1,"an unavailable endpoint is cached briefly");
        connectionCache.Connection=new RuntimeConnection {Ready=true,Code="ready",Port=9335,BrowserId="fresh-browser"};
        Thread.Sleep(650);
        Check(connectionCache.GetState().CdpReady && connectionCache.Probes==2,
            "an endpoint that becomes available is detected before the previous five-second failure cache expires");

        var closed=NewService("closed"); closed.State=new ControllerState();
        Check(closed.ApplyTheme(b,false).Success && closed.Calls[1].StartsWith("start-dream-skin.ps1"),"selected saved theme starts even if Codex was closed");

        var off=NewService("off");
        Check(off.PauseSkin().Success && off.Calls.Single().Contains("-RemoveSkin"),"off command removes and verifies live skin");
        var failedOff=NewService("failed-off"); failedOff.Failure=2;
        Check(!failedOff.PauseSkin().Success,"failed removal verification is reported as failure");
        var serialOff=NewService("serial-toggle"); var serialOn=NewService("serial-toggle");
        using(var removeEntered=new ManualResetEventSlim(false))
        using(var releaseRemoval=new ManualResetEventSlim(false))
        using(var enableRequested=new ManualResetEventSlim(false))
        using(var enableEntered=new ManualResetEventSlim(false)) {
            serialOff.BeforeRun=delegate(string script,string[] commandArgs){removeEntered.Set(); if(!releaseRemoval.Wait(5000))throw new Exception("test removal timed out");};
            serialOn.BeforeRun=delegate(string script,string[] commandArgs){enableEntered.Set();};
            var offTask=Task.Run(()=>serialOff.PauseSkin());
            Check(removeEntered.Wait(3000),"closing reaches live removal inside the controller transaction");
            var onTask=Task.Run(()=>{enableRequested.Set();return serialOn.StartSkin(false,true);});
            Check(enableRequested.Wait(1000) && !enableEntered.Wait(150) && File.Exists(Path.Combine(serialOff.StateRoot,"paused")),
                "a separate controller cannot clear the pause or apply while removal is still running");
            releaseRemoval.Set();
            Check(offTask.GetAwaiter().GetResult().Success && onTask.GetAwaiter().GetResult().Success &&
                serialOff.Calls.Single().Contains("-RemoveSkin") && serialOn.Calls.Single().Contains("-ApplyCurrentTheme") &&
                !File.Exists(Path.Combine(serialOff.StateRoot,"paused")),
                "serialized close then enable preserves the requested final state and live watcher");
        }
        var failedApply=NewService("failed-apply"); failedApply.Failure=2;
        Check(!failedApply.ApplyTheme(b,false).Success && failedApply.Calls.Count==1,"failed theme preparation does not start another skin");

        var unsupported=NewService("unsupported-source");
        Check(!unsupported.ApplyTheme(new ThemeItem { Id="unknown", Name="Unknown", Directory=b.Directory, Source="unknown" },false).Success && unsupported.Calls.Count==0,
            "an unknown theme source is rejected without executing any command");

        // All catalog inputs are local metadata fixtures. Neither Steam nor Wallpaper Engine is required.
        string projectsRoot=Path.Combine(root,"catalog","defaultprojects");
        Directory.CreateDirectory(projectsRoot);
        WriteProject(projectsRoot,"video",new { title="Portable Video\r\nLine", type="video", file="clip.webm" },"clip.webm");
        string sceneDirectory=WriteProject(projectsRoot,"legacy-scene",new {
            title="Portable Scene", file="scene.json", general=new {properties=new Dictionary<string,object> {
                {"brightness",new {type="slider"}}, {"label",new {type="text"}}, {"asset",new {type="file"}}
            }}
        },"scene.json");
        WriteProject(projectsRoot,"packaged-scene",new { title="Portable Package", type="scene", file="scene.json" },"scene.pkg");
        WriteProject(projectsRoot,"web",new { title="Portable Web", file="index.html" },"index.html");
        WriteProject(projectsRoot,"application",new { title="Portable Application", type="application", file="external.exe" });
        string presetDirectory=WriteProject(projectsRoot,"preset",new {
            title="Portable Preset", dependency="legacy-scene", preset=new Dictionary<string,object> {
                {"brightness",0.8}, {"label","do not publish a user text setting"}, {"asset","texture.png"}, {"unknown",true}
            }
        },"texture.png");
        WriteProject(projectsRoot,"missing-preset",new { title="Missing Preset", dependency="absent", preset=new {brightness=0.5} });
        WriteProject(projectsRoot,"broken-scene",new { title="Incomplete Scene", type="scene", file="missing.json" });
        File.WriteAllText(Path.Combine(projectsRoot,"escape.mp4"),"outside the project directory");
        WriteProject(projectsRoot,"escape-video",new { title="Escaped Video", type="video", file="../escape.mp4" });
        string malformed=Path.Combine(projectsRoot,"malformed");
        Directory.CreateDirectory(malformed); File.WriteAllText(Path.Combine(malformed,"project.json"),"invalid json");
        var wallpapers=WallpaperCatalog.Discover(new [] {projectsRoot});
        Check(wallpapers.Count==8,"catalog metadata includes valid and unavailable entries while excluding malformed and escaped projects");
        Check(wallpapers.Any(x=>x.MediaKind=="video" && x.Name=="Portable Video  Line" && String.IsNullOrEmpty(x.UnavailableReason)),
            "video metadata normalizes title line breaks and recognizes a local webm asset");
        Check(wallpapers.Any(x=>x.Directory==sceneDirectory && x.MediaKind=="scene" && String.IsNullOrEmpty(x.UnavailableReason)),
            "legacy JSON scenes are recognized without a scene.pkg");
        Check(wallpapers.Any(x=>x.Name=="Portable Package" && String.IsNullOrEmpty(x.UnavailableReason)),
            "a packaged scene can use its scene.pkg when the declared JSON is absent");
        Check(wallpapers.Any(x=>x.MediaKind=="web" && String.IsNullOrEmpty(x.UnavailableReason)),
            "web wallpaper metadata remains selectable");
        Check(wallpapers.Any(x=>x.MediaKind=="application" && !String.IsNullOrEmpty(x.UnavailableReason)),
            "application wallpapers remain visible with an explicit unsupported reason");
        var presetWallpaper=wallpapers.Single(x=>x.IsPreset);
        Check(presetWallpaper.MediaPath==Path.Combine(sceneDirectory,"project.json") && presetWallpaper.MediaKind=="scene" && String.IsNullOrEmpty(presetWallpaper.UnavailableReason),
            "a preset resolves the base wallpaper from the injected catalog");
        Check(presetWallpaper.MotionProperties.Count==2 && Convert.ToDouble(presetWallpaper.MotionProperties["brightness"])==0.8 &&
            Convert.ToString(presetWallpaper.MotionProperties["asset"])==Path.Combine(presetDirectory,"texture.png").Replace('\\','/'),
            "presets resolve local asset settings and exclude text or unknown properties");
        Check(wallpapers.Any(x=>x.Name=="Missing Preset" && !String.IsNullOrEmpty(x.UnavailableReason)) &&
            wallpapers.Any(x=>x.Name=="Incomplete Scene" && !String.IsNullOrEmpty(x.UnavailableReason)),
            "missing dependencies and incomplete resources are diagnosed without hiding their entries");
        Check(WallpaperCatalog.Discover(new [] {projectsRoot}).Select(x=>x.Id).OrderBy(x=>x).SequenceEqual(wallpapers.Select(x=>x.Id).OrderBy(x=>x)),
            "repeated discovery yields stable wallpaper identifiers");
        var serializedPreset=MotionHost.PropertiesJson(new Dictionary<string,object>{{"volume",100},{"text","a)~ENDb"}});
        var decodedPreset=new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(serializedPreset) as Dictionary<string,object>;
        Check(Convert.ToInt32(decodedPreset["volume"])==0 && Convert.ToString(decodedPreset["text"])=="a)~ENDb" && !serializedPreset.Contains(")~END"),
            "preset settings preserve text, stay inside the JSON argument and mute only the Codex background");
        var missingPreset=wallpapers.First(x=>!String.IsNullOrEmpty(x.UnavailableReason));
        var unavailableWallpaper=NewService("unavailable-wallpaper");
        Check(!unavailableWallpaper.ApplyTheme(missingPreset,false).Success && unavailableWallpaper.Calls.Count==0,
            "unavailable wallpaper entries remain visible without launching an unsupported source");
        var currentVideo=wallpapers.First(x=>x.MediaKind=="video");
        var sourceField=typeof(MotionHost).GetField("source",BindingFlags.NonPublic|BindingFlags.Instance);
        var snapshotField=typeof(MotionHost).GetField("latestFrame",BindingFlags.NonPublic|BindingFlags.Instance);
        var loadedSource=new MotionHost.MotionSource {Kind="video",Path="F:\\test-wallpaper\\test.mp4",Properties=MotionHost.PropertiesJson(new Dictionary<string,object>{{"speed",1},{"brightness",100}})};
        mediaHost.UpdateValidatedSource(loadedSource);
        var retainedSnapshot=new MotionHost.FrameSnapshot(firstJpeg,frameId,loadedSource,160,90,Stopwatch.GetTimestamp(),1);
        snapshotField.SetValue(mediaHost,retainedSnapshot);
        var retainedPolicy=(MotionHost.CaptureModePolicy)typeof(MotionHost).GetField("captureMode",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(mediaHost);
        retainedPolicy.ClientUnsupported(firstWindow);
        mediaHost.UpdateValidatedSource(new MotionHost.MotionSource {Kind="video",Path=loadedSource.Path,Properties=MotionHost.PropertiesJson(new Dictionary<string,object>{{"brightness",100},{"speed",1}})});
        Check(loadedSource!=null && ReferenceEquals(sourceField.GetValue(mediaHost),loadedSource) && ReferenceEquals(snapshotField.GetValue(mediaHost),retainedSnapshot) && !retainedPolicy.PreferClient(firstWindow),
            "reloading equivalent source properties preserves the playing source and its already captured frame");
        mediaHost.UpdateValidatedSource(new MotionHost.MotionSource {Kind="video",Path=loadedSource.Path,Properties=MotionHost.PropertiesJson(new Dictionary<string,object>{{"speed",2},{"brightness",100}})});
        Check(!ReferenceEquals(sourceField.GetValue(mediaHost),loadedSource) && snapshotField.GetValue(mediaHost)==null && retainedPolicy.PreferClient(firstWindow),
            "a real source property change invalidates the previous frame before the replacement can be served");
        var config=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string,object>{
            {"test-user",new {general=new {wallpaperconfig=new {selectedwallpapers=new {Monitor0=new {file=currentVideo.MediaPath}}}}}}
        });
        Check(WallpaperCatalog.CurrentFromConfig(config,"test-user",wallpapers).Id==currentVideo.Id,"current wallpaper config maps to the injected catalog");
        Check(WallpaperCatalog.CurrentFromConfig(config,"wrong-user",wallpapers)==null,"another user profile is not selected accidentally");
        Check(WallpaperCatalog.CurrentFromConfig(config.Replace("Monitor0","Monitor2"),"test-user",wallpapers).Id==currentVideo.Id,
            "current wallpaper config falls back to another configured monitor");
        Check(WallpaperCatalog.CurrentFromConfig("{}","test-user",wallpapers)==null,
            "missing current-wallpaper metadata returns no selection");

        Application.EnableVisualStyles();
        var gui=NewService("gui");
        SaveTestTheme(gui,"A","Portable Test Alpha"); SaveTestTheme(gui,"B","Portable Test Beta");
        using(var form=new MainForm(gui,false)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual;
            form.Location=new Point(-3000,-3000); form.Show(); Application.DoEvents();
            var list=(ListBox)typeof(MainForm).GetField("themeList",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
            list.Items.Clear();
            list.Items.Add(new ThemeItem { Id="A", Name="Theme A", Directory="A", Source="saved" });
            list.Items.Add(b); list.SelectedIndex=1;
            var task=(Task)typeof(MainForm).GetMethod("StartClicked",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
            var deadline=DateTime.UtcNow.AddSeconds(20);
            while(!task.IsCompleted && DateTime.UtcNow<deadline) { Application.DoEvents(); Thread.Sleep(15); }
            Check(task.IsCompleted,"GUI start action completes"); task.GetAwaiter().GetResult();
            Check(gui.Calls[0].Contains(b.Directory),"GUI start button applies B when B is selected, not active A");
            var search=(TextBox)typeof(MainForm).GetField("themeSearch",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
            search.Text="Portable Test Alpha";
            Check(list.Items.Count==1 && ((ThemeItem)list.Items[0]).Id=="A",
                "controller search filters saved fixture themes without an installed wallpaper library");
            search.Clear();
            Check(list.Items.Cast<ThemeItem>().Any(x=>x.Id=="A") && list.Items.Cast<ThemeItem>().Any(x=>x.Id=="B"),
                "clearing search restores the complete saved fixture library");
        }
        var launcher=NewService("saved-launcher");
        SaveTestTheme(launcher,"A","Portable Test Alpha");
        using(var form=new MainForm(launcher,true,false)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual;
            form.Location=new Point(-3000,-3000); form.Show(); Application.DoEvents();
            var gate=(SemaphoreSlim)typeof(MainForm).GetField("operation",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(gate.CurrentCount==0 && DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(15);}
            Check(launcher.Calls.Count>=2 && launcher.Calls[0].Contains(Path.Combine(launcher.SavedThemesRoot,"A")),
                "automatic GUI startup applies the active saved fixture theme");
            Check(!launcher.Calls.Any(x=>x.Contains("-StartPaused")),"skin launcher never requests StartPaused");
        }
        Console.WriteLine("Passed " + count + " behavioral checks; no live Codex process was changed.");
        return 0;
    }
    static byte[] MediaRequest(MotionHost host,string path) {
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        try {
            var handler=Task.Run(()=>{
                using(var accepted=listener.AcceptTcpClient())
                    typeof(MotionHost).GetMethod("HandleClient",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(host,new object[]{accepted});
            });
            using(var client=new TcpClient()) {
                client.ReceiveTimeout=2000;
                client.Connect(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
                var stream=client.GetStream();
                byte[] request=System.Text.Encoding.ASCII.GetBytes("GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:47866\r\nConnection: close\r\n\r\n");
                stream.Write(request,0,request.Length);
                using(var response=new MemoryStream()) {
                    stream.CopyTo(response);
                    handler.GetAwaiter().GetResult();
                    return response.ToArray();
                }
            }
        } finally { listener.Stop(); }
    }
}

internal sealed class ProcessProbeService : ControllerService {
    internal ProcessProbeService(string root):base(root){}
    internal ProcessResult Invoke(string script){return base.RunPowerShell(script,new string[0],15000);}
}

internal sealed class ConnectionProbeService : ControllerService {
    internal int Probes;
    internal RuntimeConnection Connection=new RuntimeConnection {Code="no_endpoint",Port=9335};
    internal ConnectionProbeService(string root):base(root){}
    protected override RuntimeConnection DiscoverConnection(int preferredPort){Probes++;return Connection;}
}
