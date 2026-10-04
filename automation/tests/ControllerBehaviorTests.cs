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
    public override ControllerState GetState() { return State; }
    protected override ProcessResult RunPowerShell(string script, string[] args, int timeout) {
        Calls.Add(Path.GetFileName(script) + " " + String.Join("|", args));
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
        if(args.Length==0) throw new ArgumentException("Pass an isolated test-state directory.");
        root=args[0]; Directory.CreateDirectory(root);
        var now = new DateTime(2026,10,3,12,0,0,DateTimeKind.Utc);
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
        Check(hot.ConnectCurrent().Success && hot.Calls.Single().Contains("-ApplyCurrentTheme"),"existing compatible Codex is attached without a launch command");

        var closed=NewService("closed"); closed.State=new ControllerState();
        Check(closed.ApplyTheme(b,false).Success && closed.Calls[1].StartsWith("start-dream-skin.ps1"),"selected saved theme starts even if Codex was closed");

        var off=NewService("off");
        Check(off.PauseSkin().Success && off.Calls.Single().Contains("-RemoveSkin"),"off command removes and verifies live skin");
        var failedOff=NewService("failed-off"); failedOff.Failure=2;
        Check(!failedOff.PauseSkin().Success,"failed removal verification is reported as failure");
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
