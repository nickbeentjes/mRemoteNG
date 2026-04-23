using mRemoteNG.Connection.HostCall;
using mRemoteNG.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace mRemoteNG.UI.Window
{
    /// <summary>
    /// Dockable panel that displays in-progress and completed SCP transfers
    /// triggered by host-call tags parsed from Claude responses or session logs.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ScpTransferWindow : BaseWindow
    {
        private ListView _lstTransfers;
        private ColumnHeader _colDirection;
        private ColumnHeader _colFilename;
        private ColumnHeader _colHost;
        private ColumnHeader _colStatus;
        private ColumnHeader _colProgress;

        // Maps transfer IDs to their ListViewItem for O(1) updates
        private readonly Dictionary<Guid, ListViewItem> _itemMap = new Dictionary<Guid, ListViewItem>();

        public ScpTransferWindow() : this(new DockContent())
        {
        }

        public ScpTransferWindow(DockContent panel)
        {
            WindowType = WindowType.ScpTransfer;
            DockPnl = panel;
            Text = "Transfers";
            TabText = "Transfers";
            HideOnClose = true;

            InitializeControls();
            SubscribeToExecutor();
        }

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void InitializeControls()
        {
            _colDirection = new ColumnHeader { Text = "Direction", Width = 80 };
            _colFilename = new ColumnHeader { Text = "Filename", Width = 200 };
            _colHost = new ColumnHeader { Text = "Host", Width = 140 };
            _colStatus = new ColumnHeader { Text = "Status", Width = 100 };
            _colProgress = new ColumnHeader { Text = "Progress", Width = 80 };

            _lstTransfers = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 8.25F)
            };
            _lstTransfers.Columns.AddRange(new[]
            {
                _colDirection, _colFilename, _colHost, _colStatus, _colProgress
            });

            Controls.Add(_lstTransfers);
        }

        // ------------------------------------------------------------------
        // Executor event wiring
        // ------------------------------------------------------------------

        private void SubscribeToExecutor()
        {
            HostCallExecutor.TransferProgress += OnTransferProgress;
            HostCallExecutor.TransferComplete += OnTransferComplete;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                HostCallExecutor.TransferProgress -= OnTransferProgress;
                HostCallExecutor.TransferComplete -= OnTransferComplete;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // Public API — called by ClaudeChatWindow / SessionLogWindow before kicking off a transfer
        // ------------------------------------------------------------------

        /// <summary>
        /// Adds a "Queued" row for the given transfer and returns the transfer ID.
        /// Call this before invoking <see cref="HostCallExecutor.ExecuteAsync"/> so the row
        /// exists before any progress events fire.
        /// </summary>
        public Guid AddTransfer(string direction, string filename, string host)
        {
            Guid id = Guid.NewGuid();

            ListViewItem item = new ListViewItem(direction);
            item.SubItems.Add(filename);
            item.SubItems.Add(host);
            item.SubItems.Add("Queued");
            item.SubItems.Add("—");
            item.Tag = id;

            InvokeOnUI(() =>
            {
                _lstTransfers.Items.Add(item);
                _itemMap[id] = item;
            });

            return id;
        }

        // ------------------------------------------------------------------
        // Progress / completion callbacks
        // ------------------------------------------------------------------

        private void OnTransferProgress(Guid id, long transferred, long total)
        {
            InvokeOnUI(() =>
            {
                if (!_itemMap.TryGetValue(id, out ListViewItem item)) return;

                item.SubItems[3].Text = "Transferring";

                if (total > 0)
                {
                    int pct = (int)(transferred * 100L / total);
                    item.SubItems[4].Text = $"{pct}%";
                }
                else
                {
                    item.SubItems[4].Text = $"{transferred / 1024} KB";
                }
            });
        }

        private void OnTransferComplete(Guid id, bool success, string? errorMessage)
        {
            InvokeOnUI(() =>
            {
                if (!_itemMap.TryGetValue(id, out ListViewItem item)) return;

                if (success)
                {
                    item.SubItems[3].Text = "Done";
                    item.SubItems[4].Text = "100%";
                    item.ForeColor = Color.DarkGreen;
                }
                else
                {
                    item.SubItems[3].Text = "Failed";
                    item.SubItems[4].Text = errorMessage ?? "Error";
                    item.ForeColor = Color.DarkRed;
                }
            });
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void InvokeOnUI(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
    }
}
