using mRemoteNG.App;
using mRemoteNG.Messages;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
    /// Singleton HTTP client that registers mRemoteNG terminal sessions with the
    /// NickHQ backend, polls for remote commands, and executes them (exec, paste,
    /// screenshot, readlog).
    ///
    /// Controlled by two environment variables:
    ///   NICKHQ_URL   — base URL (default https://keess-mac-mini.taile6c48b.ts.net)
    ///   AGENT_TOKEN  — bearer token; if empty all functionality is silently disabled.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class NickHqClient
    {
        // ------------------------------------------------------------------ config
        private static readonly string _baseUrl =
            Environment.GetEnvironmentVariable("NICKHQ_URL")?.TrimEnd('/')
            ?? "https://keess-mac-mini.taile6c48b.ts.net";

        private static readonly string _token =
            Environment.GetEnvironmentVariable("AGENT_TOKEN") ?? "";

        private static bool Enabled => !string.IsNullOrEmpty(_token);

        // ------------------------------------------------------------------ state
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // sessionId -> CancellationTokenSource for the poll loop
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        // sessionId -> log file path (stored for "readlog" command)
        private static readonly ConcurrentDictionary<string, string> _logPaths =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

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

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Registers a new session with NickHQ and starts its background poll loop.
        /// Returns the generated sessionId (a Guid string). If NickHQ is disabled
        /// (empty token) returns an empty string immediately.
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

            if (!string.IsNullOrEmpty(logPath))
                _logPaths[sessionId] = logPath;

            // Fire-and-forget: register on the server then start polling
            Task.Run(async () =>
            {
                await PostRegisterAsync(sessionId, hostname, username, protocol, label, logPath);
                var cts = new CancellationTokenSource();
                _sessions[sessionId] = cts;
                await PollSessionAsync(sessionId, cts.Token);
            });

            return sessionId;
        }

        /// <summary>
        /// Cancels the poll loop and notifies NickHQ that this session has ended.
        /// Safe to call even if the session was never registered (e.g., token was empty).
        /// </summary>
        public static void UnregisterSession(string sessionId)
        {
            if (!Enabled || string.IsNullOrEmpty(sessionId)) return;

            if (_sessions.TryRemove(sessionId, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
            }

            _logPaths.TryRemove(sessionId, out _);

            Task.Run(async () =>
            {
                try
                {
                    string url = $"{_baseUrl}/sessions/{sessionId}?token={Uri.EscapeDataString(_token)}";
                    await _http.DeleteAsync(url);
                }
                catch (Exception ex)
                {
                    LogWarning($"NickHqClient: DELETE session failed: {ex.Message}");
                }
            });
        }

        // ------------------------------------------------------------------ registration

        private static async Task PostRegisterAsync(
            string sessionId,
            string hostname,
            string username,
            string protocol,
            string label,
            string logPath)
        {
            try
            {
                var body = new JsonObject
                {
                    ["id"] = sessionId,
                    ["hostname"] = hostname,
                    ["username"] = username,
                    ["protocol"] = protocol,
                    ["label"] = label,
                    ["log_path"] = logPath
                };

                string url = $"{_baseUrl}/sessions?token={Uri.EscapeDataString(_token)}";
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                    LogWarning($"NickHqClient: POST /sessions returned {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                LogWarning($"NickHqClient: POST /sessions failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ poll loop

        private static async Task PollSessionAsync(string sessionId, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, ct);

                    string url = $"{_baseUrl}/sessions/{sessionId}/exec/poll?token={Uri.EscapeDataString(_token)}";
                    var resp = await _http.GetAsync(url, ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        LogWarning($"NickHqClient: poll returned {(int)resp.StatusCode} for session {sessionId}");
                        continue;
                    }

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    JsonNode? node = JsonNode.Parse(json);
                    if (node == null) continue;

                    // Server returns null or {"command": null} when there is nothing to do
                    JsonNode? commandNode = node["command"];
                    if (commandNode == null || commandNode.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                        continue;

                    JsonObject? command = commandNode.AsObject();
                    if (command != null)
                        _ = Task.Run(() => ExecuteCommandAsync(sessionId, command), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogWarning($"NickHqClient: poll error for session {sessionId}: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ command dispatch

        private static async Task ExecuteCommandAsync(string sessionId, JsonObject command)
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

            await PostResultAsync(sessionId, cmdId, stdout, stderr, exitCode, screenshotB64);
        }

        // ------------------------------------------------------------------ exec

        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return ("", "Empty command", 1);

            // Execute via cmd /c to support shell builtins and pipes
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

            // Wait up to 30 seconds
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

            // Must run on UI thread; use Invoke so we can report errors back
            string err = "";
            tab.Invoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                try
                {
                    // Find the embedded PuTTY HWND inside the InterfaceControl
                    var ifc = tab.Tag as mRemoteNG.Connection.InterfaceControl;
                    IntPtr puttyHwnd = IntPtr.Zero;

                    if (ifc != null)
                    {
                        EnumChildWindows(ifc.Handle, (hwnd, _) =>
                        {
                            puttyHwnd = hwnd;
                            return false; // stop after first child
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
                    // Find the embedded PuTTY child window
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

                    // Fall back to the tab's own handle if no child was found
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
                        try
                        {
                            PrintWindow(targetHwnd, hdc, 0);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
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
            string? screenshotB64)
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

                string url = $"{_baseUrl}/sessions/{sessionId}/exec/{cmdId}/result?token={Uri.EscapeDataString(_token)}";
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                    LogWarning($"NickHqClient: POST result returned {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                LogWarning($"NickHqClient: POST result failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ helpers

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
