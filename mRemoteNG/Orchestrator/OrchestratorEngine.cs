using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using mRemoteNG.App;
using mRemoteNG.Connection.NickHq;

namespace mRemoteNG.Orchestrator
{
    /// <summary>
    /// The brain of the session orchestrator. Watches terminal output via the session
    /// log file, asks Claude what to type next, maintains a journal, and can report
    /// its status in plain English via <see cref="GetStatusSummaryAsync"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class OrchestratorEngine
    {
        // ------------------------------------------------------------------ P/Invoke (child window enumeration for paste)

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        // ------------------------------------------------------------------ constants

        /// Path to the persisted Claude API key — mirrors ClaudeChatWindow._keyFilePath exactly.
        private static readonly string _keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "mRemoteNG", "claude_api_key.txt");

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        // ------------------------------------------------------------------ public state

        public string              SessionId    { get; }
        public string              TaskContext  { get; }
        public OrchestratorOptions Options      { get; }
        public OrchestratorStatus  Status       { get; private set; }
        public DateTime            StartedAt    { get; } = DateTime.Now;
        public DateTime?           LastActionAt { get; private set; }
        public List<JournalEntry>  Journal      { get; } = new();

        // ------------------------------------------------------------------ private state

        private readonly CancellationTokenSource _cts = new();
        private long _logReadPosition = 0;

        // ------------------------------------------------------------------ constructor

