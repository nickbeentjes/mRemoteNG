using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using mRemoteNG.App;
using mRemoteNG.App.Info;
using mRemoteNG.Config;
using mRemoteNG.Connection;
using mRemoteNG.Connection.HostCall;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.Connection.Protocol.VNC;
using mRemoteNG.Orchestrator;
using mRemoteNG.Properties;
using mRemoteNG.UI;
using mRemoteNG.UI.TaskDialog;
using mRemoteNG.UI.Window;
using WeifenLuo.WinFormsUI.Docking;
using mRemoteNG.Resources.Language;
using System.Runtime.Versioning;

namespace mRemoteNG.UI.Tabs
{
    [SupportedOSPlatform("windows")]
    public partial class ConnectionTab : DockContent
    {
        // P/Invoke for enumerating child windows to find the embedded PuTTY hwnd
        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_CHILD = 5;

        private ToolStrip _toolbar;
        private ToolStripButton _btnPaste;
        private ToolStripButton _btnFreezeAndCopy;
        private ToolStripLabel _lblOrchestratorStatus;
        private System.Windows.Forms.Timer _orchestratorTimer;
        private int _orchestratorTickCount;

        // NickHQ session ID for the connection hosted in this tab (null = not registered)
        private string? _orchestratorSessionId;

        /// <summary>
        ///Silent close ignores the popup asking for confirmation
        /// </summary>
        public bool silentClose { get; set; }

        /// <summary>
        /// Protocol close ignores the interface controller cleanup and the user confirmation dialog
        /// </summary>
        public bool protocolClose { get; set; }

        public ConnectionTab()
        {
            InitializeComponent();
            GotFocus += ConnectionTab_GotFocus;
            InitializeToolbar();

            // Enable drag-and-drop file uploads onto the tab
            AllowDrop = true;
            DragEnter += ConnectionTab_DragEnter;
            DragDrop += ConnectionTab_DragDrop;
        }

        private void InitializeToolbar()
        {
            _btnPaste = new ToolStripButton
            {
                Text = "\U0001F4CB Paste",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Paste clipboard text into the session"
            };
            _btnPaste.Click += BtnPaste_Click;

            _btnFreezeAndCopy = new ToolStripButton
            {
                Text = "❄ Freeze & Copy",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Open a copyable snapshot of the session log"
            };
            _btnFreezeAndCopy.Click += BtnFreezeAndCopy_Click;

            _lblOrchestratorStatus = new ToolStripLabel
            {
                Text = "⚙",
                Visible = false,
                Alignment = ToolStripItemAlignment.Right,
                ToolTipText = "Orchestrator status"
            };

            _toolbar = new ToolStrip
            {
                Dock = DockStyle.Top,
                Height = 24,
                GripStyle = ToolStripGripStyle.Hidden,
                RenderMode = ToolStripRenderMode.System
            };
            _toolbar.Items.Add(_btnPaste);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_btnFreezeAndCopy);
            _toolbar.Items.Add(_lblOrchestratorStatus);

            Controls.Add(_toolbar);

            // Timer to poll orchestrator status once per second
            _orchestratorTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _orchestratorTimer.Tick += OrchestratorTimer_Tick;
            _orchestratorTimer.Start();

            // Build the tab context menu for orchestration actions
            InitializeOrchestratorContextMenu();
        }

        private void InitializeOrchestratorContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += OrchestratorContextMenu_Opening;

            // Separator before orchestrator items
            var sep = new ToolStripSeparator();
            menu.Items.Add(sep);

            var menuOrchestrate = new ToolStripMenuItem("Orchestrate this session...");
            menuOrchestrate.Click += MenuOrchestrate_Click;
            menu.Items.Add(menuOrchestrate);

            var menuWhatDoing = new ToolStripMenuItem("What are you doing?");
            menuWhatDoing.Click += MenuWhatDoing_Click;
            menu.Items.Add(menuWhatDoing);

