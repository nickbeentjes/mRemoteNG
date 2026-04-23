using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace mRemoteNG.Orchestrator
{
    /// <summary>
    /// Modal dialog that lets the user describe a task to orchestrate on a remote session.
    /// Calls <see cref="OrchestratorRegistry.Start"/> when the user confirms.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class OrchestrateSessionDialog : Form
    {
        private readonly string _sessionId;

        private RichTextBox _txtContext = null!;
        private CheckBox _chkAutoGsd = null!;
        private CheckBox _chkStopOnError = null!;
        private CheckBox _chkNotifyDone = null!;
        private CheckBox _chkNotifyBlocked = null!;
        private Button _btnStart = null!;
        private Button _btnCancel = null!;

        /// <summary>
        /// Creates the dialog.
        /// </summary>
        /// <param name="sessionId">The NickHQ session ID passed to <see cref="OrchestratorRegistry.Start"/>.</param>
        /// <param name="sessionLabel">Human-readable label shown in the title bar (e.g. hostname).</param>
        public OrchestrateSessionDialog(string sessionId, string sessionLabel)
        {
            _sessionId = sessionId;

            Text = $"Orchestrate Session: {sessionLabel}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 520;
            Height = 310;

            BuildLayout();
        }

        private void BuildLayout()
        {
            int pad = 12;
            int y = pad;

            // Prompt label
            var lblPrompt = new Label
            {
                Text = "What do you want me to do on this session?",
                Left = pad,
                Top = y,
                Width = ClientSize.Width - pad * 2,
                AutoSize = true
            };
            Controls.Add(lblPrompt);
            y += lblPrompt.Height + 4;

            // Task description text box
            _txtContext = new RichTextBox
            {
                Left = pad,
                Top = y,
                Width = ClientSize.Width - pad * 2,
                Height = 95,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                Font = new Font("Segoe UI", 9F),
                AcceptsTab = false
            };
            Controls.Add(_txtContext);
            y += _txtContext.Height + 10;

            // Option checkboxes
            _chkAutoGsd = MakeCheckbox("Auto-answer GSD prompts", pad, y, true);
            Controls.Add(_chkAutoGsd);
            y += _chkAutoGsd.Height + 4;

            _chkStopOnError = MakeCheckbox("Stop on error (default: notify and continue)", pad, y, false);
            Controls.Add(_chkStopOnError);
            y += _chkStopOnError.Height + 4;

            _chkNotifyDone = MakeCheckbox("Notify me when done", pad, y, true);
            Controls.Add(_chkNotifyDone);
            y += _chkNotifyDone.Height + 4;

            _chkNotifyBlocked = MakeCheckbox("Notify me if blocked", pad, y, true);
            Controls.Add(_chkNotifyBlocked);
            y += _chkNotifyBlocked.Height + 14;

            // Buttons (right-aligned)
            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 80,
                Height = 26
            };
            _btnCancel.Left = ClientSize.Width - pad - _btnCancel.Width;
            _btnCancel.Top = y;
            Controls.Add(_btnCancel);

            _btnStart = new Button
            {
                Text = "Start Orchestration",
                Width = 130,
                Height = 26
            };
            _btnStart.Left = _btnCancel.Left - _btnStart.Width - 6;
            _btnStart.Top = y;
            _btnStart.Click += BtnStart_Click;
            Controls.Add(_btnStart);

            AcceptButton = _btnStart;
            CancelButton = _btnCancel;

            // Expand form height to fit all controls
            ClientSize = new Size(ClientSize.Width, y + _btnStart.Height + pad);
        }

        private static CheckBox MakeCheckbox(string text, int left, int top, bool @checked)
        {
            return new CheckBox
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                Checked = @checked
            };
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtContext.Text))
            {
                MessageBox.Show(
                    "Please describe what you want the orchestrator to do.",
                    "Task Required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _txtContext.Focus();
                return;
            }

            var options = new OrchestratorOptions
            {
                AutoAnswerGsdPrompts = _chkAutoGsd.Checked,
                StopOnError = _chkStopOnError.Checked,
                NotifyOnDone = _chkNotifyDone.Checked,
                NotifyOnBlocked = _chkNotifyBlocked.Checked,
            };

            OrchestratorRegistry.Start(_sessionId, _txtContext.Text, options);

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
