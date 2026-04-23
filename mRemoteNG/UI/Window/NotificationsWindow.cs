using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using mRemoteNG.Connection.NickHq;
using mRemoteNG.Orchestrator;

namespace mRemoteNG.UI.Window
{
    [SupportedOSPlatform("windows")]
    public class NotificationsWindow : BaseWindow
    {
        // ------------------------------------------------------------------ controls
        private Label      _lblUnread;
        private Button     _btnMarkAllRead;
        private Button     _btnClear;
        private ListView   _listView;
        private RichTextBox _txtBody;
        private Button     _btnGoToSession;
        private Button     _btnWhatsHappening;
        private Button     _btnStop;
        private Button     _btnDismiss;
        private Panel      _pnlTop;
        private Panel      _pnlActions;

        // ------------------------------------------------------------------ state
        private readonly HttpClient _httpClient = new();
        private readonly System.Windows.Forms.Timer _pollTimer;

        /// <summary>Locally cached alerts, keyed by id.</summary>
        private readonly Dictionary<string, NickHqAlert> _alerts = new(StringComparer.Ordinal);

        // ------------------------------------------------------------------ ctor

        public NotificationsWindow() : this(new DockContent()) { }

        public NotificationsWindow(DockContent panel)
        {
            WindowType  = WindowType.NotificationsWindow;
            DockPnl     = panel;
            Text        = "Notifications";
            TabText     = "Notifications";
            HideOnClose = true;

            InitializeControls();

            _pollTimer          = new System.Windows.Forms.Timer { Interval = 15000 };
            _pollTimer.Tick    += async (_, _) => await PollAlertsAsync();
            _pollTimer.Start();
        }

        // ------------------------------------------------------------------ layout

        private void InitializeControls()
        {
            // ---- top bar ---------------------------------------------------
            _lblUnread = new Label
            {
                Text      = "No alerts",
                AutoSize  = false,
                Width     = 160,
                Height    = 24,
                Dock      = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            _btnClear = new Button
            {
                Text   = "Clear",
                Width  = 60,
                Height = 24,
                Dock   = DockStyle.Right,
                Font   = new Font("Segoe UI", 9F)
            };
            _btnClear.Click += BtnClear_Click;

            _btnMarkAllRead = new Button
            {
                Text   = "Mark all read",
                Width  = 100,
                Height = 24,
                Dock   = DockStyle.Right,
                Font   = new Font("Segoe UI", 9F)
            };
            _btnMarkAllRead.Click += BtnMarkAllRead_Click;

            _pnlTop = new Panel { Dock = DockStyle.Top, Height = 28 };
            _pnlTop.Controls.Add(_lblUnread);
            _pnlTop.Controls.Add(_btnClear);
            _pnlTop.Controls.Add(_btnMarkAllRead);

            // ---- list view -------------------------------------------------
            _listView = new ListView
            {
                Dock          = DockStyle.Fill,
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = false,
                Font          = new Font("Segoe UI", 9F)
            };
            _listView.Columns.Add("",        18,  HorizontalAlignment.Center); // level icon
            _listView.Columns.Add("Title",   180, HorizontalAlignment.Left);
            _listView.Columns.Add("Session", 90,  HorizontalAlignment.Left);
            _listView.Columns.Add("Time",    52,  HorizontalAlignment.Left);
            _listView.Columns.Add("Source",  90,  HorizontalAlignment.Left);
            _listView.SelectedIndexChanged += ListView_SelectedIndexChanged;

            // ---- body text -------------------------------------------------
            var txtBodyPanel = new Panel { Dock = DockStyle.Bottom, Height = 72 };
            _txtBody = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                ReadOnly    = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 9F),
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                BackColor   = SystemColors.Window
            };
            txtBodyPanel.Controls.Add(_txtBody);

            // ---- action buttons --------------------------------------------
            _btnGoToSession = new Button
            {
                Text    = "Go to Session",
                Width   = 100,
                Height  = 26,
                Enabled = false,
                Font    = new Font("Segoe UI", 9F)
            };
            _btnGoToSession.Click += BtnGoToSession_Click;

