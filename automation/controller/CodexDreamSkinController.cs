using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexDreamSkinController
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool autoStart = HasArgument(args, "--start");
            bool silent = HasArgument(args, "--silent");
            bool native = HasArgument(args, "--native");
            bool selfTest = HasArgument(args, "--self-test");
            bool motionHost = HasArgument(args, "--motion-host");
            bool supervise = HasArgument(args, "--supervise");
            bool currentWallpaper = HasArgument(args, "--wallpaper-current");
            ControllerService service;
            try
            {
                string motionStateRoot = ArgumentValue(args, "--motion-state-root");
                service = motionHost && !String.IsNullOrWhiteSpace(motionStateRoot)
                    ? new ControllerService(motionStateRoot) : new ControllerService();
            }
            catch (Exception initializationError)
            {
                try
                {
                    string crashPath = Path.Combine(Path.GetTempPath(), "codex-dream-skin-controller-crash.txt");
                    string detail = initializationError.GetType().FullName + ": " + initializationError.Message + Environment.NewLine + initializationError.StackTrace;
                    File.WriteAllText(crashPath, detail, new UTF8Encoding(false));
                }
                catch { }
                if (!silent) MessageBox.Show("控制器初始化失败：" + initializationError.Message, "Codex Dream Skin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (selfTest)
            {
                try
                {
                    File.WriteAllText(service.SelfTestPath, service.SelfTestJson() + Environment.NewLine, new UTF8Encoding(false));
                    return service.PrerequisitesReady ? 0 : 2;
                }
                catch (Exception selfTestError)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "codex-dream-skin-controller-selftest-crash.txt"),
                            selfTestError.GetType().FullName + ": " + selfTestError.Message + Environment.NewLine + selfTestError.StackTrace,
                            new UTF8Encoding(false));
                    }
                    catch { }
                    return 1;
                }
            }

            if (motionHost)
                return MotionHost.Run(service, args);
            if (supervise)
                return RecoveryAgent.Run(service);

            if (native)
            {
                StartResult nativeResult = service.StartNative();
                service.RecordResult(nativeResult);
                if (!nativeResult.Success && !silent)
                    MessageBox.Show(nativeResult.Message, "Codex Dream Skin", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return nativeResult.Success ? 0 : 2;
            }

            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex instanceMutex = new Mutex(false, "Local\\CodexDreamSkin.NativeController." + sid))
            {
                bool ownsMutex = false;
                try
                {
                    try { ownsMutex = instanceMutex.WaitOne(0); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex)
                    {
                        // A launch request still needs to do its work when the
                        // library window is already open. Do not report a no-op
                        // as a successful hot start.
                        if (autoStart || silent)
                        {
                            StartResult existingResult = currentWallpaper
                                ? service.ApplyTheme(WallpaperCatalog.Current(), false)
                                : service.StartSkin(false, true);
                            service.RecordResult(existingResult);
                            if (!existingResult.Success && !silent)
                                MessageBox.Show(existingResult.Message, "Codex Dream Skin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return existingResult.Success ? 0 : 2;
                        }
                        if (!silent)
                        {
                            MessageBox.Show("Codex 皮肤控制器已经在运行。", "Codex Dream Skin",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        return 0;
                    }

                    if (silent)
                    {
                        StartResult result = currentWallpaper ? service.ApplyTheme(WallpaperCatalog.Current(), false) : service.StartSkin(false, true);
                        service.RecordResult(result);
                        return result.Success ? 0 : 2;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(service, autoStart, currentWallpaper));
                    return 0;
                }
                catch (Exception ex)
                {
                    service.Log("fatal: " + ex.Message);
                    if (!silent)
                    {
                        MessageBox.Show(ex.Message, "Codex Dream Skin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return 1;
                }
                finally
                {
                    if (ownsMutex) { try { instanceMutex.ReleaseMutex(); } catch { } }
                }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (string arg in args)
            {
                if (String.Equals(arg, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string ArgumentValue(string[] args, string expected)
        {
            for (int index = 0; index + 1 < args.Length; index++)
                if (String.Equals(args[index], expected, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }
    }

    internal class ControllerService
    {
        private const int DefaultPort = 9335;
        private int connectionPort = DefaultPort;
        private int Port { get { return connectionPort; } }
        private RuntimeConnection cachedConnection;
        private DateTime connectionCheckedUtc = DateTime.MinValue;
        private const string AutoStartName = "CodexDreamSkinNative";
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly string stateRoot;
        private readonly string controlRoot;
        private readonly string projectRoot;
        private readonly string engineScripts;
        private readonly string startScript;
        private readonly string verifyScript;
        private readonly string verifyLog;
        private readonly string restoreScript;
        private readonly string applySavedScript;
        private readonly string pauseFile;
        private readonly string logFile;
        private readonly string runtimeVersion;
        private readonly string codexVersion;
        private readonly string compatProfile;

        public ControllerService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDreamSkin")) { }

        internal ControllerService(string root)
        {
            stateRoot = root;
            controlRoot = Path.Combine(stateRoot, "control");
            Directory.CreateDirectory(controlRoot);
            projectRoot = ResolveProjectRoot();
            engineScripts = Path.Combine(stateRoot, "engine", "scripts");
            startScript = Path.Combine(engineScripts, "start-dream-skin.ps1");
            verifyScript = Path.Combine(engineScripts, "verify-dream-skin.ps1");
            verifyLog = Path.Combine(stateRoot, "verify.log");
            restoreScript = Path.Combine(engineScripts, "restore-dream-skin.ps1");
            applySavedScript = Path.Combine(controlRoot, "apply-saved-theme.ps1");
            if (!File.Exists(applySavedScript)) {
                applySavedScript = Path.Combine(projectRoot, "automation", "controller", "apply-saved-theme.ps1");
            }
            pauseFile = Path.Combine(stateRoot, "paused");
            logFile = Path.Combine(controlRoot, "native-controller.log");
            runtimeVersion = ReadRuntimeVersion();
            codexVersion = DetectCodexVersion();
            compatProfile = CompatProfileFor(codexVersion);
            try { WallpaperCatalog.SaveRoots(controlRoot); }
            catch (Exception error) { Log("wallpaper library configuration: " + error.Message); }
        }

        public virtual bool PrerequisitesReady
        {
            get { return File.Exists(startScript) && File.Exists(verifyScript) && File.Exists(restoreScript) && File.Exists(applySavedScript); }
        }

        public string ProjectRoot { get { return projectRoot; } }
        public string WallpaperControlRoot { get { return controlRoot; } }
        public string StateRoot { get { return stateRoot; } }
        public string SavedThemesRoot { get { return Path.Combine(stateRoot, "themes"); } }
        public string RuntimeVersion { get { return runtimeVersion; } }
        public string CodexVersion { get { return codexVersion; } }
        public string CompatProfile { get { return compatProfile; } }
        public string SelfTestPath { get { return Path.Combine(Path.GetTempPath(), "codex-dream-skin-controller-self-test.json"); } }

        public virtual ControllerState GetState()
        {
            ControllerState result = new ControllerState();
            result.Paused = File.Exists(pauseFile);
            result.ActiveThemeName = "未找到活动主题";
            result.ActiveThemeId = "";

            Dictionary<string, object> active = ReadJsonObject(Path.Combine(stateRoot, "active-theme", "theme.json"));
            if (active != null)
            {
                result.ActiveThemeId = GetString(active, "id", "");
                result.ActiveThemeName = GetString(active, "name", result.ActiveThemeId);
            }

            Dictionary<string, object> state = ReadJsonObject(Path.Combine(stateRoot, "state.json"));
            if (state != null)
            {
                int pid = GetInt(state, "injectorPid", 0);
                string startedAt = GetString(state, "injectorStartedAt", "");
                result.InjectorPid = pid;
                result.InjectorRunning = IsRecordedProcessAlive(pid, startedAt, "node");
            }
            if (state != null && cachedConnection == null) connectionPort = GetInt(state, "port", DefaultPort);
            RuntimeConnection connection = ProbeConnection();
            result.CdpReady = connection.Ready;
            result.CdpPort = connection.Port;
            result.ConnectionCode = connection.Code;
            result.ConnectionMessage = connection.Message;
            result.CodexSessionKey = connection.BrowserId;
            result.InjectorSessionMatches = result.InjectorRunning && state != null &&
                RuntimeConnection.MatchesSession(GetString(state, "browserId", ""), GetInt(state, "port", 0), connection);
            result.RecoveryStatus = GetString(ReadJsonObject(Path.Combine(controlRoot, "recovery-status.json")), "status", "");
            result.CodexRunning = IsCodexRunning();
            return result;
        }

        public List<ThemeItem> GetThemes()
        {
            List<ThemeItem> result = new List<ThemeItem>();
            if (Directory.Exists(SavedThemesRoot))
            {
                foreach (string directory in Directory.GetDirectories(SavedThemesRoot))
                {
                    try
                    {
                        Dictionary<string, object> theme = ReadJsonObject(Path.Combine(directory, "theme.json"));
                        if (theme == null) continue;
                        string id = GetString(theme, "id", Path.GetFileName(directory));
                        if (id.StartsWith("we-", StringComparison.OrdinalIgnoreCase)) continue;
                        string name = GetString(theme, "name", id);
                        result.Add(new ThemeItem { Id = id, Name = name, Directory = directory, Source = "saved" });
                    }
                    catch { }
                }
            }
            result.AddRange(WallpaperCatalog.Discover());
            result.Sort(delegate(ThemeItem left, ThemeItem right)
            {
                int source = String.Compare(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);
                if (source != 0) return source;
                return String.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        public StartResult StartSkin(bool allowOneRestart, bool silent)
        {
            StartResult result = StartRuntime(false, silent);
            if (result.Success) EnsureRecoveryAgent();
            return result;
        }

        public ControllerState GetFreshState()
        {
            connectionCheckedUtc = DateTime.MinValue;
            return GetState();
        }

        public virtual void EnsureRecoveryAgent() { RecoveryAgent.EnsureStarted(this); }

        public virtual StartResult RecoverConnection()
        {
            connectionCheckedUtc = DateTime.MinValue;
            ControllerState state = GetState();
            if (state.Paused) return StartResult.Ok("皮肤保持关闭。", false);
            if (!state.CodexRunning || !state.CdpReady)
                return StartResult.Fail("等待可连接的 Codex 会话；不会自动重启 Codex。");
            return StartRuntime(false, true, true);
        }

        public virtual bool IsMediaHostReady()
        {
            var theme = ReadJsonObject(Path.Combine(stateRoot, "active-theme", "theme.json"));
            object motion;
            return theme == null || !theme.TryGetValue("motion", out motion) ||
                !(motion is Dictionary<string, object>) || MotionHostReady();
        }

        public virtual StartResult VerifyCurrentConnection()
        {
            connectionCheckedUtc = DateTime.MinValue;
            ControllerState state = GetState();
            if (state.Paused) return StartResult.Ok("皮肤保持关闭。", false);
            if (!state.CdpReady || !state.InjectorSessionMatches)
                return StartResult.Fail("当前皮肤进程未绑定到有效的 Codex 会话，需要重新连接。");
            ProcessResult verified = RunPowerShell(verifyScript,
                new string[] { "-Port", Port.ToString(CultureInfo.InvariantCulture) }, 60000);
            return verified.ExitCode == 0 ? StartResult.Ok("背景与当前 Codex 页面已验证。", false)
                : StartResult.Fail("页面兼容检查未通过，将自动尝试恢复连接。");
        }

        public StartResult StartNative()
        {
            ControllerState state = GetState();
            if (state.CodexRunning)
                return StartResult.Ok(state.CdpReady ? "Codex 已运行，已检测到可连接的页面。" : "Codex 已运行，但当前未开放背景接口；尚不能应用皮肤。", false);
            return StartRuntime(true, false);
        }

        public StartResult ConnectCurrent()
        {
            connectionCheckedUtc = DateTime.MinValue;
            ControllerState state = GetState();
            if (!state.CodexRunning) return StartResult.Fail("请先打开 Codex，再检测当前会话。");
            if (!state.CdpReady) return StartResult.Fail(state.ConnectionMessage ?? "当前会话没有可连接的 Codex 页面。");
            return StartSkin(false, false);
        }

        public void RecordResult(StartResult result)
        {
            Log(result.Message);
            try
            {
                File.WriteAllText(Path.Combine(controlRoot, "last-operation.json"), json.Serialize(new Dictionary<string, object> {
                    { "time", DateTimeOffset.Now.ToString("o") }, { "success", result.Success },
                    { "message", result.Message }, { "codex_version", codexVersion }, { "port", Port }
                }), new UTF8Encoding(false));
            }
            catch { }
        }

        public StartResult RefreshWallpaperCatalog()
        {
            return StartResult.Ok("Wallpaper Engine 壁纸列表已刷新。", false);
        }

        private StartResult StartRuntime(bool startPaused, bool silent, bool preservePause = false)
        {
            if (!PrerequisitesReady) return StartResult.Fail("Dream Skin 运行时或工程入口不完整。");
            connectionCheckedUtc = DateTime.MinValue;
            ControllerState before = GetState();
            if (preservePause && (before.Paused || File.Exists(pauseFile))) return StartResult.Ok("皮肤保持关闭。", false);
            if (!before.CdpReady && before.CodexRunning)
            {
                string message = silent
                    ? "Codex 已运行但没有本地皮肤接口；无重启模式已安全退出。"
                    : before.ConnectionMessage ?? "当前 Codex 未开放背景接口，无法热连接。";
                return StartResult.Fail(message);
            }
            if (!preservePause && !startPaused && before.InjectorSessionMatches && before.CdpReady && File.Exists(pauseFile)) File.Delete(pauseFile);

            if (!preservePause && before.InjectorSessionMatches && before.CdpReady)
            {
                if (!EnsureMotionHost(before.InjectorPid)) return StartResult.Fail("动态壁纸服务未能就绪，背景尚未启用。");
                ProcessResult refresh = RunPowerShell(verifyScript, new string[] { "-Port", Port.ToString(CultureInfo.InvariantCulture), "-ApplyCurrentTheme" }, 60000);
                if (refresh.ExitCode != 0) return StartResult.Fail("皮肤刷新失败：" + Compact(refresh.Error));
                return StartResult.Ok("皮肤已刷新；Codex 没有重启。", false);
            }

            List<string> arguments = new List<string>();
            if (startPaused) arguments.Add("-StartPaused");
            if (preservePause) arguments.Add("-PreservePaused");
            arguments.Add("-Port");
            arguments.Add(Port.ToString(CultureInfo.InvariantCulture));
            DateTime startRequestedAtUtc = DateTime.UtcNow;
            ProcessResult start = RunPowerShell(startScript, arguments.ToArray(), 240000);
            if (preservePause && File.Exists(pauseFile)) return StartResult.Ok("皮肤保持关闭。", false);
            if (start.ExitCode != 0)
            {
                string detail = File.Exists(verifyLog) && File.GetLastWriteTimeUtc(verifyLog) >= startRequestedAtUtc ? ReadTechnicalLog(verifyLog) : "";
                return StartResult.Fail("背景连接未完成：" +
                    (String.IsNullOrWhiteSpace(detail) ? Compact(start.Error) : detail));
            }

            connectionCheckedUtc = DateTime.MinValue;
            ControllerState after = GetState();
            DateTime readyDeadline = DateTime.UtcNow.AddSeconds(4);
            while ((!after.InjectorSessionMatches || !after.CdpReady) && DateTime.UtcNow < readyDeadline)
            {
                Thread.Sleep(200);
                connectionCheckedUtc = DateTime.MinValue;
                after = GetState();
            }
            if (!after.InjectorSessionMatches || !after.CdpReady)
            {
                return StartResult.Fail("启动命令已结束，但注入器或本地 CDP 未通过检查；不会继续重试。");
            }
            if (!EnsureMotionHost(after.InjectorPid)) return StartResult.Fail("动态壁纸服务未能就绪，背景尚未启用。");
            return StartResult.Ok(startPaused ? "Codex 已以原生外观启动；之后可随时启用、关闭或切换皮肤。" : "皮肤已启动；Codex 没有被重启。", false);
        }

        private bool EnsureMotionHost(int injectorPid)
        {
            var theme = ReadJsonObject(Path.Combine(stateRoot, "active-theme", "theme.json"));
            object motion;
            if (theme == null || !theme.TryGetValue("motion", out motion) || !(motion is Dictionary<string, object>)) return true;
            if (injectorPid <= 0) return false;
            if (MotionHostReady()) return true;
            string executable = Path.Combine(controlRoot, "CodexDreamSkinController.exe");
            if (!File.Exists(executable)) return false;
            ProcessStartInfo info = new ProcessStartInfo(executable,
                "--motion-host --parent-pid " + injectorPid.ToString(CultureInfo.InvariantCulture));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            try { Process.Start(info); }
            catch (Exception error) { Log("motion host start failed: " + error.Message); return false; }
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            do { if (MotionHostReady()) return true; Thread.Sleep(100); } while (DateTime.UtcNow < deadline);
            return false;
        }

        private static bool MotionHostReady()
        {
            try
            {
                HttpWebRequest probe = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:47866/health");
                probe.Timeout = 500;
                probe.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)probe.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                    return response.StatusCode == HttpStatusCode.OK && reader.ReadToEnd() == "codex-dream-skin-motion/1";
            }
            catch { }
            return false;
        }

        public StartResult ApplyTheme(ThemeItem item, bool allowOneRestart)
        {
            if (item == null) return StartResult.Fail("请先选择主题。");
            if (!String.IsNullOrEmpty(item.UnavailableReason)) return StartResult.Fail(item.UnavailableReason);
            connectionCheckedUtc = DateTime.MinValue;
            ControllerState state = GetState();
            if (state.CodexRunning && !state.CdpReady)
            {
                return StartResult.Fail(state.ConnectionMessage ?? "当前 Codex 没有可连接的背景接口；壁纸尚未应用。");
            }
            if (String.Equals(item.Source, "wallpaper", StringComparison.OrdinalIgnoreCase))
            {
                if (Process.GetProcessesByName("wallpaper64").Length == 0 && Process.GetProcessesByName("wallpaper32").Length == 0)
                    return StartResult.Fail("动态壁纸需要 Wallpaper Engine 保持运行，请先启动它。");
                if (!File.Exists(applySavedScript)) return StartResult.Fail("已保存主题应用脚本不存在。");
                string imported = WallpaperCatalog.Import(item, SavedThemesRoot);
                ProcessResult prepared = RunPowerShell(applySavedScript, new string[] { "-ThemeDirectory", imported }, 60000);
                if (prepared.ExitCode != 0) return StartResult.Fail("导入 Wallpaper Engine 壁纸失败：" + Compact(prepared.Error));
                StartResult enabled = StartSkin(false, false);
                if (!enabled.Success) return StartResult.Fail("壁纸已导入，但启用失败：" + enabled.Message);
                return StartResult.Ok("已将 Wallpaper Engine 壁纸 “" + item.Name + "” 用作 Codex 动态皮肤。", false);
            }
            if (String.Equals(item.Source, "saved", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(applySavedScript)) return StartResult.Fail("已保存主题应用脚本不存在。");
                ProcessResult saved = RunPowerShell(applySavedScript, new string[] { "-ThemeDirectory", item.Directory }, 60000);
                if (saved.ExitCode != 0) return StartResult.Fail("切换已保存主题失败：" + Compact(saved.Error));
                StartResult enabled = StartSkin(false, false);
                if (!enabled.Success) return StartResult.Fail("主题已保存，但启用失败：" + enabled.Message);
                return StartResult.Ok("已启用选中主题 “" + item.Name + "”；Codex 没有重启。", false);
            }
            return StartResult.Fail("此发布版仅支持已保存主题和 Wallpaper Engine 壁纸。");
        }

        public StartResult PauseSkin()
        {
            Directory.CreateDirectory(stateRoot);
            string temporary = pauseFile + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(temporary, "paused by CodexDreamSkinController\r\n", new UTF8Encoding(false));
            if (File.Exists(pauseFile)) File.Delete(temporary);
            else File.Move(temporary, pauseFile);
            ControllerState state = GetState();
            if (!state.CdpReady)
                return StartResult.Ok("已设为关闭；本次 Codex 没有皮肤接口。", false);
            // Let the existing watcher remove early-injection hooks, then verify DOM removal.
            Thread.Sleep(1800);
            ProcessResult removed = RunPowerShell(verifyScript, new string[] {
                "-Port", Port.ToString(CultureInfo.InvariantCulture), "-RemoveSkin"
            }, 60000);
            if (removed.ExitCode != 0)
                return StartResult.Fail("已请求关闭，但未能确认界面已恢复：" + Compact(removed.Error));
            return StartResult.Ok("皮肤已关闭并验证；Codex 保持运行，可随时重新启用。", false);
        }

        public StartResult RestoreOfficial()
        {
            if (IsCodexRunning())
            {
                return PauseSkin();
            }
            ProcessResult result = RunPowerShell(restoreScript, new string[] {
                "-Port", Port.ToString(CultureInfo.InvariantCulture), "-RestoreBaseTheme"
            }, 120000);
            if (result.ExitCode != 0) return StartResult.Fail("恢复官方外观失败：" + Compact(result.Error));
            return StartResult.Ok("已恢复官方外观。", true);
        }

        public bool IsAutoStartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", false))
            {
                return key != null && key.GetValue(AutoStartName) != null;
            }
        }

        public void SetAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                if (enabled)
                {
                    string executable = Application.ExecutablePath;
                    key.SetValue(AutoStartName, Quote(executable) + " --supervise", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(AutoStartName, false);
                }
            }
            Log("autostart=" + enabled.ToString());
        }

        public string SelfTestJson()
        {
            ControllerState state = GetState();
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["status"] = PrerequisitesReady ? "pass" : "fail";
            report["controller_version"] = "3.6.3";
            report["automatic_recovery"] = true;
            report["injector_session_matches"] = state.InjectorSessionMatches;
            report["periodic_readonly_render_check"] = true;
            report["native_executable"] = Application.ExecutablePath;
            report["project_root"] = projectRoot;
            report["runtime_version"] = runtimeVersion;
            report["codex_version"] = codexVersion;
            report["compat_profile"] = compatProfile;
            report["start_script_exists"] = File.Exists(startScript);
            report["verify_script_exists"] = File.Exists(verifyScript);
            report["restore_script_exists"] = File.Exists(restoreScript);
            report["apply_saved_script_exists"] = File.Exists(applySavedScript);
            report["injector_running"] = state.InjectorRunning;
            report["cdp_ready"] = state.CdpReady;
            report["connection_code"] = state.ConnectionCode;
            report["connection_message"] = state.ConnectionMessage;
            report["connection_port"] = state.CdpPort;
            report["ready_for_hot_apply"] = state.CodexRunning && state.CdpReady;
            report["codex_running"] = state.CodexRunning;
            report["active_theme"] = state.ActiveThemeId;
            report["selectable_theme_count"] = GetThemes().Count;
            report["auto_start_enabled"] = IsAutoStartEnabled();
            report["unified_entry"] = true;
            report["never_restarts_codex"] = true;
            report["wallpaper_engine_theme_count"] = WallpaperCatalog.Discover().Count;
            return json.Serialize(report);
        }

        public void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(controlRoot);
                File.AppendAllText(logFile, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + " " + message.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        private string ReadRuntimeVersion()
        {
            try
            {
                string injector = Path.Combine(engineScripts, "injector.mjs");
                if (!File.Exists(injector)) return "unknown";
                Match match = Regex.Match(File.ReadAllText(injector, Encoding.UTF8),
                    "SKIN_VERSION\\s*=\\s*\"([0-9.]+)\"");
                return match.Success ? match.Groups[1].Value : "unknown";
            }
            catch { return "unknown"; }
        }

        private static string DetectCodexVersion()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("ChatGPT"))
                {
                    try
                    {
                        string path = process.MainModule != null ? process.MainModule.FileName : "";
                        Match match = Regex.Match(path ?? "", @"OpenAI\.Codex_(\d+\.\d+\.\d+\.\d+)_");
                        if (match.Success) return match.Groups[1].Value;
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "powershell.exe";
                info.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"(Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1 -ExpandProperty Version)\"";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(); } catch { }
                        return "";
                    }
                    Version parsed;
                    if (Version.TryParse(output, out parsed)) return output;
                }
            }
            catch { }
            return "";
        }

        private static string CompatProfileFor(string versionText)
        {
            Version version;
            if (String.IsNullOrWhiteSpace(versionText) || !Version.TryParse(versionText, out version)) return "探测中";
            if (version.Major > 26 || (version.Major == 26 && version.Minor >= 903)) return "26.903+";
            if (version.Major == 26 && version.Minor >= 803) return "26.803";
            if (version.Major == 26 && version.Minor >= 727) return "26.727";
            return "legacy";
        }

        private string ResolveProjectRoot()
        {
            string config = Path.Combine(controlRoot, "control-config.json");
            Dictionary<string, object> value = ReadJsonObject(config);
            if (value != null)
            {
                string configured = GetString(value, "project_root", "");
                if (Directory.Exists(configured)) return Path.GetFullPath(configured);
            }
            DirectoryInfo candidate = new DirectoryInfo(Application.StartupPath);
            while (candidate != null)
            {
                if (File.Exists(Path.Combine(candidate.FullName, "windows", "scripts", "injector.mjs")))
                    return candidate.FullName;
                candidate = candidate.Parent;
            }
            return Application.StartupPath;
        }

        private bool IsRecordedProcessAlive(int pid, string startedAt, string expectedName)
        {
            if (pid <= 0 || String.IsNullOrWhiteSpace(startedAt)) return false;
            try
            {
                Process process = Process.GetProcessById(pid);
                if (process.HasExited || process.ProcessName.IndexOf(expectedName, StringComparison.OrdinalIgnoreCase) < 0) return false;
                DateTime saved = DateTime.Parse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                TimeSpan delta = process.StartTime.ToUniversalTime() - saved.ToUniversalTime();
                return Math.Abs(delta.TotalSeconds) < 2.0;
            }
            catch { return false; }
        }

        private RuntimeConnection ProbeConnection()
        {
            if (cachedConnection != null && (DateTime.UtcNow - connectionCheckedUtc).TotalSeconds < 5) return cachedConnection;
            cachedConnection = RuntimeConnection.Discover(Port);
            if (cachedConnection.Ready) connectionPort = cachedConnection.Port;
            connectionCheckedUtc = DateTime.UtcNow;
            return cachedConnection;
        }

        private bool IsCodexRunning()
        {
            try { return Process.GetProcessesByName("ChatGPT").Length > 0; }
            catch { return false; }
        }

        protected virtual ProcessResult RunPowerShell(string script, string[] arguments, int timeoutMilliseconds)
        {
            if (!File.Exists(script)) return new ProcessResult { ExitCode = -1, Error = "脚本不存在：" + script };
            StringBuilder command = new StringBuilder();
            command.Append("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ");
            command.Append(Quote(script));
            foreach (string argument in arguments)
            {
                command.Append(" ");
                command.Append(Quote(argument));
            }
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "powershell.exe";
            info.Arguments = command.ToString();
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.WorkingDirectory = projectRoot;
            using (Process process = new Process())
            {
                process.StartInfo = info;
                var outputBuffer = new StringBuilder();
                var errorBuffer = new StringBuilder();
                var outputClosed = new TaskCompletionSource<bool>();
                var errorClosed = new TaskCompletionSource<bool>();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) outputClosed.TrySetResult(true);
                    else lock (outputBuffer) { if (outputBuffer.Length < 262144) outputBuffer.AppendLine(e.Data); }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) errorClosed.TrySetResult(true);
                    else lock (errorBuffer) { if (errorBuffer.Length < 262144) errorBuffer.AppendLine(e.Data); }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    Log("command timeout: " + Path.GetFileName(script));
                    return new ProcessResult { ExitCode = -2, Output = "", Error = "操作超时，已停止控制命令；不会自动重试。" };
                }
                // A launched GUI may inherit these handles after PowerShell exits.
                // EOF is therefore not a reliable completion signal for the command.
                if (!Task.WhenAll(outputClosed.Task, errorClosed.Task).Wait(1000))
                {
                    try { process.CancelOutputRead(); } catch { }
                    try { process.CancelErrorRead(); } catch { }
                }
                string output, error;
                lock (outputBuffer) output = outputBuffer.ToString();
                lock (errorBuffer) error = errorBuffer.ToString();
                Log("command " + Path.GetFileName(script) + " exit=" + process.ExitCode.ToString(CultureInfo.InvariantCulture));
                return new ProcessResult { ExitCode = process.ExitCode, Output = output, Error = String.IsNullOrWhiteSpace(error) ? output : error };
            }
        }

        private Dictionary<string, object> ReadJsonObject(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        private static string GetString(Dictionary<string, object> value, string key, string fallback)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return fallback;
            return Convert.ToString(item, CultureInfo.InvariantCulture);
        }

        private static int GetInt(Dictionary<string, object> value, string key, int fallback)
        {
            object item;
            int result;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return fallback;
            return Int32.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Compact(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "没有错误输出。";
            if (value.IndexOf("did not expose a verified loopback CDP endpoint", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Codex 已启动，但当前版本没有提供可连接的背景接口；壁纸尚未应用。";
            List<string> useful = new List<string>();
            foreach (string rawLine in value.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("At ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("所在位置", StringComparison.OrdinalIgnoreCase) || line.StartsWith("+ ") ||
                    line.StartsWith("~") || line.StartsWith("CategoryInfo", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase)) continue;
                useful.Add(line);
                if (useful.Count == 3) break;
            }
            string compact = String.Join(" ", useful.ToArray());
            if (compact.Length == 0) compact = "命令失败，详细信息已写入日志。";
            return compact.Length > 420 ? compact.Substring(0, 420) + "…" : compact;
        }

        private static string ReadTechnicalLog(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                foreach (string rawLine in File.ReadLines(path, Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) continue;
                    string result = line.Substring(6).Trim();
                    return result.Length > 420 ? result.Substring(0, 420) + "…" : result;
                }
            }
            catch { }
            return "";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    internal sealed class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, string text);
        private readonly ControllerService service;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly Label statusLabel;
        private readonly ListBox themeList;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Button applyButton;
        private readonly Button refreshButton;
        private readonly Button previewButton;
        private readonly Button officialButton;
        private readonly Button folderButton;
        private readonly Button nativeButton;
        private readonly Button wallpaperButton;
        private readonly PictureBox previewImage;
        private readonly Label previewCaption;
        private readonly TextBox themeSearch;
        private readonly Label libraryHint;
        private List<ThemeItem> cachedThemes = new List<ThemeItem>();
        private readonly CheckBox autoStart;
        private readonly System.Windows.Forms.Timer timer;
        private bool suppressAutoStartEvent;
        private bool startOnShow;
        private string activeThemeId;

        public MainForm(ControllerService service, bool startOnShow, bool currentWallpaper = false)
        {
            this.service = service;
            this.startOnShow = startOnShow;
            Text = "Codex 皮肤控制器";
            // Match the launcher and embedded executable artwork in WinForms.
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 674);
            MinimumSize = new Size(916, 713);
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = Color.FromArgb(244, 247, 251);
            ForeColor = Color.FromArgb(34, 48, 68);

            Panel header = new Panel();
            header.Location = new Point(0, 0);
            header.Size = new Size(900, 102);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.BackColor = Color.FromArgb(19, 33, 54);
            header.Paint += delegate(object sender, PaintEventArgs e) {
                using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    header.ClientRectangle, Color.FromArgb(19, 33, 54), Color.FromArgb(37, 62, 91), 0F))
                    e.Graphics.FillRectangle(brush, header.ClientRectangle);
                using (Brush accent = new SolidBrush(Color.FromArgb(87, 205, 189)))
                    e.Graphics.FillRectangle(accent, 28, 25, 4, 47);
            };
            Controls.Add(header);

            Label title = new Label();
            title.Text = "Codex Dream Skin";
            title.Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.BackColor = Color.Transparent;
            title.Location = new Point(46, 19);
            title.AutoSize = true;
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "你的主题库  /  Wallpaper Engine 动态壁纸  /  连接状态";
            subtitle.Location = new Point(48, 65);
            subtitle.AutoSize = true;
            subtitle.BackColor = Color.Transparent;
            subtitle.ForeColor = Color.FromArgb(182, 205, 225);
            header.Controls.Add(subtitle);

            statusLabel = new Label();
            statusLabel.Location = new Point(28, 122);
            statusLabel.Size = new Size(844, 82);
            statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            statusLabel.Padding = new Padding(16, 10, 12, 8);
            statusLabel.Font = new Font("Microsoft YaHei UI", 9.5F);
            statusLabel.BackColor = Color.White;
            Controls.Add(statusLabel);

            Label selectLabel = new Label();
            selectLabel.Text = "皮肤库";
            selectLabel.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            selectLabel.Location = new Point(28, 223);
            selectLabel.AutoSize = true;
            Controls.Add(selectLabel);

            libraryHint = new Label();
            libraryHint.Text = "全部 Wallpaper Engine 壁纸";
            libraryHint.Location = new Point(108, 228);
            libraryHint.ForeColor = Color.FromArgb(115, 130, 150);
            libraryHint.AutoSize = true;
            Controls.Add(libraryHint);

            themeSearch = new TextBox();
            themeSearch.Location = new Point(675, 221);
            themeSearch.Size = new Size(197, 28);
            themeSearch.BorderStyle = BorderStyle.FixedSingle;
            themeSearch.Font = new Font("Microsoft YaHei UI", 9F);
            themeSearch.TextChanged += delegate { RefreshThemeItems(); };
            Controls.Add(themeSearch);
            SendMessage(themeSearch.Handle, 0x1501, IntPtr.Zero, "搜索主题或壁纸");

            Button allThemes = new Button();
            allThemes.Text = "全部";
            allThemes.Location = new Point(617, 221);
            allThemes.Size = new Size(50, 28);
            allThemes.FlatStyle = FlatStyle.Flat;
            allThemes.FlatAppearance.BorderColor = Color.FromArgb(215, 224, 234);
            allThemes.BackColor = Color.White;
            allThemes.Click += delegate { themeSearch.Clear(); RefreshView(true); };
            Controls.Add(allThemes);

            themeList = new ListBox();
            themeList.Location = new Point(28, 258);
            themeList.Size = new Size(526, 196);
            themeList.DisplayMember = "Display";
            themeList.DrawMode = DrawMode.OwnerDrawFixed;
            themeList.ItemHeight = 48;
            themeList.BorderStyle = BorderStyle.None;
            themeList.BackColor = Color.White;
            themeList.DrawItem += DrawThemeItem;
            themeList.SelectedIndexChanged += delegate {
                LoadSelectedPreview();
                ThemeItem item = themeList.SelectedItem as ThemeItem;
                if (startButton != null && operation.CurrentCount > 0)
                    startButton.Enabled = item != null && String.IsNullOrEmpty(item.UnavailableReason);
            };
            Controls.Add(themeList);

            Panel previewPanel = new Panel();
            previewPanel.Location = new Point(572, 258);
            previewPanel.Size = new Size(300, 196);
            previewPanel.BackColor = Color.FromArgb(26, 42, 64);
            Controls.Add(previewPanel);
            previewImage = new PictureBox();
            previewImage.Dock = DockStyle.Fill;
            previewImage.SizeMode = PictureBoxSizeMode.Zoom;
            previewImage.BackColor = previewPanel.BackColor;
            previewPanel.Controls.Add(previewImage);
            previewCaption = new Label();
            previewCaption.Text = "选择皮肤查看预览";
            previewCaption.TextAlign = ContentAlignment.MiddleCenter;
            previewCaption.ForeColor = Color.FromArgb(210, 224, 238);
            previewCaption.BackColor = Color.FromArgb(26, 42, 64);
            previewCaption.Dock = DockStyle.Bottom;
            previewCaption.Height = 30;
            previewPanel.Controls.Add(previewCaption);
            previewCaption.BringToFront();

            startButton = MakeButton("启用选中皮肤", 28, 476, 192, true);
            stopButton = MakeButton("关闭皮肤", 232, 476, 134, false);
            applyButton = MakeButton("使用当前桌面壁纸", 378, 476, 174, false);
            refreshButton = MakeButton("刷新状态", 564, 476, 134, false);
            previewButton = MakeButton("大图预览", 710, 476, 162, false);
            officialButton = MakeButton("恢复官方外观", 28, 532, 162, false);
            folderButton = MakeButton("打开主题目录", 202, 532, 162, false);

            nativeButton = MakeButton("检测并连接已打开的 Codex", 376, 532, 496, false);
            nativeButton.Click += async delegate { await RunOperation("正在检测当前 Codex 的背景接口……", service.ConnectCurrent); };

            wallpaperButton = MakeButton("刷新 Wallpaper Engine 壁纸", 28, 594, 240, false);
            wallpaperButton.Click += async delegate { await RunOperation("正在读取 Wallpaper Engine 壁纸库……", service.RefreshWallpaperCatalog); };

            autoStart = new CheckBox();
            autoStart.Text = "随 Windows 登录自动恢复连接";
            autoStart.Location = new Point(290, 602);
            autoStart.AutoSize = true;
            Controls.Add(autoStart);

            Label version = new Label();
            version.Text = "控制器 3.6.3 · Wallpaper";
            version.Location = new Point(682, 606);
            version.AutoSize = true;
            version.ForeColor = Color.FromArgb(121, 138, 157);
            Controls.Add(version);

            startButton.Click += async delegate { await StartClicked(); };
            stopButton.Click += async delegate { await RunOperation("正在关闭皮肤……", service.PauseSkin); };
            applyButton.Click += async delegate { await ApplyClicked(); };
            refreshButton.Click += delegate { RefreshView(true); };
            previewButton.Click += delegate { OpenPreview(); };
            folderButton.Click += delegate { OpenThemeFolder(); };
            officialButton.Click += async delegate { await RestoreOfficialClicked(); };
            autoStart.CheckedChanged += AutoStartChanged;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += delegate { if (operation.CurrentCount > 0) RefreshView(false); };
            Shown += async delegate {
                RefreshView(true);
                timer.Start();
                if (service.GetState().InjectorRunning) service.EnsureRecoveryAgent();
                if (currentWallpaper)
                {
                    ThemeItem selected = null;
                    try { selected = WallpaperCatalog.Current(); }
                    catch (Exception error) { ShowError(error.Message); return; }
                    if (selected == null) { ShowError("找不到 Wallpaper Engine 当前壁纸，请先在皮肤库中选择。"); return; }
                    themeSearch.Clear();
                    for (int index = 0; index < themeList.Items.Count; index++)
                    {
                        ThemeItem candidate = themeList.Items[index] as ThemeItem;
                        if (candidate != null && candidate.Source == "wallpaper" && candidate.Id == selected.Id) { themeList.SelectedIndex = index; break; }
                    }
                }
                if (this.startOnShow)
                {
                    this.startOnShow = false;
                    await StartClicked();
                }
            };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); operation.Dispose(); if (previewImage.Image != null) previewImage.Image.Dispose(); };
        }

        private Button MakeButton(string text, int x, int y, int width, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, 44);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(215, 224, 234);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(29, 139, 145) : Color.FromArgb(231, 240, 247);
            button.BackColor = primary ? Color.FromArgb(22, 119, 128) : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(43, 62, 81);
            button.Cursor = Cursors.Hand;
            button.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            Controls.Add(button);
            return button;
        }

        private void DrawThemeItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= themeList.Items.Count) return;
            ThemeItem item = themeList.Items[e.Index] as ThemeItem;
            if (item == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Rectangle row = e.Bounds;
            using (Brush background = new SolidBrush(selected ? Color.FromArgb(224, 243, 244) : Color.White))
                e.Graphics.FillRectangle(background, row);
            using (Brush stripe = new SolidBrush(selected ? Color.FromArgb(22, 119, 128) : Color.FromArgb(225, 232, 239)))
                e.Graphics.FillRectangle(stripe, row.X, row.Y + 5, 3, row.Height - 10);
            string caption = item.Name + (item.Id == activeThemeId ? "  ·  当前使用" : "");
            string mediaKind = item.MediaKind == "video" ? "视频" : item.MediaKind == "scene" ? "场景" : item.MediaKind == "web" ? "网页" : item.MediaKind == "preset" ? "预设" : "应用";
            if (item.IsPreset) mediaKind += "预设";
            string detail = (item.Source == "wallpaper" ? "Wallpaper Engine · " + mediaKind : item.Source == "saved" ? "已保存" : "其他") + "  /  " + item.Id;
            if (!String.IsNullOrEmpty(item.UnavailableReason)) detail = "暂不可用 · " + item.UnavailableReason;
            using (Font titleFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, caption, titleFont,
                    new Rectangle(row.X + 16, row.Y + 5, row.Width - 24, 22), Color.FromArgb(35, 55, 72), TextFormatFlags.EndEllipsis);
            using (Font detailFont = new Font("Microsoft YaHei UI", 8F))
                TextRenderer.DrawText(e.Graphics, detail, detailFont,
                    new Rectangle(row.X + 16, row.Y + 28, row.Width - 24, 17), Color.FromArgb(119, 136, 151), TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        private void LoadSelectedPreview()
        {
            Image old = previewImage.Image;
            previewImage.Image = null;
            if (old != null) old.Dispose();
            ThemeItem item = themeList.SelectedItem as ThemeItem;
            if (item == null) { previewCaption.Text = "选择皮肤查看预览"; return; }
            previewCaption.Text = String.IsNullOrEmpty(item.UnavailableReason) ? item.Name : item.UnavailableReason;
            string[] candidates = item.PreviewPath == null
                ? new string[] { Path.Combine(item.Directory, "preview-1920x1080.png"), Path.Combine(item.Directory, "preview-2560x1440.png"), Path.Combine(item.Directory, "preview.png") }
                : new string[] { item.PreviewPath };
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using (MemoryStream memory = new MemoryStream(File.ReadAllBytes(path)))
                    using (Image loaded = Image.FromStream(memory))
                        previewImage.Image = new Bitmap(loaded);
                    previewCaption.Text = String.IsNullOrEmpty(item.UnavailableReason) ? item.Name : item.UnavailableReason;
                    return;
                }
                catch { }
            }
            previewCaption.Text = "此主题暂无图片预览";
        }

        private async Task StartClicked()
        {
            ThemeItem selected = themeList.SelectedItem as ThemeItem;
            if (selected == null) { ShowError("请先选择一个主题。"); return; }
            await RunOperation("正在启用选中皮肤……", delegate { return service.ApplyTheme(selected, false); });
        }

        private async Task ApplyClicked()
        {
            ThemeItem selected;
            try { selected = WallpaperCatalog.Current(); }
            catch (Exception error) { ShowError(error.Message); return; }
            if (selected == null) { ShowError("没有在已安装列表中找到 Wallpaper Engine 当前壁纸。"); return; }
            themeSearch.Clear();
            RefreshView(true);
            for (int index = 0; index < themeList.Items.Count; index++)
            {
                ThemeItem item = themeList.Items[index] as ThemeItem;
                if (item != null && item.Source == "wallpaper" && item.Id == selected.Id) { themeList.SelectedIndex = index; break; }
            }
            await RunOperation("正在连接并应用当前 Wallpaper Engine 壁纸……", delegate { return service.ApplyTheme(selected, false); });
        }

        private async Task RestoreOfficialClicked()
        {
            DialogResult answer = MessageBox.Show(
                "运行中会关闭皮肤并保留随时启用能力；Codex 已退出时会执行完整恢复。是否继续？",
                "恢复官方外观", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            await RunOperation("正在恢复官方外观……", delegate { return service.RestoreOfficial(); });
        }

        private async Task RunOperation(string workingText, Func<StartResult> action)
        {
            if (!await operation.WaitAsync(0)) return;
            SetButtonsEnabled(false);
            statusLabel.Text = workingText + "\r\n启用后会在后台自动恢复连接。";
            try
            {
                StartResult result = await Task.Run(action);
                service.RecordResult(result);
                RefreshView(true);
                if (!result.Success) ShowError(result.Message);
                else statusLabel.Text = result.Message + "\r\n当前主题：" + service.GetState().ActiveThemeName;
            }
            catch (Exception ex)
            {
                service.Log("operation error: " + ex.Message);
                ShowError(ex.Message);
            }
            finally
            {
                SetButtonsEnabled(true);
                operation.Release();
            }
        }

        private void RefreshView(bool reloadThemes)
        {
            ControllerState state = service.GetState();
            activeThemeId = state.ActiveThemeId;
            string runtime = state.Paused ? "已关闭 / 暂停" : state.InjectorRunning ? "运行中（PID " + state.InjectorPid.ToString(CultureInfo.InvariantCulture) + "）" : "未运行";
            if (!state.Paused && state.RecoveryStatus == "compatibility-check-needed") runtime = "页面兼容检查未通过，需要兼容修复";
            else if (!state.Paused && (state.RecoveryStatus == "retry-wait" || (state.InjectorRunning && !state.InjectorSessionMatches))) runtime = "正在自动重新连接";
            string detectedCodex = String.IsNullOrEmpty(service.CodexVersion) ? "未检测到" : service.CodexVersion;
            string connection = state.CdpReady ? "可热连接 · " + state.CdpPort : state.ConnectionCode == "no_targets" ? "有端口，但无 Codex 页面" : "当前会话未提供背景接口";
            statusLabel.Text = "状态：" + runtime + "；连接：" + connection +
                "\r\nCodex " + detectedCodex + " · 运行时 " + service.RuntimeVersion +
                "\r\n当前主题：" + state.ActiveThemeName;
            statusLabel.BackColor = state.InjectorSessionMatches && !state.Paused && state.RecoveryStatus != "compatibility-check-needed" && state.RecoveryStatus != "retry-wait" ? Color.FromArgb(230, 247, 243) : Color.White;
            themeList.Invalidate();
            suppressAutoStartEvent = true;
            autoStart.Checked = service.IsAutoStartEnabled();
            suppressAutoStartEvent = false;
            if (!reloadThemes) return;
            cachedThemes = service.GetThemes();
            RefreshThemeItems();
        }

        private void RefreshThemeItems()
        {
            string selectedId = activeThemeId;
            string selectedDirectory = null;
            ThemeItem selected = themeList.SelectedItem as ThemeItem;
            if (selected != null) { selectedId = selected.Id; selectedDirectory = selected.Directory; }
            themeList.Items.Clear();
            string query = themeSearch.Text.Trim();
            foreach (ThemeItem item in cachedThemes)
            {
                if (query.Length == 0 || item.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    item.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    themeList.Items.Add(item);
            }
            int wallpapers = 0, unavailable = 0;
            foreach (ThemeItem item in cachedThemes)
            {
                if (item.Source == "wallpaper") wallpapers++;
                if (!String.IsNullOrEmpty(item.UnavailableReason)) unavailable++;
            }
            libraryHint.Text = "Wallpaper " + wallpapers + " 款 · 可用 " + (cachedThemes.Count - unavailable) + " 款 · 显示 " + themeList.Items.Count + " / " + cachedThemes.Count;
            for (int index = 0; index < themeList.Items.Count; index++)
            {
                ThemeItem item = themeList.Items[index] as ThemeItem;
                if (item != null && (selectedDirectory != null ? String.Equals(item.Directory, selectedDirectory, StringComparison.OrdinalIgnoreCase) : item.Id == selectedId)) { themeList.SelectedIndex = index; break; }
            }
            if (themeList.SelectedIndex < 0 && themeList.Items.Count > 0) themeList.SelectedIndex = 0;
        }

        private void SetButtonsEnabled(bool enabled)
        {
            themeList.Enabled = enabled;
            nativeButton.Enabled = enabled;
            ThemeItem selected = themeList.SelectedItem as ThemeItem;
            startButton.Enabled = enabled && selected != null && String.IsNullOrEmpty(selected.UnavailableReason);
            stopButton.Enabled = enabled;
            applyButton.Enabled = enabled;
            refreshButton.Enabled = enabled;
            previewButton.Enabled = enabled;
            officialButton.Enabled = enabled;
            folderButton.Enabled = enabled;
            autoStart.Enabled = enabled;
            wallpaperButton.Enabled = enabled;
        }

        private void AutoStartChanged(object sender, EventArgs e)
        {
            if (suppressAutoStartEvent) return;
            try { service.SetAutoStart(autoStart.Checked); }
            catch (Exception ex)
            {
                suppressAutoStartEvent = true;
                autoStart.Checked = !autoStart.Checked;
                suppressAutoStartEvent = false;
                ShowError(ex.Message);
            }
        }

        private void OpenPreview()
        {
            ThemeItem selected = themeList.SelectedItem as ThemeItem;
            if (selected == null) { ShowError("请先选择一个主题。"); return; }
            string preview = selected.PreviewPath ?? Path.Combine(selected.Directory, "preview-2560x1440.png");
            if (!File.Exists(preview)) preview = Path.Combine(selected.Directory, "preview.html");
            if (!File.Exists(preview)) { ShowError("这个主题没有离线预览。"); return; }
            Process.Start(new ProcessStartInfo(preview) { UseShellExecute = true });
        }

        private void OpenThemeFolder()
        {
            ThemeItem selected = themeList.SelectedItem as ThemeItem;
            string folder = selected == null ? service.SavedThemesRoot : selected.Directory;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }

        private void ShowError(string message)
        {
            MessageBox.Show(message, "Codex Dream Skin", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal sealed class ControllerState
    {
        public bool InjectorRunning;
        public bool InjectorSessionMatches;
        public string RecoveryStatus;
        public bool Paused;
        public bool CdpReady;
        public int CdpPort;
        public string ConnectionCode;
        public string ConnectionMessage;
        public string CodexSessionKey;
        public bool CodexRunning;
        public int InjectorPid;
        public string ActiveThemeId;
        public string ActiveThemeName;
    }

    internal sealed class ThemeItem
    {
        public string Id;
        public string Name;
        public string Directory;
        public string Source;
        public string MediaKind;
        public string MediaPath;
        public string PreviewPath;
        public string UnavailableReason;
        public bool IsPreset;
        public Dictionary<string, object> MotionProperties;
        public string Display
        {
            get
            {
                string prefix = String.Equals(Source, "wallpaper", StringComparison.OrdinalIgnoreCase) ? "[Wallpaper Engine] " :
                    String.Equals(Source, "saved", StringComparison.OrdinalIgnoreCase) ? "[已保存] " : "[其他] ";
                return prefix + Name + "  [" + Id + "]";
            }
        }
        public override string ToString() { return Display; }
    }

    internal sealed class StartResult
    {
        public bool Success;
        public bool RestartRequired;
        public bool Restarted;
        public string Message;
        public static StartResult Ok(string message, bool restarted) { return new StartResult { Success = true, Restarted = restarted, Message = message }; }
        public static StartResult Fail(string message) { return new StartResult { Success = false, Message = message }; }
        public static StartResult NeedsRestart(string message) { return new StartResult { Success = false, RestartRequired = true, Message = message }; }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }
}
