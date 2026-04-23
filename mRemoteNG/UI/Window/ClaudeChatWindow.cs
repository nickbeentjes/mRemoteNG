using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using System.Runtime.Versioning;
using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Connection.HostCall;
using mRemoteNG.UI;
using mRemoteNG.UI.Tabs;

namespace mRemoteNG.UI.Window
{
    [SupportedOSPlatform("windows")]
    public class ClaudeChatWindow : BaseWindow
    {
        private RichTextBox _txtHistory;
        private TextBox _txtInput;
        private Button _btnSend;
        private Panel _pnlBottom;

        private readonly HttpClient _httpClient = new();
        private readonly List<object> _conversationHistory = new();

        public ClaudeChatWindow() : this(new DockContent())
        {
        }

        public ClaudeChatWindow(DockContent panel)
        {
            WindowType = WindowType.ClaudeChat;
            DockPnl = panel;
            Text = "Claude AI";
            TabText = "Claude AI";
            HideOnClose = true;
            InitializeControls();
        }

        private void InitializeControls()
        {
            _txtHistory = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = SystemColors.Window,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.None
            };

            _txtInput = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.FixedSingle
            };
            _txtInput.KeyDown += TxtInput_KeyDown;

            _btnSend = new Button
            {
                Text = "Send",
                Dock = DockStyle.Right,
                Width = 60,
                Font = new Font("Segoe UI", 9F)
            };
            _btnSend.Click += BtnSend_Click;

            _pnlBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 28
            };
            _pnlBottom.Controls.Add(_txtInput);
            _pnlBottom.Controls.Add(_btnSend);

            Controls.Add(_txtHistory);
            Controls.Add(_pnlBottom);
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                BtnSend_Click(sender, EventArgs.Empty);
            }
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            string input = _txtInput.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            _txtInput.Clear();

            AppendLine($"[You] {input}");
            AppendLine("");

            if (input.StartsWith("cgo ", StringComparison.OrdinalIgnoreCase))
            {
                HandleCgoCommand(input.Substring(4).Trim());
                return;
            }

            _btnSend.Enabled = false;
            try
            {
                string response = await SendToClaudeAsync(input);

                // Strip host-call tags before displaying, then execute them
                string displayText = HostCallParser.Strip(response);
                AppendLine($"[Claude] {displayText}");
                AppendLine("");

                await ExecuteHostCallsAsync(response);
            }
            catch (Exception ex)
            {
                AppendLine($"[Error] {ex.Message}");
                AppendLine("");
            }
            finally
            {
                _btnSend.Enabled = true;
                _txtInput.Focus();
            }
        }

        private void HandleCgoCommand(string args)
        {
            if (args.StartsWith("--connect ", StringComparison.OrdinalIgnoreCase))
            {
                AppendLine("[System] cgo --connect not yet wired — coming in Phase 2");
            }
            else if (args.Equals("--discover", StringComparison.OrdinalIgnoreCase))
            {
                AppendLine("[System] Smart Connections refresh not yet wired — coming in Phase 2");
            }
            else if (args.StartsWith("--gsd ", StringComparison.OrdinalIgnoreCase))
            {
                AppendLine("[System] GSD launcher not yet wired — coming in Phase 3");
            }
            else
            {
                AppendLine("[System] Usage: cgo --connect <name> | cgo --discover | cgo --gsd <cmd>");
            }

            AppendLine("");
            _txtInput.Focus();
        }

        private async Task<string> SendToClaudeAsync(string userMessage)
        {
            string apiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY") ?? string.Empty;
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("CLAUDE_API_KEY environment variable is not set.");

            _conversationHistory.Add(new { role = "user", content = userMessage });

            var requestBody = new
            {
                model = "claude-sonnet-4-6",
                max_tokens = 1024,
                messages = _conversationHistory
            };

            string json = JsonSerializer.Serialize(requestBody);
            using HttpRequestMessage request = new(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"API error {(int)response.StatusCode}: {responseBody}");

            JsonNode parsed = JsonNode.Parse(responseBody);
            string assistantMessage = parsed?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;

            _conversationHistory.Add(new { role = "assistant", content = assistantMessage });

            return assistantMessage;
        }

        private async Task ExecuteHostCallsAsync(string responseText)
        {
            var calls = new List<HostCall>(HostCallParser.Parse(responseText));
            if (calls.Count == 0) return;

            // Get the active connection's ConnectionInfo from the focused tab
            ConnectionInfo connectionInfo = null;
            try
            {
                ConnectionTab activeTab = TabHelper.Instance.CurrentTab;
                if (activeTab?.Tag is InterfaceControl ifc)
                    connectionInfo = ifc.Info;
            }
            catch { /* no active tab — connectionInfo stays null */ }

            if (connectionInfo == null)
            {
                AppendLine("[System] Host-call tags found but no active SSH connection.");
                AppendLine("");
                return;
            }

            // Show the transfer window the first time
            AppWindows.Show(WindowType.ScpTransfer);

            foreach (HostCall call in calls)
            {
                string filename = System.IO.Path.GetFileName(call.Payload.TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(filename)) filename = call.Payload;
                string direction = call.Action == "file-download" ? "Download" : "Upload";

                // AddTransfer returns the Guid that HostCallExecutor will raise events under
                Guid transferId = AppWindows.ScpTransferForm.AddTransfer(direction, filename, connectionInfo.Hostname);
                _ = HostCallExecutor.ExecuteAsync(call, connectionInfo, transferId);
            }
        }

        private void AppendLine(string text)
        {
            if (_txtHistory.InvokeRequired)
            {
                _txtHistory.Invoke(() => AppendLine(text));
                return;
            }

            _txtHistory.AppendText(text + Environment.NewLine);
            _txtHistory.ScrollToCaret();
        }
    }
}
