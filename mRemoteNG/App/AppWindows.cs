#region Usings
using System;
using System.Runtime.Versioning;
using mRemoteNG.Resources.Language;
using mRemoteNG.UI;
using mRemoteNG.UI.Forms;
using mRemoteNG.UI.Window;
#endregion

namespace mRemoteNG.App
{
    [SupportedOSPlatform("windows")]
    public static class AppWindows
    {
        private static ActiveDirectoryImportWindow _adimportForm;
        private static ExternalToolsWindow _externalappsForm;
        private static PortScanWindow _portscanForm;
        private static UltraVNCWindow _ultravncscForm;
        private static ConnectionTreeWindow _treeForm;
        private static ClaudeChatWindow _claudeChatForm;
        private static SessionLogWindow _sessionLogForm;
        private static ScpTransferWindow _scpTransferForm;
        private static NotificationsWindow _notificationsForm;

        internal static ConnectionTreeWindow TreeForm
        {
            get => _treeForm ?? (_treeForm = new ConnectionTreeWindow());
            set => _treeForm = value;
        }

        internal static ClaudeChatWindow ClaudeChatForm
        {
            get => _claudeChatForm ?? (_claudeChatForm = new ClaudeChatWindow());
            set => _claudeChatForm = value;
        }

        internal static SessionLogWindow SessionLogForm
        {
            get => _sessionLogForm ?? (_sessionLogForm = new SessionLogWindow());
            set => _sessionLogForm = value;
        }

        internal static ScpTransferWindow ScpTransferForm
        {
            get => _scpTransferForm ?? (_scpTransferForm = new ScpTransferWindow());
            set => _scpTransferForm = value;
        }

        internal static NotificationsWindow NotificationsForm
        {
            get => _notificationsForm ?? (_notificationsForm = new NotificationsWindow());
            set => _notificationsForm = value;
        }

        internal static ConfigWindow ConfigForm { get; set; } = new ConfigWindow();
        internal static ErrorAndInfoWindow ErrorsForm { get; set; } = new ErrorAndInfoWindow();
        internal static UpdateWindow UpdateForm { get; set; } = new UpdateWindow();
        internal static SSHTransferWindow SshtransferForm { get; private set; } = new SSHTransferWindow();
        internal static OptionsWindow OptionsFormWindow { get; private set; }


        public static void Show(WindowType windowType)
        {
            try
            {
                WeifenLuo.WinFormsUI.Docking.DockPanel dockPanel = FrmMain.Default.pnlDock;
                // ReSharper disable once SwitchStatementMissingSomeCases
                switch (windowType)
                {
                    case WindowType.ActiveDirectoryImport:
                        if (_adimportForm == null || _adimportForm.IsDisposed)
                            _adimportForm = new ActiveDirectoryImportWindow();
                        _adimportForm.Show(dockPanel);
                        break;
                    case WindowType.Options:
                        if (OptionsFormWindow == null || OptionsFormWindow.IsDisposed)
                            OptionsFormWindow = new OptionsWindow();
                        OptionsFormWindow.SetActivatedPage(Language.StartupExit);
                        // Reload controls from stored settings before every show so that any
                        // edits left over from a previous hide (Tab-X without Apply/OK) are
                        // discarded.  Safe on first call — no-op until FrmOptions is embedded.
                        OptionsFormWindow.RefreshSettings();
                        OptionsFormWindow.Show(dockPanel);
                        break;
                    case WindowType.SSHTransfer:
                        if (SshtransferForm == null || SshtransferForm.IsDisposed)
                            SshtransferForm = new SSHTransferWindow();
                        SshtransferForm.Show(dockPanel);
                        break;
                    case WindowType.Update:
                        if (UpdateForm == null || UpdateForm.IsDisposed)
                            UpdateForm = new UpdateWindow();
                        UpdateForm.Show(dockPanel);
                        break;
                    case WindowType.ExternalApps:
                        if (_externalappsForm == null || _externalappsForm.IsDisposed)
                            _externalappsForm = new ExternalToolsWindow();
                        _externalappsForm.Show(dockPanel);
                        break;
                    case WindowType.PortScan:
                        _portscanForm = new PortScanWindow();
                        _portscanForm.Show(dockPanel);
                        break;
                    case WindowType.UltraVNCSC:
                        if (_ultravncscForm == null || _ultravncscForm.IsDisposed)
                            _ultravncscForm = new UltraVNCWindow();
                        _ultravncscForm.Show(dockPanel);
                        break;
                    case WindowType.SessionLog:
                        if (_sessionLogForm == null || _sessionLogForm.IsDisposed)
                            _sessionLogForm = new SessionLogWindow();
                        _sessionLogForm.Show(dockPanel);
                        break;
                    case WindowType.ScpTransfer:
                        if (_scpTransferForm == null || _scpTransferForm.IsDisposed)
                            _scpTransferForm = new ScpTransferWindow();
                        _scpTransferForm.Show(dockPanel, WeifenLuo.WinFormsUI.Docking.DockState.DockBottom);
                        break;
                    case WindowType.NotificationsWindow:
                        if (_notificationsForm == null || _notificationsForm.IsDisposed)
                            _notificationsForm = new NotificationsWindow();
                        _notificationsForm.Show(dockPanel, WeifenLuo.WinFormsUI.Docking.DockState.DockBottom);
                        break;
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("App.Runtime.Windows.Show() failed.", ex);
            }
        }
    }
}