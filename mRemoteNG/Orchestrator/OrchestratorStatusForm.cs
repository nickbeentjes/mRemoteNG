using System;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace mRemoteNG.Orchestrator
{
    /// <summary>
    /// Modeless form that shows the live status of a running <see cref="OrchestratorEngine"/>.
    /// Open it via "What are you doing?" menu item on a tab that is being orchestrated.
    /// No .Designer.cs — all controls are built in code.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class OrchestratorStatusForm : Form
    {
        private readonly OrchestratorEngine _engine;

        // ------------------------------------------------------------------ controls

        private Label       _lblTask        = null!;
        private Label       _lblStatusLine  = null!;
        private RichTextBox _rtbSummary     = null!;
        private Label       _lblSummaryNote = null!;
        private ListView    _lvJournal      = null!;
        private Button      _btnRefresh     = null!;
        private Button      _btnStop        = null!;
        private Button      _btnClose       = null!;

        // ------------------------------------------------------------------ constructor

        public OrchestratorStatusForm(OrchestratorEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            Text             = $"Orchestrating: {engine.SessionId}";
            FormBorderStyle  = FormBorderStyle.Sizable;
            StartPosition    = FormStartPosition.CenterParent;
            MinimizeBox      = false;
            ShowInTaskbar    = true;
            Width            = 680;
            Height           = 540;
            MinimumSize      = new Size(480, 380);

            BuildLayout();
            PopulateJournal();
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            int pad = 10;

            // ---- Top info panel ----------------------------------------
            var pnlTop = new Panel
            {
                Dock    = DockStyle.Top,
                Height  = 56,
                Padding = new Padding(pad, pad, pad, 0)
            };

            _lblTask = new Label
            {
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 18,
                Font      = new Font("Segoe UI", 9F, FontStyle.Bold),
                Text      = $"Task: {TruncateStr(_engine.TaskContext, 120)}"
            };

            _lblStatusLine = new Label
            {
                AutoSize = false,
                Dock     = DockStyle.Top,
                Height   = 18,
                Font     = new Font("Segoe UI", 9F),
                Text     = BuildStatusLine()
            };

            // Add bottom-first so Dock stacks correctly
            pnlTop.Controls.Add(_lblTask);
            pnlTop.Controls.Add(_lblStatusLine);

            // ---- Summary richtext (4 lines ≈ 72px) ---------------------
            var pnlSummary = new Panel
            {
                Dock   = DockStyle.Top,
                Height = 90
            };

            var lblSummaryHeader = new Label
            {
                Text     = "Claude's summary",
                Dock     = DockStyle.Top,
                Height   = 18,
                Font     = new Font("Segoe UI", 8F, FontStyle.Bold),
                Padding  = new Padding(pad, 4, 0, 0)
            };

            _rtbSummary = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                BackColor   = SystemColors.Control,
                Font        = new Font("Segoe UI", 9F),
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                Text        = "Thinking..."
            };

            pnlSummary.Controls.Add(_rtbSummary);
            pnlSummary.Controls.Add(lblSummaryHeader);

            // ---- Journal header ----------------------------------------
            var pnlJournalHeader = new Panel
            {
                Dock   = DockStyle.Top,
                Height = 22
            };

            var lblJournal = new Label
            {
                Text    = "Journal",
                Dock    = DockStyle.Fill,
                Font    = new Font("Segoe UI", 8F, FontStyle.Bold),
                Padding = new Padding(pad, 4, 0, 0)
            };
            pnlJournalHeader.Controls.Add(lblJournal);

            // ---- Journal list ------------------------------------------
            _lvJournal = new ListView
            {
                Dock      = DockStyle.Fill,
                View      = View.Details,
                FullRowSelect = true,
                GridLines = false,
                Font      = new Font("Consolas", 8.5F)
            };
            _lvJournal.Columns.Add("Time",    70,  HorizontalAlignment.Left);
            _lvJournal.Columns.Add("Type",    90,  HorizontalAlignment.Left);
            _lvJournal.Columns.Add("Content", 460, HorizontalAlignment.Left);

            // ---- Button strip ------------------------------------------
            var pnlButtons = new Panel
            {
                Dock   = DockStyle.Bottom,
                Height = 38
            };

            _btnClose = new Button
            {
                Text   = "Close",
                Width  = 75,
                Height = 26,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnClose.Left   = ClientSize.Width - pad - _btnClose.Width;
            _btnClose.Top    = 6;
            _btnClose.Click += (_, _) => Close();

            _btnStop = new Button
            {
                Text   = "Stop Orchestration",
                Width  = 130,
                Height = 26,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnStop.Left   = _btnClose.Left - _btnStop.Width - 6;
            _btnStop.Top    = 6;
            _btnStop.Click += BtnStop_Click;

            _btnRefresh = new Button
            {
                Text   = "Refresh",
                Width  = 75,
                Height = 26,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            _btnRefresh.Left   = pad;
            _btnRefresh.Top    = 6;
            _btnRefresh.Click += BtnRefresh_Click;

            pnlButtons.Controls.Add(_btnRefresh);
            pnlButtons.Controls.Add(_btnStop);
            pnlButtons.Controls.Add(_btnClose);

            // Wire anchor on resize for buttons
            SizeChanged += (_, _) =>
            {
                _btnClose.Left = ClientSize.Width - pad - _btnClose.Width;
                _btnStop.Left  = _btnClose.Left - _btnStop.Width - 6;
            };

            // ---- Assemble (Dock Fill must be added LAST) ---------------
            Controls.Add(_lvJournal);
            Controls.Add(pnlJournalHeader);
            Controls.Add(pnlSummary);
            Controls.Add(pnlTop);
            Controls.Add(pnlButtons);
        }

        // ------------------------------------------------------------------ load / refresh

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await RefreshSummaryAsync();
        }

        private async void BtnRefresh_Click(object sender, EventArgs e)
        {
            _btnRefresh.Enabled = false;
            try
            {
                PopulateJournal();
                _lblStatusLine.Text = BuildStatusLine();
                await RefreshSummaryAsync();
            }
            finally
            {
                _btnRefresh.Enabled = true;
            }
        }

        private async System.Threading.Tasks.Task RefreshSummaryAsync()
        {
            _rtbSummary.Text = "Thinking...";
            try
            {
                string summary = await _engine.GetStatusSummaryAsync();
                if (!IsDisposed)
                    _rtbSummary.Text = summary;
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    _rtbSummary.Text = $"[Error getting summary: {ex.Message}]";
            }
        }

        // ------------------------------------------------------------------ stop

        private void BtnStop_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Stop the orchestration for this session?",
                "Stop Orchestration",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                OrchestratorRegistry.Stop(_engine.SessionId);
                Close();
            }
        }

        // ------------------------------------------------------------------ journal population

        private void PopulateJournal()
        {
            _lvJournal.BeginUpdate();
            _lvJournal.Items.Clear();

            foreach (var entry in _engine.Journal.AsEnumerable().Reverse())
            {
                var item = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss"));
                item.SubItems.Add(entry.Type);
                item.SubItems.Add(TruncateStr(entry.Content, 300));

                item.ForeColor = entry.Type switch
                {
                    "command"     => Color.DarkBlue,
                    "blocked"     => Color.DarkRed,
                    "done"        => Color.DarkGreen,
                    "observation" => Color.DimGray,
                    _             => SystemColors.WindowText
                };

                _lvJournal.Items.Add(item);
            }

            _lvJournal.EndUpdate();
        }

        // ------------------------------------------------------------------ helpers

        private string BuildStatusLine()
        {
            string icon = _engine.Status switch
            {
                OrchestratorStatus.Running  => "Running",
                OrchestratorStatus.Waiting  => "Waiting",
                OrchestratorStatus.Blocked  => "BLOCKED",
                OrchestratorStatus.Done     => "Done",
                OrchestratorStatus.Error    => "Error",
                OrchestratorStatus.Stopped  => "Stopped",
                _                           => _engine.Status.ToString()
            };

            string lastAction = _engine.LastActionAt.HasValue
                ? _engine.LastActionAt.Value.ToString("HH:mm")
                : "—";

            return $"Status: {icon}   |   Started: {_engine.StartedAt:HH:mm}   |   Last action: {lastAction}";
        }

        private static string TruncateStr(string s, int maxLen) =>
            s.Length <= maxLen ? s : s[..maxLen] + "…";
    }
}
