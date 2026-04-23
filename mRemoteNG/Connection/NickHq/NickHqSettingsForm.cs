using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace mRemoteNG.Connection.NickHq
{
    /// <summary>
    /// Modal settings dialog for managing NickHQ server entries.
    /// Opens via Tools → NickHQ Servers…
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class NickHqSettingsForm : Form
    {
        // ------------------------------------------------------------------ controls
        private ListView _listView;
        private Button _btnAdd;
        private Button _btnEdit;
        private Button _btnRemove;
        private CheckBox _chkConnectOnStartup;
        private Button _btnOk;
        private Button _btnCancel;

        // ------------------------------------------------------------------ state
        private List<NickHqServer> _servers;
        private bool _connectOnStartup;

        // ------------------------------------------------------------------ ctor

        public NickHqSettingsForm()
        {
            _servers = new List<NickHqServer>(NickHqConfig.Load());
            _connectOnStartup = NickHqConfig.ConnectOnStartup;
            BuildLayout();
            PopulateList();
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            Text = "NickHQ Servers";
            Size = new Size(680, 440);
            MinimumSize = new Size(580, 360);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            // ListView
            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                View = View.Details,
                HideSelection = false
            };
            _listView.Columns.Add("Name", 160);
            _listView.Columns.Add("URL", 270);
            _listView.Columns.Add("Auto-connect", 90);
            _listView.Columns.Add("Enabled", 70);
            _listView.DoubleClick += (_, __) => EditSelected();
            _listView.SelectedIndexChanged += (_, __) => UpdateButtonState();

            // Right-side button panel
            _btnAdd = new Button { Text = "Add", Width = 80, Height = 26 };
            _btnAdd.Click += (_, __) => AddServer();

            _btnEdit = new Button { Text = "Edit", Width = 80, Height = 26, Enabled = false };
            _btnEdit.Click += (_, __) => EditSelected();

            _btnRemove = new Button { Text = "Remove", Width = 80, Height = 26, Enabled = false };
            _btnRemove.Click += (_, __) => RemoveSelected();

            var btnPanel = new Panel { Dock = DockStyle.Right, Width = 96 };
            btnPanel.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnRemove });
            _btnAdd.Location = new Point(8, 8);
            _btnEdit.Location = new Point(8, 42);
            _btnRemove.Location = new Point(8, 76);

            // Centre panel = list + side buttons
            var centrePanel = new Panel { Dock = DockStyle.Fill };
            centrePanel.Controls.Add(_listView);
            centrePanel.Controls.Add(btnPanel);

            // Startup checkbox strip
            _chkConnectOnStartup = new CheckBox
            {
                Text = "Connect to enabled servers on startup",
                Checked = _connectOnStartup,
                AutoSize = true,
                Padding = new Padding(4, 0, 0, 0)
            };
            _chkConnectOnStartup.CheckedChanged += (_, __) => _connectOnStartup = _chkConnectOnStartup.Checked;

            var startupPanel = new Panel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(4) };
            startupPanel.Controls.Add(_chkConnectOnStartup);
            _chkConnectOnStartup.Location = new Point(4, 6);

            // Bottom OK / Cancel bar
            _btnOk = new Button { Text = "OK", Width = 80, Height = 26, DialogResult = DialogResult.OK };
            _btnOk.Click += BtnOk_Click;

            _btnCancel = new Button { Text = "Cancel", Width = 80, Height = 26, DialogResult = DialogResult.Cancel };

            var okPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 38,
                Padding = new Padding(4)
            };
            okPanel.Controls.AddRange(new Control[] { _btnCancel, _btnOk });

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Controls.Add(centrePanel);
            Controls.Add(startupPanel);
            Controls.Add(okPanel);
        }

        // ------------------------------------------------------------------ list population

        private void PopulateList()
        {
            _listView.Items.Clear();
            foreach (var s in _servers)
            {
                var item = new ListViewItem(s.Name) { Tag = s };
                item.SubItems.Add(s.Url);
                item.SubItems.Add(s.AutoConnect ? "Yes" : "No");
                item.SubItems.Add(s.Enabled ? "Yes" : "No");
                _listView.Items.Add(item);
            }
            UpdateButtonState();
        }

        private void UpdateButtonState()
        {
            bool hasSelection = _listView.SelectedItems.Count > 0;
            _btnEdit.Enabled = hasSelection;
            _btnRemove.Enabled = hasSelection;
        }

        // ------------------------------------------------------------------ CRUD

        private void AddServer()
        {
            var server = new NickHqServer { Name = "New Server" };
            using var dlg = new NickHqServerEditForm(server);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _servers.Add(server);
                PopulateList();
            }
        }

        private void EditSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var server = (NickHqServer)_listView.SelectedItems[0].Tag!;
            using var dlg = new NickHqServerEditForm(server);
            if (dlg.ShowDialog(this) == DialogResult.OK)
                PopulateList();
        }

        private void RemoveSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var server = (NickHqServer)_listView.SelectedItems[0].Tag!;
            if (MessageBox.Show($"Remove server \"{server.Name}\"?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _servers.Remove(server);
                PopulateList();
            }
        }

        // ------------------------------------------------------------------ save

        private void BtnOk_Click(object sender, EventArgs e)
        {
            NickHqConfig.SaveConnectOnStartup(_connectOnStartup, _servers);
            NickHqConfig.Invalidate(); // force reload on next NickHqClient access
        }
    }

    // ======================================================================
    // Edit/Add dialog for a single NickHqServer
    // ======================================================================

    [SupportedOSPlatform("windows")]
    internal sealed class NickHqServerEditForm : Form
    {
        private readonly NickHqServer _server;

        private TextBox _txtName;
        private TextBox _txtUrl;
        private TextBox _txtBearer;
        private TextBox _txtAgent;
        private CheckBox _chkEnabled;
        private CheckBox _chkAutoConnect;
        private Button _btnTest;
        private Label _lblTestResult;
        private Button _btnOk;
        private Button _btnCancel;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public NickHqServerEditForm(NickHqServer server)
        {
            _server = server;
            BuildLayout();
            LoadFromServer();
        }

        private void BuildLayout()
        {
            Text = "Edit NickHQ Server";
            Size = new Size(500, 360);
            MinimumSize = new Size(440, 340);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(10),
                AutoSize = true
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int row = 0;

            _txtName = AddRow(table, "Name:", row++, "");
            _txtUrl = AddRow(table, "URL:", row++, "https://your-mac.taile6c48b.ts.net");
            _txtBearer = AddRow(table, "Bearer Token:", row++, "NICKHQ_API_TOKEN value");
            _txtAgent = AddRow(table, "Agent Token:", row++, "AGENT_TOKEN value");

            // Checkboxes
            _chkEnabled = new CheckBox { Text = "Enabled", Checked = true, AutoSize = true };
            _chkAutoConnect = new CheckBox { Text = "Auto-connect on startup", Checked = true, AutoSize = true };

            var checkPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false
            };
            checkPanel.Controls.Add(_chkEnabled);
            checkPanel.Controls.Add(new Label { Width = 20 }); // spacer
            checkPanel.Controls.Add(_chkAutoConnect);

            table.Controls.Add(new Label { Text = "Options:", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill });
            table.Controls.Add(checkPanel);
            table.SetRow(checkPanel, row);
            table.SetColumn(checkPanel, 1);
            row++;

            // Test connection row
            _btnTest = new Button { Text = "Test Connection", Width = 120, Height = 26 };
            _btnTest.Click += BtnTest_Click;
            _lblTestResult = new Label { AutoSize = true, Padding = new Padding(4, 4, 0, 0) };

            var testPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false
            };
            testPanel.Controls.Add(_btnTest);
            testPanel.Controls.Add(_lblTestResult);

            table.Controls.Add(new Label());
            table.Controls.Add(testPanel);
            table.SetRow(testPanel, row);
            table.SetColumn(testPanel, 1);
            row++;

            // OK / Cancel
            _btnOk = new Button { Text = "OK", Width = 80, Height = 26, DialogResult = DialogResult.OK };
            _btnOk.Click += BtnOk_Click;
            _btnCancel = new Button { Text = "Cancel", Width = 80, Height = 26, DialogResult = DialogResult.Cancel };

            var okPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 38,
                Padding = new Padding(4)
            };
            okPanel.Controls.AddRange(new Control[] { _btnCancel, _btnOk });

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Controls.Add(table);
            Controls.Add(okPanel);
        }

        private static TextBox AddRow(TableLayoutPanel table, string label, int row, string placeholder)
        {
            var lbl = new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill
            };
            var txt = new TextBox
            {
                Dock = DockStyle.Fill,
                PlaceholderText = placeholder
            };
            table.Controls.Add(lbl);
            table.Controls.Add(txt);
            table.SetRow(lbl, row);
            table.SetColumn(lbl, 0);
            table.SetRow(txt, row);
            table.SetColumn(txt, 1);
            return txt;
        }

        private void LoadFromServer()
        {
            _txtName.Text = _server.Name;
            _txtUrl.Text = _server.Url;
            _txtBearer.Text = _server.BearerToken;
            _txtAgent.Text = _server.AgentToken;
            _chkEnabled.Checked = _server.Enabled;
            _chkAutoConnect.Checked = _server.AutoConnect;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            _server.Name = _txtName.Text.Trim();
            _server.Url = _txtUrl.Text.Trim().TrimEnd('/');
            _server.BearerToken = _txtBearer.Text.Trim();
            _server.AgentToken = _txtAgent.Text.Trim();
            _server.Enabled = _chkEnabled.Checked;
            _server.AutoConnect = _chkAutoConnect.Checked;
        }

        private async void BtnTest_Click(object sender, EventArgs e)
        {
            _btnTest.Enabled = false;
            _lblTestResult.ForeColor = SystemColors.ControlText;
            _lblTestResult.Text = "Testing…";

            string url = _txtUrl.Text.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(url))
            {
                _lblTestResult.Text = "Enter a URL first.";
                _btnTest.Enabled = true;
                return;
            }

            try
            {
                var resp = await _http.GetAsync($"{url}/health");
                if (resp.IsSuccessStatusCode)
                {
                    _lblTestResult.ForeColor = Color.Green;
                    _lblTestResult.Text = "Reachable";
                }
                else
                {
                    _lblTestResult.ForeColor = Color.DarkRed;
                    _lblTestResult.Text = $"Failed: HTTP {(int)resp.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                _lblTestResult.ForeColor = Color.DarkRed;
                _lblTestResult.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                _btnTest.Enabled = true;
            }
        }
    }
}
