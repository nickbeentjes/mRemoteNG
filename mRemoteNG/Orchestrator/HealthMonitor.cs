using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using mRemoteNG.Connection.NickHq;

namespace mRemoteNG.Orchestrator
{
    /// <summary>
    /// Background health monitor that checks active sessions every 5 minutes.
    /// Detects orchestrators that have gone silent and posts a NickHQ alert.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class HealthMonitor
    {
        private static Timer? _timer;
        private static readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
        private static bool _running;

        /// <summary>
        /// Starts the background health monitor. Safe to call multiple times — only one
        /// timer is ever active. Call from <c>frmMain</c> after <c>NickHqClient.ConnectAll()</c>.
        /// </summary>
        public static void Start()
        {
            if (_running) return;
            _running = true;

            _timer = new Timer(
                callback: _ => Task.Run(TickAsync),
                state: null,
                dueTime: _interval,       // first tick after 5 minutes
                period: _interval);
        }

        /// <summary>
        /// Stops the health monitor. Called on application shutdown.
        /// </summary>
        public static void Stop()
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }

        // ------------------------------------------------------------------ tick

        private static async Task TickAsync()
        {
            try
            {
                await CheckAllSessionsAsync();
            }
            catch (Exception ex)
            {
                // Health monitor must never crash the app
                try
                {
                    App.Runtime.MessageCollector.AddMessage(
                        Messages.MessageClass.WarningMsg,
                        $"[HealthMonitor] Unhandled exception in tick: {ex.Message}",
                        onlyLog: true);
                }
                catch { /* swallow */ }
            }
        }

        private static async Task CheckAllSessionsAsync()
        {
            // Iterate every tab that SessionTabRegistry knows about.
            // SessionTabRegistry maps sessionId -> ConnectionTab but doesn't expose
            // GetAll() natively — the parallel OrchestratorRegistry does. We iterate
            // all orchestrated sessions plus we rely on the orchestrator to know its
            // own sessionId. For unorchestrated sessions we would need the full tab
            // registry, which is available via SessionTabRegistry if a GetAll is added.
            // For now, limit health checks to orchestrated sessions.

            foreach (var (sessionId, engine) in OrchestratorRegistry.GetAll())
            {
                try
                {
                    await CheckOrchestratedSession(sessionId, engine);
                }
                catch (Exception ex)
                {
                    App.Runtime.MessageCollector.AddMessage(
                        Messages.MessageClass.WarningMsg,
                        $"[HealthMonitor] Error checking session {sessionId}: {ex.Message}",
                        onlyLog: true);
                }
            }
        }

        private static async Task CheckOrchestratedSession(string sessionId, OrchestratorEngine engine)
        {
            if (engine.Status != OrchestratorStatus.Running)
                return;

            double minutesSinceAction = engine.LastActionAt.HasValue
                ? (DateTime.Now - engine.LastActionAt.Value).TotalMinutes
                : (DateTime.Now - engine.StartedAt).TotalMinutes;

            if (minutesSinceAction > 10)
            {
                // Try to get a friendly hostname via the session log path on PuttyBase
                string hostnameHint = GetSessionHostname(sessionId);

                await NickHqClient.PostAlertAsync(
                    sessionId,
                    "warning",
                    $"Orchestrator may be stuck on {hostnameHint}",
                    $"No action for {minutesSinceAction:F0} minutes. Task: {engine.TaskContext}");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string GetSessionHostname(string sessionId)
        {
            // Walk registered log paths via SessionTabRegistry to find the ConnectionTab,
            // then read the hostname from the InterfaceControl.
            if (Connection.NickHq.SessionTabRegistry.TryGet(sessionId, out var tab))
            {
                try
                {
                    string? hostname = null;
                    if (!tab.IsDisposed)
                    {
                        tab.Invoke((System.Windows.Forms.MethodInvoker)(() =>
                        {
                            var ifc = tab.Tag as Connection.InterfaceControl;
                            hostname = ifc?.Info?.Hostname;
                        }));
                    }
                    if (!string.IsNullOrEmpty(hostname))
                        return hostname;
                }
                catch { /* best-effort */ }
            }

            return sessionId;
        }
    }
}
