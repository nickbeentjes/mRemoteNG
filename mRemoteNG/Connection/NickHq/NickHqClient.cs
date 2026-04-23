using mRemoteNG.App;
using mRemoteNG.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using mRemoteNG.UI.Tabs;

namespace mRemoteNG.Connection.NickHq
{
    /// <summary>
    /// Manages connections to one or more NickHQ backend servers, registers terminal
    /// sessions with each connected server, polls for remote commands, and executes them
    /// (exec, paste, screenshot, readlog).
    ///
    /// Server list is loaded from NickHqConfig (persisted to %APPDATA%\mRemoteNG\nickhq_servers.json).
    ///
    /// Legacy env-var fallback: if no config file exists but AGENT_TOKEN env var is set,
    /// a synthetic server entry is created from NICKHQ_URL / AGENT_TOKEN so existing setups
    /// continue to work without any config migration.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class NickHqClient
    {
        // ------------------------------------------------------------------ HTTP

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // ------------------------------------------------------------------ connected servers
        // serverId -> NickHqServer
        private static readonly ConcurrentDictionary<string, NickHqServer> _connectedServers =
            new(StringComparer.Ordinal);

        // ------------------------------------------------------------------ sessions
        // sessionId -> per-server poll state
        private static readonly ConcurrentDictionary<string, SessionState> _sessions =
            new(StringComparer.Ordinal);

        // sessionId -> log file path (stored for "readlog" command)
        private static readonly ConcurrentDictionary<string, string> _logPaths =
            new(StringComparer.Ordinal);

        private sealed class SessionState
        {
            // serverId -> CancellationTokenSource
            public ConcurrentDictionary<string, CancellationTokenSource> ServerCts { get; } = new();
            public string Hostname { get; init; } = "";
            public string Username { get; init; } = "";
            public string Protocol { get; init; } = "";
            public string Label { get; init; } = "";
            public string LogPath { get; init; } = "";
        }

        // ------------------------------------------------------------------ P/Invoke

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ------------------------------------------------------------------ helpers

        private static bool Enabled => _connectedServers.Count > 0;

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Connects to all servers that are Enabled and have AutoConnect set.
        /// Safe to call multiple times — servers already connected are skipped.
        /// If no config file exists but AGENT_TOKEN env var is set, uses env var fallback.
        /// </summary>
        public static void ConnectAll()
        {
            var servers = GetEffectiveServers();
            foreach (var server in servers.Where(s => s.Enabled && s.AutoConnect))
                ConnectServer(server);
        }

        /// <summary>
        /// Marks a server as connected so sessions will be registered with it.
        /// Idempotent — calling with the same server twice is a no-op.
        /// </summary>
        public static void ConnectServer(NickHqServer server)
        {
            if (string.IsNullOrWhiteSpace(server.AgentToken))
            {
                LogWarning($"NickHqClient: Skipping server '{server.Name}' — AgentToken is empty.");
                return;
            }

            _connectedServers[server.Id] = server;
        }

        /// <summary>Returns a snapshot of currently connected servers.</summary>
        public static List<NickHqServer> GetConnectedServers() =>
            new(_connectedServers.Values);

        /// <summary>
        /// Registers a new session with all connected NickHQ servers and starts poll
        /// loops for each. Returns the generated sessionId (a Guid string).
        /// Returns empty string if no servers are connected.
        /// </summary>
        public static string RegisterSession(
            string hostname,
            string username,
            string protocol,
            string label,
            string logPath)
        {
            if (!Enabled) return string.Empty;

            string sessionId = Guid.NewGuid().ToString();

            var state = new SessionState
            {
                Hostname = hostname,
                Username = username,
                Protocol = protocol,
                Label = label,
                LogPath = logPath
            };
            _sessions[sessionId] = state;

            if (!string.IsNullOrEmpty(logPath))
                _logPaths[sessionId] = logPath;

            // Register + poll on each connected server
            foreach (var server in _connectedServers.Values.ToList())
                StartServerSession(sessionId, state, server);

            return sessionId;
        }

        /// <summary>
        /// Cancels all poll loops for the session and notifies each connected NickHQ
        /// server that the session has ended. Safe to call even if the session was
        /// never registered.
        /// </summary>
        public static void UnregisterSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            if (!_sessions.TryRemove(sessionId, out var state)) return;

            _logPaths.TryRemove(sessionId, out _);

            foreach (var (serverId, cts) in state.ServerCts)
            {
                try { cts.Cancel(); } catch { /* ignore */ }

                if (_connectedServers.TryGetValue(serverId, out var server))
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            string url = $"{server.Url}/sessions/{sessionId}?token={Uri.EscapeDataString(server.AgentToken)}";
                            await _http.DeleteAsync(url);
                        }
                        catch (Exception ex)
                        {
                            LogWarning($"NickHqClient: DELETE session failed ({server.Name}): {ex.Message}");
                        }
                    });
                }
            }
        }

        // ------------------------------------------------------------------ per-server session lifecycle

        private static void StartServerSession(string sessionId, SessionState state, NickHqServer server)
        {
            var cts = new CancellationTokenSource();
            state.ServerCts[server.Id] = cts;

            Task.Run(async () =>
            {
                await PostRegisterAsync(sessionId, state, server);
                await PollSessionAsync(sessionId, server, cts.Token);
            });
        }

        // ------------------------------------------------------------------ registration

        private static async Task PostRegisterAsync(string sessionId, SessionState state, NickHqServer server)
        {
            try
            {
                var body = new JsonObject
                {
                    ["id"] = sessionId,
                    ["hostname"] = state.Hostname,
                    ["username"] = state.Username,
                    ["protocol"] = state.Protocol,
                    ["label"] = state.Label,
                    ["log_path"] = state.LogPath
                };

                string url = $"{server.Url}/sessions?token={Uri.EscapeDataString(server.AgentToken)}";
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                    LogWarning($"NickHqClient: POST /sessions returned {(int)resp.StatusCode} ({server.Name})");
            }
            catch (Exception ex)
            {
                LogWarning($"NickHqClient: POST /sessions failed ({server.Name}): {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ poll loop

        private static async Task PollSessionAsync(string sessionId, NickHqServer server, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, ct);

                    string url = $"{server.Url}/sessions/{sessionId}/exec/poll?token={Uri.EscapeDataString(server.AgentToken)}";
                    var resp = await _http.GetAsync(url, ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        LogWarning($"NickHqClient: poll returned {(int)resp.StatusCode} for session {sessionId} ({server.Name})");
                        continue;
                    }

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    JsonNode? node = JsonNode.Parse(json);
                    if (node == null) continue;

                    JsonNode? commandNode = node["command"];
                    if (commandNode == null || commandNode.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                        continue;

                    JsonObject? command = commandNode.AsObject();
                    if (command != null)
                        _ = Task.Run(() => ExecuteCommandAsync(sessionId, command, server), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogWarning($"NickHqClient: poll error for session {sessionId} ({server.Name}): {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ command dispatch

        private static async Task ExecuteCommandAsync(string sessionId, JsonObject command, NickHqServer server)
        {
            string? cmdId = command["id"]?.GetValue<string>();
            string? type = command["type"]?.GetValue<string>();
            string? payload = command["payload"]?.GetValue<string>();

            string stdout = "";
            string stderr = "";
            int exitCode = 0;
            string? screenshotB64 = null;

            try
            {
                switch (type?.ToLowerInvariant())
                {
                    case "exec":
                        (stdout, stderr, exitCode) = await RunProcessAsync(payload ?? "");
                        break;

                    case "screenshot":
                        screenshotB64 = TakeTabScreenshot(sessionId);
                        if (screenshotB64 == null)
                        {
                            stderr = "Screenshot failed: could not locate tab window.";
                            exitCode = 1;
                        }
                        break;

                    case "paste":
                        (stdout, stderr, exitCode) = PasteToTab(sessionId, payload ?? "");
                        break;

                    case "readlog":
                        (stdout, stderr, exitCode) = ReadSessionLog(sessionId, payload);
                        break;

                    default:
                        stderr = $"Unknown command type: {type}";
                        exitCode = 1;
                        break;
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                exitCode = 1;
            }

            await PostResultAsync(sessionId, cmdId, stdout, stderr, exitCode, screenshotB64, server);
        }

        // ------------------------------------------------------------------ exec

        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return ("", "Empty command", 1);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await Task.Run(() => proc.WaitForExit(30_000));

            return (stdoutSb.ToString(), stderrSb.ToString(), proc.ExitCode);
        }

        // ------------------------------------------------------------------ paste

        private static (string stdout, string stderr, int exitCode) PasteToTab(string sessionId, string text)
        {
            if (!SessionTabRegistry.TryGet(sessionId, out ConnectionTab tab))
                return ("", $"No tab found for session {sessionId}", 1);

            if (tab.IsDisposed)
                return ("", "Tab is disposed", 1);

            string err = "";
            tab.Invoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                try
                {
                    var ifc = tab.Tag as mRemoteNG.Connection.InterfaceControl;
                    IntPtr puttyHwnd = IntPtr.Zero;

                    if (ifc != null)
                    {
                        EnumChildWindows(ifc.Handle, (hwnd, _) =>
                        {
                            puttyHwnd = hwnd;
                            return false;
                        }, IntPtr.Zero);
                    }

                    if (puttyHwnd != IntPtr.Zero)
                    {
                        foreach (char c in text)
                            mRemoteNG.App.NativeMethods.PostMessage(puttyHwnd, mRemoteNG.App.NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                    }
                    else
                    {
                        err = "Could not locate embedded PuTTY HWND for paste.";
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            }));

            return string.IsNullOrEmpty(err)
                ? ($"Pasted {text.Length} characters.", "", 0)
                : ("", err, 1);
        }

        // ------------------------------------------------------------------ readlog

        private static (string stdout, string stderr, int exitCode) ReadSessionLog(string sessionId, string? payload)
        {
            int lineCount = 100;
            if (!string.IsNullOrEmpty(payload) && int.TryParse(payload.Trim(), out int n) && n > 0)
                lineCount = n;

            if (!_logPaths.TryGetValue(sessionId, out string? logPath) || string.IsNullOrEmpty(logPath))
                return ("", $"No log path registered for session {sessionId}", 1);

            if (!File.Exists(logPath))
                return ("", $"Log file not found: {logPath}", 1);

            try
            {
                string[] lines = File.ReadAllLines(logPath);
                int skip = Math.Max(0, lines.Length - lineCount);
                string result = string.Join(Environment.NewLine, lines, skip, lines.Length - skip);
                return (result, "", 0);
            }
            catch (Exception ex)
            {
                return ("", $"Failed to read log: {ex.Message}", 1);
            }
        }

        // ------------------------------------------------------------------ screenshot

        private static string? TakeTabScreenshot(string sessionId)
        {
            if (!SessionTabRegistry.TryGet(sessionId, out ConnectionTab tab))
                return null;

            if (tab.IsDisposed)
                return null;

            string? b64 = null;

            tab.Invoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                try
                {
                    var ifc = tab.Tag as mRemoteNG.Connection.InterfaceControl;
                    IntPtr targetHwnd = IntPtr.Zero;

                    if (ifc != null)
                    {
                        EnumChildWindows(ifc.Handle, (hwnd, _) =>
                        {
                            targetHwnd = hwnd;
                            return false;
                        }, IntPtr.Zero);
                    }

                    if (targetHwnd == IntPtr.Zero)
                        targetHwnd = tab.Handle;

                    if (!GetWindowRect(targetHwnd, out RECT rect))
                        return;

                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w <= 0 || h <= 0) return;

                    using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        try { PrintWindow(targetHwnd, hdc, 0); }
                        finally { g.ReleaseHdc(hdc); }
                    }

                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    b64 = Convert.ToBase64String(ms.ToArray());
                }
                catch (Exception ex)
                {
                    LogWarning($"NickHqClient: screenshot failed: {ex.Message}");
                }
            }));

            return b64;
        }

        // ------------------------------------------------------------------ post result

        private static async Task PostResultAsync(
            string sessionId,
            string? cmdId,
            string stdout,
            string stderr,
            int exitCode,
            string? screenshotB64,
            NickHqServer server)
        {
            if (string.IsNullOrEmpty(cmdId)) return;

            try
            {
                var body = new JsonObject
                {
                    ["stdout"] = stdout,
                    ["stderr"] = stderr,
                    ["exit_code"] = exitCode
                };

                if (screenshotB64 != null)
                    body["screenshot_b64"] = screenshotB64;

                string url = $"{server.Url}/sessions/{sessionId}/exec/{cmdId}/result?token={Uri.EscapeDataString(server.AgentToken)}";
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                    LogWarning($"NickHqClient: POST result returned {(int)resp.StatusCode} ({server.Name})");
            }
            catch (Exception ex)
            {
                LogWarning($"NickHqClient: POST result failed ({server.Name}): {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ alert

        /// <summary>
        /// Posts an alert to all connected NickHQ servers for the given session.
        /// Used by <see cref="mRemoteNG.Orchestrator.HealthMonitor"/> to report stuck orchestrators.
        /// </summary>
        /// <param name="sessionId">The NickHQ session ID.</param>
        /// <param name="level">Severity string, e.g. "warning" or "error".</param>
        /// <param name="title">Short alert title.</param>
        /// <param name="detail">Longer description / body text.</param>
        public static async Task PostAlertAsync(string sessionId, string level, string title, string detail)
        {
            if (!Enabled) return;
            if (string.IsNullOrEmpty(sessionId)) return;

            var body = new JsonObject
            {
                ["level"]  = level,
                ["title"]  = title,
                ["detail"] = detail
            };

            string json = body.ToJsonString();

            foreach (var server in _connectedServers.Values.ToList())
            {
                try
                {
                    string url = $"{server.Url}/sessions/{sessionId}/alert?token={Uri.EscapeDataString(server.AgentToken)}";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await _http.PostAsync(url, content);
                    if (!resp.IsSuccessStatusCode)
                        LogWarning($"NickHqClient: POST alert returned {(int)resp.StatusCode} ({server.Name})");
                }
                catch (Exception ex)
                {
                    LogWarning($"NickHqClient: POST alert failed ({server.Name}): {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ log path lookup (for orchestrator)

        /// <summary>
        /// Returns the log file path registered for the given session, or <c>null</c> if none.
        /// Used by <see cref="mRemoteNG.Orchestrator.OrchestratorEngine"/> to tail terminal output.
        /// </summary>
        public static string? TryGetLogPath(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            return _logPaths.TryGetValue(sessionId, out string? path) ? path : null;
        }

        // ------------------------------------------------------------------ env-var fallback

        /// <summary>
        /// Returns the effective server list: config file if it exists, otherwise
        /// synthesises a single entry from NICKHQ_URL / AGENT_TOKEN env vars so
        /// pre-config setups keep working.
        /// </summary>
        private static List<NickHqServer> GetEffectiveServers()
        {
            var servers = NickHqConfig.Load();
            if (servers.Count > 0) return servers;

            string? agentToken = Environment.GetEnvironmentVariable("AGENT_TOKEN");
            if (string.IsNullOrEmpty(agentToken)) return servers;

            // Synthesise from env vars and persist so the user can manage via UI next time
            var synth = new NickHqServer
            {
                Name = "Default (from env vars)",
                Url = Environment.GetEnvironmentVariable("NICKHQ_URL")?.TrimEnd('/')
                      ?? "https://keess-mac-mini.taile6c48b.ts.net",
                AgentToken = agentToken,
                BearerToken = Environment.GetEnvironmentVariable("NICKHQ_API_TOKEN") ?? "",
                Enabled = true,
                AutoConnect = true
            };

            NickHqConfig.Save(new List<NickHqServer> { synth });
            NickHqConfig.Invalidate();

            return new List<NickHqServer> { synth };
        }

        // ------------------------------------------------------------------ logging

        private static void LogWarning(string message)
        {
            try
            {
                Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, message, onlyLog: true);
            }
            catch
            {
                // If the message collector itself is unavailable, swallow silently
            }
        }
    }
}
