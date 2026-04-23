using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using System.Runtime.Versioning;
using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Connection.HostCall;
using mRemoteNG.Messages;
using mRemoteNG.UI;

namespace mRemoteNG.UI.Window
{
    [SupportedOSPlatform("windows")]
    public class SessionLogWindow : BaseWindow
    {
        private ListBox _lstSessions;
        private RichTextBox _txtLog;
        private Panel _pnlLeft;
        private Panel _pnlRight;
        private Panel _pnlSearch;
        private Button _btnRefresh;
        private TextBox _txtSearch;
        private Button _btnFind;
        private SplitContainer _splitContainer;

        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "mRemoteNG", "SessionLogs");

        // FileSystemWatcher support for host-call detection
        private FileSystemWatcher _watcher;
        private string _watchedLogPath;
        private long _watchedReadPosition;  // bytes already processed for host-calls

        public SessionLogWindow() : this(new DockContent())
        {
        }

        public SessionLogWindow(DockContent panel)
        {
            WindowType = WindowType.SessionLog;
            DockPnl = panel;
            Text = "Session Logs";
            TabText = "Session Logs";
            HideOnClose = true;
            InitializeControls();
        }

        private void InitializeControls()
        {
            // --- Left panel: refresh button + session list ---
            _btnRefresh = new Button
            {
                Text = "Refresh",
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font("Segoe UI", 8.25F)
            };
            _btnRefresh.Click += BtnRefresh_Click;

            _lstSessions = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.25F),
                IntegralHeight = false
            };
            _lstSessions.SelectedIndexChanged += LstSessions_SelectedIndexChanged;

            _pnlLeft = new Panel { Dock = DockStyle.Fill };
            _pnlLeft.Controls.Add(_lstSessions);
            _pnlLeft.Controls.Add(_btnRefresh);

            // --- Right panel: log viewer + search strip ---
            _txtLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Both,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
                BackColor = SystemColors.Window
            };

            _txtSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.25F)
            };
            _txtSearch.KeyDown += TxtSearch_KeyDown;

            _btnFind = new Button
            {
                Text = "Find",
                Dock = DockStyle.Right,
                Width = 50,
                Font = new Font("Segoe UI", 8.25F)
            };
            _btnFind.Click += BtnFind_Click;

            _pnlSearch = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 26
            };
            _pnlSearch.Controls.Add(_txtSearch);
            _pnlSearch.Controls.Add(_btnFind);

            _pnlRight = new Panel { Dock = DockStyle.Fill };
            _pnlRight.Controls.Add(_txtLog);
            _pnlRight.Controls.Add(_pnlSearch);

            // --- SplitContainer: left ~30%, right ~70% ---
            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 4
            };
            _splitContainer.Panel1.Controls.Add(_pnlLeft);
            _splitContainer.Panel2.Controls.Add(_pnlRight);

            Controls.Add(_splitContainer);

            // Set splitter position after handle creation
            Load += SessionLogWindow_Load;
            VisibleChanged += SessionLogWindow_VisibleChanged;
        }

        private void SessionLogWindow_Load(object sender, EventArgs e)
        {
            // Position splitter at ~30% of width
            if (_splitContainer.Width > 0)
                _splitContainer.SplitterDistance = Math.Max(100, _splitContainer.Width * 3 / 10);
            RefreshSessionList();
        }

        private void SessionLogWindow_VisibleChanged(object sender, EventArgs e)
        {
            if (Visible)
                RefreshSessionList();
        }

        private void RefreshSessionList()
        {
            _lstSessions.Items.Clear();

            if (!Directory.Exists(LogDir))
                return;

            try
            {
                string[] files = Directory.GetFiles(LogDir, "*.log")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .ToArray();

                foreach (string file in files)
                    _lstSessions.Items.Add(Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                _lstSessions.Items.Add("Error: " + ex.Message);
            }
        }

        private void LstSessions_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_lstSessions.SelectedItem == null)
                return;

            string fileName = _lstSessions.SelectedItem.ToString();
            string fullPath = Path.Combine(LogDir, fileName);

            try
            {
                _txtLog.Text = File.ReadAllText(fullPath);
                _txtLog.SelectionStart = 0;
                _txtLog.ScrollToCaret();
            }
            catch (Exception ex)
            {
                _txtLog.Text = "Could not read log file: " + ex.Message;
            }

            StartWatchingLog(fullPath);
        }

        // ------------------------------------------------------------------
        // FileSystemWatcher — detect host-call tags appended to the active log
        // ------------------------------------------------------------------

        private void StartWatchingLog(string logPath)
        {
            // Stop any previous watcher
            StopWatchingLog();

            if (!File.Exists(logPath)) return;

            _watchedLogPath = logPath;

            // Start reading from the current end of file so we only process new content
            try
            {
                _watchedReadPosition = new FileInfo(logPath).Length;
            }
            catch
            {
                _watchedReadPosition = 0;
            }

            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(logPath), Path.GetFileName(logPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += LogFile_Changed;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                    "SessionLogWindow: could not watch log file: " + ex.Message, onlyLog: true);
            }
        }

        private void StopWatchingLog()
        {
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= LogFile_Changed;
            _watcher.Dispose();
            _watcher = null;
            _watchedLogPath = null;
            _watchedReadPosition = 0;
        }

        private void LogFile_Changed(object sender, FileSystemEventArgs e)
        {
            string logPath = _watchedLogPath;
            if (string.IsNullOrEmpty(logPath)) return;

            // Read only the newly-appended bytes
            string newContent;
            try
            {
                using FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= _watchedReadPosition) return;
                fs.Seek(_watchedReadPosition, SeekOrigin.Begin);
                using StreamReader sr = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                newContent = sr.ReadToEnd();
                _watchedReadPosition = fs.Position;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(newContent)) return;

            // Look for host-call tags in the new content
            var calls = new List<HostCall>(HostCallParser.Parse(newContent));
            if (calls.Count == 0) return;

            ConnectionInfo connectionInfo = SessionLogRegistry.TryGet(logPath);
            if (connectionInfo == null) return;

            // Dispatch to UI thread to show the transfer window and queue rows
            BeginInvoke(new Action(() =>
            {
                AppWindows.Show(WindowType.ScpTransfer);

                foreach (HostCall call in calls)
                {
                    string filename = Path.GetFileName(call.Payload.TrimEnd('/', '\\'));
                    if (string.IsNullOrEmpty(filename)) filename = call.Payload;
                    string direction = call.Action == "file-download" ? "Download" : "Upload";

                    Guid transferId = AppWindows.ScpTransferForm.AddTransfer(direction, filename, connectionInfo.Hostname);
                    _ = HostCallExecutor.ExecuteAsync(call, connectionInfo, transferId);
                }
            }));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                StopWatchingLog();
            base.Dispose(disposing);
        }

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            RefreshSessionList();
        }

        private void BtnFind_Click(object sender, EventArgs e)
        {
            FindInLog();
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                FindInLog();
            }
        }

        private void FindInLog()
        {
            string term = _txtSearch.Text;
            if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(_txtLog.Text))
                return;

            // Clear any previous highlights by resetting selection
            _txtLog.SelectAll();
            _txtLog.SelectionBackColor = _txtLog.BackColor;
            _txtLog.DeselectAll();

            int firstMatch = -1;
            int searchStart = 0;
            string text = _txtLog.Text;
            StringComparison comparison = StringComparison.OrdinalIgnoreCase;

            while (true)
            {
                int idx = text.IndexOf(term, searchStart, comparison);
                if (idx < 0)
                    break;

                _txtLog.Select(idx, term.Length);
                _txtLog.SelectionBackColor = Color.Yellow;

                if (firstMatch < 0)
                    firstMatch = idx;

                searchStart = idx + term.Length;
            }

            // Scroll to first match
            if (firstMatch >= 0)
            {
                _txtLog.Select(firstMatch, term.Length);
                _txtLog.ScrollToCaret();
            }
        }
    }
}