            var menuStop = new ToolStripMenuItem("Stop Orchestration");
            menuStop.Click += MenuStopOrchestration_Click;
            menu.Items.Add(menuStop);

            TabPageContextMenuStrip = menu;
        }

        private void OrchestratorContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (sender is not ContextMenuStrip menu) return;

            // Resolve session ID fresh each time the menu opens (may have been set after construction)
            EnsureOrchestratorSessionId();

            bool isPutty = (Tag as InterfaceControl)?.Protocol is PuttyBase;
            bool isOrchestrated = !string.IsNullOrEmpty(_orchestratorSessionId)
                                  && OrchestratorRegistry.IsOrchestrated(_orchestratorSessionId);

            // Items: [0] separator, [1] orchestrate, [2] what are you doing, [3] stop
            menu.Items[0].Visible = isPutty;          // separator — only show if any item below will show
            menu.Items[1].Visible = isPutty;           // "Orchestrate this session..."
            menu.Items[2].Visible = isOrchestrated;   // "What are you doing?"
            menu.Items[3].Visible = isOrchestrated;   // "Stop Orchestration"

            // Hide separator if no items are visible
            menu.Items[0].Visible = isPutty || isOrchestrated;
        }

        private void MenuOrchestrate_Click(object sender, EventArgs e)
        {
            EnsureOrchestratorSessionId();
            if (string.IsNullOrEmpty(_orchestratorSessionId)) return;

            InterfaceControl? ifc = Tag as InterfaceControl;
            string label = ifc?.Info?.Hostname ?? TabText ?? "session";

            using var dlg = new OrchestrateSessionDialog(_orchestratorSessionId, label);
            dlg.ShowDialog(this);
        }

        private void MenuWhatDoing_Click(object sender, EventArgs e)
        {
            EnsureOrchestratorSessionId();
            if (string.IsNullOrEmpty(_orchestratorSessionId)) return;

            var engine = OrchestratorRegistry.Get(_orchestratorSessionId);
            if (engine == null) return;

            new OrchestratorStatusForm(engine).Show(this);
        }

        private void MenuStopOrchestration_Click(object sender, EventArgs e)
        {
            EnsureOrchestratorSessionId();
            if (string.IsNullOrEmpty(_orchestratorSessionId)) return;

            OrchestratorRegistry.Stop(_orchestratorSessionId);
        }

        private void EnsureOrchestratorSessionId()
        {
            if (_orchestratorSessionId != null) return;
            if ((Tag as InterfaceControl)?.Protocol is PuttyBase putty)
                _orchestratorSessionId = putty.NickHqSessionId;
        }

        private void OrchestratorTimer_Tick(object sender, EventArgs e)
        {
            EnsureOrchestratorSessionId();

            if (string.IsNullOrEmpty(_orchestratorSessionId))
            {
                _lblOrchestratorStatus.Visible = false;
                return;
            }

            var engine = OrchestratorRegistry.Get(_orchestratorSessionId);
            if (engine == null)
            {
                _lblOrchestratorStatus.Visible = false;
                return;
            }

            _orchestratorTickCount++;
            bool altTick = (_orchestratorTickCount % 2) == 0;

            switch (engine.Status)
            {
                case OrchestratorStatus.Running:
                    _lblOrchestratorStatus.Visible = true;
                    _lblOrchestratorStatus.Text = altTick ? "⚙" : "◌";
                    _lblOrchestratorStatus.ForeColor = Color.Orange;
                    break;
                case OrchestratorStatus.Waiting:
                    _lblOrchestratorStatus.Visible = true;
                    _lblOrchestratorStatus.Text = altTick ? "⚙" : "◌";
                    _lblOrchestratorStatus.ForeColor = Color.Gold;
                    break;
                case OrchestratorStatus.Blocked:
                    _lblOrchestratorStatus.Visible = true;
                    _lblOrchestratorStatus.Text = "⚠";
                    _lblOrchestratorStatus.ForeColor = Color.Red;
                    break;
                case OrchestratorStatus.Done:
                    _lblOrchestratorStatus.Visible = true;
                    _lblOrchestratorStatus.Text = "✓";
                    _lblOrchestratorStatus.ForeColor = Color.Green;
                    break;
                default:
                    // Stopped / Error — hide indicator
                    _lblOrchestratorStatus.Visible = false;
                    break;
            }
        }

        // Find the first child hwnd of the given panel handle (the embedded PuTTY window)
        private static IntPtr FindFirstChildHwnd(IntPtr parentHwnd)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parentHwnd, (hwnd, lParam) =>
            {
                found = hwnd;
                return false; // stop after first
            }, IntPtr.Zero);
            return found;
        }

        private void BtnPaste_Click(object sender, EventArgs e)
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text))
                return;

            // Try to find the embedded PuTTY child window to send WM_CHAR messages
            InterfaceControl ifc = Tag as InterfaceControl;
            IntPtr puttyHwnd = IntPtr.Zero;

            if (ifc != null)
                puttyHwnd = FindFirstChildHwnd(ifc.Handle);

            if (puttyHwnd != IntPtr.Zero)
            {
                foreach (char c in text)
                {
                    NativeMethods.PostMessage(puttyHwnd, NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                }
            }
            else
            {
                // Fallback: use SendKeys if we cannot find the child hwnd
                if (ifc != null)
                    NativeMethods.SetForegroundWindow(ifc.Handle);
                SendKeys.Send(text.Replace("{", "{{").Replace("}", "}}").Replace("(", "(").Replace(")", ")").Replace("+", "{+}").Replace("^", "{^}").Replace("%", "{%}").Replace("~", "{~}").Replace("[", "{[}").Replace("]", "{]}"));
            }
        }

        private void BtnFreezeAndCopy_Click(object sender, EventArgs e)
        {
            InterfaceControl ifc = Tag as InterfaceControl;
            string logPath = null;
            string hostname = "session";

            if (ifc?.Protocol is PuttyBase putty)
            {
                logPath = putty.SessionLogPath;
                hostname = ifc.Info?.Hostname ?? "session";
            }

            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                MessageBox.Show(
                    "No session log is available for this connection.\n\nSession logging is automatically enabled for SSH and Telnet connections — the log file may not exist yet if the session just started.",
                    "Session Log Unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                // Read last 500 lines from the log
                string[] lines = File.ReadAllLines(logPath);
                int skip = Math.Max(0, lines.Length - 500);
                string snippet = string.Join(Environment.NewLine, lines, skip, lines.Length - skip);

                Form frm = new Form
                {
                    Text = $"Copy from {hostname} — select text, Ctrl+C",
                    FormBorderStyle = FormBorderStyle.SizableToolWindow,
                    StartPosition = FormStartPosition.CenterParent,
                    Width = 800,
                    Height = 500,
                    ShowInTaskbar = false
                };

                RichTextBox rtb = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    Text = snippet,
                    ReadOnly = false,
                    ScrollBars = RichTextBoxScrollBars.Both,
                    Font = new System.Drawing.Font("Consolas", 9F),
                    WordWrap = false
                };

                frm.Controls.Add(rtb);
                frm.Show(this);

                // Scroll to bottom so the most recent output is visible
                rtb.SelectionStart = rtb.Text.Length;
                rtb.ScrollToCaret();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not open session log: " + ex.Message,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            // Push any InterfaceControl children below the toolbar so PuTTY
            // doesn't render under it.
            if (_toolbar == null) return;
            int toolbarBottom = _toolbar.Visible ? _toolbar.Bottom : 0;
            foreach (System.Windows.Forms.Control ctrl in Controls)
            {
                if (ctrl is InterfaceControl ifc)
                {
                    ifc.Location = new System.Drawing.Point(0, toolbarBottom);
                    ifc.Size = new System.Drawing.Size(ClientSize.Width, Math.Max(0, ClientSize.Height - toolbarBottom));
                }
            }
        }

        private void ConnectionTab_GotFocus(object sender, EventArgs e)
        {
            TabHelper.Instance.CurrentTab = this;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop and dispose the orchestrator poll timer to avoid post-close ticks
            if (_orchestratorTimer != null)
            {
                _orchestratorTimer.Stop();
                _orchestratorTimer.Dispose();
                _orchestratorTimer = null;
            }

            if (!protocolClose)
            {
                if (!silentClose)
                {
                    if (Settings.Default.ConfirmCloseConnection == (int)ConfirmCloseEnum.All)
                    {
                        DialogResult result = CTaskDialog.MessageBox(this, GeneralAppInfo.ProductName,
                                                            string
                                                                .Format(Language.ConfirmCloseConnectionPanelMainInstruction,
                                                                        TabText), "", "", "",
                                                            Language.CheckboxDoNotShowThisMessageAgain,
                                                            ETaskDialogButtons.YesNo, ESysIcons.Question,
                                                            ESysIcons.Question);
                        if (CTaskDialog.VerificationChecked)
                        {
                            Settings.Default.ConfirmCloseConnection = (int)ConfirmCloseEnum.Never;
                            Settings.Default.Save();
                        }

                        if (result == DialogResult.No)
                        {
                            e.Cancel = true;
                        }
                        else
                        {
                            ((InterfaceControl)Tag)?.Protocol.Close();
                        }
                    }
                    else
                    {
                        // close without the confirmation prompt...
                        ((InterfaceControl)Tag)?.Protocol.Close();
                    }
                }
                else
                {
                    ((InterfaceControl)Tag)?.Protocol.Close();
                }
            }

            base.OnFormClosing(e);
        }


        #region Drag and Drop Upload

        private void ConnectionTab_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void ConnectionTab_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            // Get the ConnectionInfo for this tab
            InterfaceControl ifc = Tag as InterfaceControl;
            ConnectionInfo info = ifc?.Info;
            if (info == null)
            {
                MessageBox.Show(
                    "No active connection associated with this tab.",
                    "Upload",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string defaultRemotePath = $"/home/{info.Username}/";
            string hostname = info.Hostname;

            // Confirmation dialog with editable remote path
            Form dlg = new Form
            {
                Text = $"Upload {files.Length} file(s) to {hostname}?",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                Width = 460,
                Height = 150,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false
            };

            Label lbl = new Label
            {
                Text = "Remote destination path:",
                Left = 12,
                Top = 16,
                Width = 160,
                AutoSize = true
            };

            TextBox txtRemote = new TextBox
            {
                Text = defaultRemotePath,
                Left = 12,
                Top = 38,
                Width = 420
            };

            Button btnOk = new Button
            {
                Text = "Upload",
                DialogResult = DialogResult.OK,
                Left = 262,
                Top = 72,
                Width = 80
            };

            Button btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left = 352,
                Top = 72,
                Width = 80
            };

            dlg.Controls.AddRange(new System.Windows.Forms.Control[] { lbl, txtRemote, btnOk, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string remotePath = txtRemote.Text.TrimEnd('/') + "/";

            // Show transfer window and queue uploads
            AppWindows.Show(WindowType.ScpTransfer);

            foreach (string localFile in files)
            {
                if (!File.Exists(localFile)) continue;

                string filename = Path.GetFileName(localFile);
                Guid transferId = AppWindows.ScpTransferForm.AddTransfer("Upload", filename, hostname);
                _ = HostCallExecutor.ExecuteUploadAsync(localFile, remotePath, info, transferId);
            }
        }

        #endregion

        #region HelperFunctions

        public void RefreshInterfaceController()
        {
            try
            {
                InterfaceControl interfaceControl = Tag as InterfaceControl;
                if (interfaceControl?.Info.Protocol == ProtocolType.VNC)
                    ((ProtocolVNC)interfaceControl.Protocol).RefreshScreen();
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage("RefreshIC (UI.Window.Connection) failed", ex);
            }
        }

        #endregion
    }
}