        public OrchestratorEngine(string sessionId, string taskContext, OrchestratorOptions options)
        {
            SessionId   = sessionId;
            TaskContext = taskContext;
            Options     = options;
            Status      = OrchestratorStatus.Waiting;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Kicks off the orchestration loop. Call once after construction.</summary>
        public void Start()
        {
            Task.Run(() => RunLoopAsync(_cts.Token));
        }

        /// <summary>Cancels the loop and marks the engine as stopped.</summary>
        public void Stop()
        {
            _cts.Cancel();
            Status = OrchestratorStatus.Stopped;
        }

        // ------------------------------------------------------------------ main loop

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 1. Read new terminal output since last read
                    string newOutput = ReadNewLogOutput();
                    if (!string.IsNullOrWhiteSpace(newOutput))
                        AddJournal("observation", newOutput.Length > 500
                            ? newOutput[^500..] : newOutput);

                    // 2. Build prompt from journal (last 20 entries) + new output
                    string prompt = BuildPrompt(newOutput);

                    // 3. Ask Claude
                    Status = OrchestratorStatus.Running;
                    string claudeReply = await AskClaudeAsync(prompt, ct);

                    if (ct.IsCancellationRequested) break;

                    // 4. Parse reply
                    if (claudeReply.StartsWith("DONE", StringComparison.OrdinalIgnoreCase))
                    {
                        Status = OrchestratorStatus.Done;
                        AddJournal("done", claudeReply);
                        if (Options.NotifyOnDone)
                            await NickHqClient.PostAlertAsync(SessionId, "done",
                                "Orchestration complete", claudeReply);
                        break;
                    }
                    else if (claudeReply.StartsWith("WAIT", StringComparison.OrdinalIgnoreCase))
                    {
                        Status = OrchestratorStatus.Waiting;
                        AddJournal("waiting", "Waiting for terminal output...");
                        await Task.Delay(5000, ct);
                        continue;
                    }
                    else if (claudeReply.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase))
                    {
                        Status = OrchestratorStatus.Blocked;
                        string reason = claudeReply.Substring(8).Trim();
                        AddJournal("blocked", reason);
                        if (Options.NotifyOnBlocked)
                            await NickHqClient.PostAlertAsync(SessionId, "warning",
                                "Orchestrator needs input", reason);
                        // Wait for user to intervene — poll every 30 s
                        await Task.Delay(30_000, ct);
                        continue;
                    }
                    else
                    {
                        // It's a command to type
                        string cmd = claudeReply.Trim();
                        AddJournal("command", cmd);
                        LastActionAt = DateTime.Now;
                        SendCommandToTerminal(cmd);
                        Status = OrchestratorStatus.Waiting;
                        await Task.Delay(3000, ct);   // wait for command to execute
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation — do nothing
            }
            catch (Exception ex)
            {
                Status = OrchestratorStatus.Error;
                AddJournal("observation", $"[OrchestratorEngine] Fatal error: {ex.Message}");
                try
                {
                    Runtime.MessageCollector.AddMessage(
                        Messages.MessageClass.WarningMsg,
                        $"[OrchestratorEngine] Unhandled exception: {ex}",
                        onlyLog: true);
                }
                catch { /* swallow */ }
            }
        }

        // ------------------------------------------------------------------ log reading

        private string ReadNewLogOutput()
        {
            string? logPath = NickHqClient.TryGetLogPath(SessionId);
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                return string.Empty;

            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(_logReadPosition, SeekOrigin.Begin);

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096, leaveOpen: true);
                string newText = reader.ReadToEnd();

                _logReadPosition = fs.Position;
                return newText;
            }
            catch (Exception ex)
            {
                AddJournal("observation", $"[log read error] {ex.Message}");
                return string.Empty;
            }
        }

        // ------------------------------------------------------------------ prompt builder

        private string BuildPrompt(string newOutput)
        {
            var journalLines = string.Join("\n",
                Journal.TakeLast(20)
                       .Select(j => $"[{j.Timestamp:HH:mm:ss}] {j.Type}: {j.Content}"));

            return $"""
                You are orchestrating a terminal session to complete a task.

                TASK: {TaskContext}

                RECENT JOURNAL (last 20 actions):
                {journalLines}

                LATEST TERMINAL OUTPUT:
                {newOutput}

                What should I do next? Reply with EXACTLY one of:
                - A single shell command to type (just the command, no explanation)
                - WAIT (if still processing, nothing new to do yet)
                - DONE: <brief summary of what was accomplished>
                - BLOCKED: <specific question for the user>

                For GSD prompts showing numbered options: pick the appropriate option number.
                Do not explain yourself. Just the command, WAIT, DONE, or BLOCKED.
                """;
        }

        // ------------------------------------------------------------------ Claude API

        private async Task<string> AskClaudeAsync(string prompt, CancellationToken ct)
        {
            string apiKey = LoadApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                AddJournal("observation", "[OrchestratorEngine] No Claude API key configured.");
                Status = OrchestratorStatus.Blocked;
                return "BLOCKED: No Claude API key is configured. Set CLAUDE_API_KEY env var or use Options → Claude API Key.";
            }

            var requestBody = new
            {
                model      = "claude-sonnet-4-6",
                max_tokens = 256,
                messages   = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            string json = JsonSerializer.Serialize(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Claude API error {(int)response.StatusCode}: {body}");

            JsonNode? parsed = JsonNode.Parse(body);
            return parsed?["content"]?[0]?["text"]?.GetValue<string>()?.Trim() ?? "WAIT";
        }

        // ------------------------------------------------------------------ terminal send

        private void SendCommandToTerminal(string cmd)
        {
            if (!SessionTabRegistry.TryGet(SessionId, out var tab))
            {
                AddJournal("observation", $"[SendCommand] No tab found for session {SessionId}");
                return;
            }

            if (tab.IsDisposed)
            {
                AddJournal("observation", "[SendCommand] Tab is disposed");
                return;
            }

            // Append newline so the command executes
            string textToSend = cmd.TrimEnd('\n', '\r') + "\n";

            try
            {
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
                                return false;  // stop after first child — that's the PuTTY window
                            }, IntPtr.Zero);
                        }

                        if (puttyHwnd != IntPtr.Zero)
                        {
                            foreach (char c in textToSend)
                                NativeMethods.PostMessage(puttyHwnd, NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                        }
                        else
                        {
                            AddJournal("observation", "[SendCommand] Could not locate PuTTY HWND");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddJournal("observation", $"[SendCommand] Invoke error: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                AddJournal("observation", $"[SendCommand] Tab.Invoke failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ status summary

        /// <summary>
        /// Asks Claude for a plain-English summary of the current orchestration state.
        /// Called by <see cref="OrchestratorStatusForm"/> when the user clicks "What are you doing?".
        /// </summary>
        public async Task<string> GetStatusSummaryAsync()
        {
            var journal = Journal.TakeLast(15).ToList();
            var journalText = string.Join("\n",
                journal.Select(j => $"[{j.Timestamp:HH:mm:ss}] {j.Type}: {j.Content}"));

            var prompt = $"""
                You are summarising the current state of an automated terminal session.

                Task: {TaskContext}
                Status: {Status}
                Started: {StartedAt:HH:mm}
                Last action: {LastActionAt?.ToString("HH:mm") ?? "not started"}

                Journal:
                {journalText}

                Write 2-4 sentences explaining what is happening, where things are up to,
                and what happens next. Be specific about commands run and their outcomes.
                Write as if reporting to the person who set this task running.
                """;

            return await AskClaudeAsync(prompt, CancellationToken.None);
        }

        // ------------------------------------------------------------------ helpers

        private void AddJournal(string type, string content) =>
            Journal.Add(new JournalEntry { Type = type, Content = content });

        private static string LoadApiKey()
        {
            var envKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
            if (!string.IsNullOrEmpty(envKey)) return envKey;
            if (File.Exists(_keyFilePath)) return File.ReadAllText(_keyFilePath).Trim();
            return string.Empty;
        }
    }
}