            _btnWhatsHappening = new Button
            {
                Text    = "What's happening?",
                Width   = 130,
                Height  = 26,
                Enabled = false,
                Font    = new Font("Segoe UI", 9F)
            };
            _btnWhatsHappening.Click += BtnWhatsHappening_Click;

            _btnStop = new Button
            {
                Text    = "Stop",
                Width   = 60,
                Height  = 26,
                Enabled = false,
                Font    = new Font("Segoe UI", 9F)
            };
            _btnStop.Click += BtnStop_Click;

            _btnDismiss = new Button
            {
                Text    = "Dismiss",
                Width   = 70,
                Height  = 26,
                Enabled = false,
                Font    = new Font("Segoe UI", 9F)
            };
            _btnDismiss.Click += BtnDismiss_Click;

            var flpActions = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding       = new Padding(2)
            };
            flpActions.Controls.AddRange(new Control[]
            {
                _btnGoToSession, _btnWhatsHappening, _btnStop, _btnDismiss
            });

            _pnlActions = new Panel { Dock = DockStyle.Bottom, Height = 34 };
            _pnlActions.Controls.Add(flpActions);

            // ---- assemble (Fill must be added last) -----------------------
            Controls.Add(_listView);
            Controls.Add(txtBodyPanel);
            Controls.Add(_pnlActions);
            Controls.Add(_pnlTop);
        }

        // ------------------------------------------------------------------ polling

        private async Task PollAlertsAsync()
        {
            List<NickHqServer> servers = NickHqConfig.Load();
            bool gotNew = false;

            foreach (NickHqServer server in servers)
            {
                if (!server.Enabled) continue;
                if (string.IsNullOrWhiteSpace(server.Url)) continue;

                try
                {
                    string url = server.Url.TrimEnd('/') + "/alerts?unread=true";
                    using HttpRequestMessage req = new(HttpMethod.Get, url);
                    if (!string.IsNullOrWhiteSpace(server.BearerToken))
                        req.Headers.Add("Authorization", "Bearer " + server.BearerToken);

                    using HttpResponseMessage resp = await _httpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;

                    string json = await resp.Content.ReadAsStringAsync();
                    List<NickHqAlert> fetched = ParseAlerts(json, server.Name);

                    foreach (NickHqAlert alert in fetched)
                    {
                        if (!_alerts.ContainsKey(alert.Id))
                        {
                            _alerts[alert.Id] = alert;
                            gotNew = true;
                        }
                    }
                }
                catch
                {
                    // swallow — connection failure is non-fatal
                }
            }

            if (gotNew)
                UpdateUiFromAlerts();
        }

        private static List<NickHqAlert> ParseAlerts(string json, string serverName)
        {
            var result = new List<NickHqAlert>();
            try
            {
                JsonNode? root = JsonNode.Parse(json);
                JsonNode? arr  = root is JsonArray ? root : root?["alerts"] ?? root?["items"];
                if (arr is not JsonArray items) return result;

                foreach (JsonNode? node in items)
                {
                    if (node == null) continue;
                    result.Add(new NickHqAlert
                    {
                        Id        = node["id"]?.GetValue<string>()         ?? Guid.NewGuid().ToString(),
                        Title     = node["title"]?.GetValue<string>()      ?? "(no title)",
                        Body      = node["body"]?.GetValue<string>()       ?? "",
                        SessionId = node["session_id"]?.GetValue<string>() ?? "",
                        Level     = node["level"]?.GetValue<string>()      ?? "info",
                        Source    = node["source"]?.GetValue<string>()     ?? serverName,
                        Timestamp = TryParseTime(node["timestamp"]?.GetValue<string>()),
                        IsRead    = node["read"]?.GetValue<bool>() ?? false
                    });
                }
            }
            catch { /* malformed JSON — return empty */ }
            return result;
        }

        private static DateTime TryParseTime(string? s)
        {
            if (s != null && DateTime.TryParse(s, out DateTime dt))
                return dt;
            return DateTime.Now;
        }

        // ------------------------------------------------------------------ UI update

        private void UpdateUiFromAlerts()
        {
            if (InvokeRequired) { BeginInvoke(UpdateUiFromAlerts); return; }

            _listView.BeginUpdate();
            _listView.Items.Clear();

            int unread = 0;
            bool hasError = false, hasWarning = false;

            foreach (NickHqAlert alert in _alerts.Values)
            {
                if (!alert.IsRead) unread++;
                if (alert.Level == "error")   hasError   = true;
                if (alert.Level == "warning")  hasWarning = true;

                (string icon, Color color) = LevelInfo(alert.Level);

                var item = new ListViewItem(icon)
                {
                    Tag      = alert,
                    ForeColor = color,
                    Font     = alert.IsRead
                                   ? new Font("Segoe UI", 9F, FontStyle.Regular)
                                   : new Font("Segoe UI", 9F, FontStyle.Bold)
                };
                item.SubItems.Add(alert.Title);
                item.SubItems.Add(alert.SessionId);
                item.SubItems.Add(alert.Timestamp.ToString("HH:mm"));
                item.SubItems.Add(alert.Source);

                _listView.Items.Add(item);
            }

            _listView.EndUpdate();

            // Update unread label
            _lblUnread.Text = unread > 0 ? $"{unread} unread" : "No unread alerts";

            // Flash tab text
            TabText = unread > 0 ? $"Notifications ({unread})" : "Notifications";

            // Auto-show for errors/warnings
            if ((hasError || hasWarning) && IsHidden)
            {
                Show();
                Activate();
            }
        }

        private bool IsHidden =>
            DockState == DockState.Hidden || DockState == DockState.Unknown;

        private static (string icon, Color color) LevelInfo(string level) => level switch
        {
            "error"   => ("●", Color.Red),
            "warning" => ("●", Color.Orange),
            "done"    => ("●", Color.Green),
            _         => ("●", Color.Gray),
        };

        // ------------------------------------------------------------------ ListView events

        private void ListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool hasSelection = _listView.SelectedItems.Count > 0;
            _btnGoToSession.Enabled    = hasSelection;
            _btnWhatsHappening.Enabled = hasSelection;
            _btnStop.Enabled           = hasSelection;
            _btnDismiss.Enabled        = hasSelection;

            if (!hasSelection) { _txtBody.Clear(); return; }

            NickHqAlert? alert = _listView.SelectedItems[0].Tag as NickHqAlert;
            if (alert == null) return;

            _txtBody.Text = alert.Body;

            // Mark read locally
            alert.IsRead = true;
            _listView.SelectedItems[0].Font =
                new Font("Segoe UI", 9F, FontStyle.Regular);
            UpdateUnreadLabel();
        }

        private void UpdateUnreadLabel()
        {
            int unread = 0;
            foreach (NickHqAlert a in _alerts.Values)
                if (!a.IsRead) unread++;

            _lblUnread.Text = unread > 0 ? $"{unread} unread" : "No unread alerts";
            TabText         = unread > 0 ? $"Notifications ({unread})" : "Notifications";
        }

        // ------------------------------------------------------------------ action buttons

        private void BtnGoToSession_Click(object sender, EventArgs e)
        {
            NickHqAlert? alert = SelectedAlert();
            if (alert == null || string.IsNullOrEmpty(alert.SessionId)) return;

            if (SessionTabRegistry.TryGet(alert.SessionId, out var tab))
            {
                tab.Show();
                tab.Activate();
            }
            else
            {
                MessageBox.Show($"No open tab found for session '{alert.SessionId}'.",
                    "Go to Session", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void BtnWhatsHappening_Click(object sender, EventArgs e)
        {
            NickHqAlert? alert = SelectedAlert();
            if (alert == null || string.IsNullOrEmpty(alert.SessionId)) return;

            if (OrchestratorRegistry.IsOrchestrated(alert.SessionId))
            {
                OrchestratorEngine? engine = OrchestratorRegistry.Get(alert.SessionId);
                if (engine != null)
                    new OrchestratorStatusForm(engine).Show();
            }
            else
            {
                MessageBox.Show("No active orchestration for this session.",
                    "What's happening?", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            NickHqAlert? alert = SelectedAlert();
            if (alert == null || string.IsNullOrEmpty(alert.SessionId)) return;

            if (!OrchestratorRegistry.IsOrchestrated(alert.SessionId))
            {
                MessageBox.Show("No active orchestration for this session.",
                    "Stop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult confirm = MessageBox.Show(
                $"Stop orchestration for session '{alert.SessionId}'?",
                "Stop Orchestration", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                OrchestratorRegistry.Stop(alert.SessionId);
                _btnStop.Enabled = false;
            }
        }

        private async void BtnDismiss_Click(object sender, EventArgs e)
        {
            NickHqAlert? alert = SelectedAlert();
            if (alert == null) return;

            await DeleteAlertAsync(alert);

            _alerts.Remove(alert.Id);
            if (_listView.SelectedItems.Count > 0)
                _listView.Items.Remove(_listView.SelectedItems[0]);

            _txtBody.Clear();
            UpdateUnreadLabel();
        }

        private async void BtnMarkAllRead_Click(object sender, EventArgs e)
        {
            List<NickHqServer> servers = NickHqConfig.Load();
            foreach (NickHqServer server in servers)
            {
                if (!server.Enabled || string.IsNullOrWhiteSpace(server.Url)) continue;
                try
                {
                    string url = server.Url.TrimEnd('/') + "/alerts/read";
                    using HttpRequestMessage req = new(HttpMethod.Post, url);
                    if (!string.IsNullOrWhiteSpace(server.BearerToken))
                        req.Headers.Add("Authorization", "Bearer " + server.BearerToken);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    await _httpClient.SendAsync(req);
                }
                catch { /* non-fatal */ }
            }

            // Mark all local alerts read
            foreach (NickHqAlert a in _alerts.Values)
                a.IsRead = true;

            foreach (ListViewItem item in _listView.Items)
                item.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            UpdateUnreadLabel();
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            _alerts.Clear();
            _listView.Items.Clear();
            _txtBody.Clear();
            _lblUnread.Text = "No unread alerts";
            TabText         = "Notifications";
        }

        // ------------------------------------------------------------------ HTTP helpers

        private async Task DeleteAlertAsync(NickHqAlert alert)
        {
            // Find a server to delete from (match by Source name, fall back to first enabled)
            List<NickHqServer> servers = NickHqConfig.Load();
            NickHqServer? target = null;
            foreach (NickHqServer s in servers)
            {
                if (!s.Enabled || string.IsNullOrWhiteSpace(s.Url)) continue;
                if (s.Name == alert.Source || target == null)
                    target = s;
                if (s.Name == alert.Source) break;
            }

            if (target == null) return;

            try
            {
                string url = target.Url.TrimEnd('/') + "/alerts/" + Uri.EscapeDataString(alert.Id);
                using HttpRequestMessage req = new(HttpMethod.Delete, url);
                if (!string.IsNullOrWhiteSpace(target.BearerToken))
                    req.Headers.Add("Authorization", "Bearer " + target.BearerToken);
                await _httpClient.SendAsync(req);
            }
            catch { /* non-fatal */ }
        }

        // ------------------------------------------------------------------ helpers

        private NickHqAlert? SelectedAlert() =>
            _listView.SelectedItems.Count > 0
                ? _listView.SelectedItems[0].Tag as NickHqAlert
                : null;
    }

    // ---------------------------------------------------------------------- data model

    internal class NickHqAlert
    {
        public string   Id        { get; set; } = "";
        public string   Title     { get; set; } = "";
        public string   Body      { get; set; } = "";
        public string   SessionId { get; set; } = "";
        public string   Level     { get; set; } = "info";
        public string   Source    { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool     IsRead    { get; set; }
    }
}
