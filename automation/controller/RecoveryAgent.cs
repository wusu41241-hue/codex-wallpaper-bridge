using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexDreamSkinController
{
    // Supervises only the existing skin runtime. It never closes or launches
    // Codex, and a paused skin always takes precedence over recovery.
    internal sealed class RecoveryPolicy
    {
        private string session;
        private bool verified;
        private bool needsRepair;
        internal int Failures { get; private set; }
        internal DateTime RetryAt { get; private set; }
        internal DateTime LastVerifiedUtc { get; private set; }
        internal DateTime VerificationDueUtc { get; private set; }

        internal string Decide(ControllerState state, bool mediaReady, DateTime now)
        {
            string nextSession = state.CodexSessionKey ?? "";
            if (nextSession != session || state.Paused || !state.CodexRunning)
            {
                session = nextSession;
                Failures = 0;
                RetryAt = DateTime.MinValue;
                verified = false;
                needsRepair = false;
                LastVerifiedUtc = DateTime.MinValue;
                VerificationDueUtc = DateTime.MinValue;
            }
            if (state.Paused) return "paused";
            if (!state.CodexRunning) return "waiting-codex";
            if (!state.CdpReady) return "waiting-interface";
            bool healthyProcesses = state.InjectorRunning && state.InjectorSessionMatches && mediaReady;
            if (healthyProcesses && verified && !needsRepair && now < VerificationDueUtc)
            {
                return "active";
            }
            if (now < RetryAt) return "retry-wait";
            return healthyProcesses && !needsRepair ? "verify" : "recover";
        }

        internal void RecordAttempt(bool success, DateTime now)
        {
            if (success) {
                Failures = 0; RetryAt = DateTime.MinValue;
                verified = true; needsRepair = false; LastVerifiedUtc = now;
                VerificationDueUtc = now.AddMinutes(5);
                return;
            }
            verified = false;
            needsRepair = true;
            Failures = Math.Min(8, Failures + 1);
            RetryAt = now.AddSeconds(Math.Min(120, 5 * (1 << (Failures - 1))));
        }
    }

    internal static class RecoveryAgent
    {
        private static string MutexName
        {
            get { return "Local\\CodexDreamSkin.Recovery." + WindowsIdentity.GetCurrent().User.Value; }
        }

        internal static void EnsureStarted(ControllerService service)
        {
            bool owns = false;
            using (Mutex probe = new Mutex(false, MutexName))
            {
                try { owns = probe.WaitOne(0); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return;
                probe.ReleaseMutex();
            }
            string executable = Path.Combine(service.WallpaperControlRoot, "CodexDreamSkinController.exe");
            if (!File.Exists(executable)) return;
            try
            {
                Process.Start(new ProcessStartInfo(executable, "--supervise") {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception error) { service.Log("recovery agent start failed: " + error.Message); }
        }

        internal static int Run(ControllerService initialService)
        {
            using (Mutex mutex = new Mutex(false, MutexName))
            {
                bool owns = false;
                try
                {
                    try { owns = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns) return 0;
                    var policy = new RecoveryPolicy();
                    var service = initialService;
                    var json = new JavaScriptSerializer();
                    string previousStatus = null;
                    string lastResult = "";
                    while (true)
                    {
                        try
                        {
                            ControllerState state = service.GetState();
                            string status = policy.Decide(state,
                                state.InjectorRunning && service.IsMediaHostReady(), DateTime.UtcNow);
                            if (status == "recover" || status == "verify")
                            {
                                // Re-resolve the registered package and runtime paths after updates.
                                service = new ControllerService();
                                bool repairing = status == "recover";
                                string checkedSession = state.CodexSessionKey;
                                StartResult result = repairing ? service.RecoverConnection() : service.VerifyCurrentConnection();
                                lastResult = result.Message;
                                state = service.GetFreshState();
                                if (state.Paused || !state.CodexRunning || !state.CdpReady ||
                                    !String.Equals(checkedSession, state.CodexSessionKey, StringComparison.Ordinal))
                                {
                                    status = policy.Decide(state, state.InjectorRunning && service.IsMediaHostReady(), DateTime.UtcNow);
                                }
                                else
                                {
                                    bool confirmed = result.Success && state.InjectorSessionMatches && state.InjectorRunning;
                                    policy.RecordAttempt(confirmed, DateTime.UtcNow);
                                    if (result.Success && !confirmed) lastResult = "恢复后的会话尚未通过身份校验，将继续自动重连。";
                                    if (repairing || !confirmed) service.RecordResult(confirmed ? result : StartResult.Fail(lastResult));
                                    status = confirmed ? "active" : policy.Failures >= 3 ? "compatibility-check-needed" : "retry-wait";
                                }
                            }
                            if (status == "retry-wait" && policy.Failures >= 3) status = "compatibility-check-needed";
                            string statusKey = status + ":" + state.ConnectionCode + ":" + state.InjectorPid + ":" + policy.Failures + ":" + lastResult + ":" + policy.LastVerifiedUtc.Ticks;
                            if (statusKey != previousStatus)
                            {
                                string path = Path.Combine(service.WallpaperControlRoot, "recovery-status.json");
                                string temporary = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                                File.WriteAllText(temporary, json.Serialize(new Dictionary<string, object> {
                                    { "status", status }, { "time", DateTimeOffset.Now.ToString("o") },
                                    { "pid", Process.GetCurrentProcess().Id }, { "connection_code", state.ConnectionCode },
                                    { "port", state.CdpPort }, { "injector_pid", state.InjectorPid },
                                    { "failure_count", policy.Failures }, { "last_result", lastResult },
                                    { "injector_session_matches", state.InjectorSessionMatches },
                                    { "last_verified_utc", policy.LastVerifiedUtc == DateTime.MinValue ? null : policy.LastVerifiedUtc.ToString("o") },
                                    { "next_verification_utc", policy.VerificationDueUtc == DateTime.MinValue ? null : policy.VerificationDueUtc.ToString("o") },
                                    { "never_restarts_codex", true }
                                }), new UTF8Encoding(false));
                                if (File.Exists(path)) File.Replace(temporary, path, null);
                                else File.Move(temporary, path);
                                previousStatus = statusKey;
                            }
                        }
                        catch (Exception error)
                        {
                            policy.RecordAttempt(false, DateTime.UtcNow);
                            if (previousStatus != "error") service.Log("recovery agent: " + error.Message);
                            previousStatus = "error";
                        }
                        Thread.Sleep(3000);
                    }
                }
                finally { if (owns) { try { mutex.ReleaseMutex(); } catch { } } }
            }
        }
    }
}
