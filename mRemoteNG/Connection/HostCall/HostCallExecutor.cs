using mRemoteNG.App;
using mRemoteNG.Messages;
using Renci.SshNet;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace mRemoteNG.Connection.HostCall
{
    /// <summary>
    /// Executes host-call actions (SCP file download / upload) against a remote connection.
    /// Progress is reported via the <see cref="TransferProgress"/> event so the
    /// <see cref="mRemoteNG.UI.Window.ScpTransferWindow"/> can update its ListView row.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class HostCallExecutor
    {
        /// <summary>
        /// Raised on each SCP progress tick. Arguments: (transferId, bytesTransferred, totalBytes).
        /// </summary>
        public static event Action<Guid, long, long>? TransferProgress;

        /// <summary>
        /// Raised when a transfer completes (success or failure).
        /// Arguments: (transferId, success, errorMessage).
        /// </summary>
        public static event Action<Guid, bool, string?>? TransferComplete;

        /// <summary>
        /// Local directory where downloaded files are placed.
        /// Created on first use if it does not already exist.
        /// </summary>
        public static readonly string DefaultDownloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "mRemoteNG-transfers");

        /// <summary>
        /// Executes a single host-call asynchronously.
        /// If <paramref name="transferId"/> is provided (non-empty), that ID is used for
        /// progress/completion events so the caller can correlate with a pre-registered
        /// ScpTransferWindow row. Otherwise a new ID is generated and returned.
        /// </summary>
        public static async Task<Guid> ExecuteAsync(HostCall call, ConnectionInfo connection,
            Guid transferId = default)
        {
            if (transferId == default)
                transferId = Guid.NewGuid();

            await Task.Run(() =>
            {
                switch (call.Action.ToLowerInvariant())
                {
                    case "file-download":
                        RunDownload(transferId, call.Payload, connection);
                        break;

                    case "file-upload":
                        RunUpload(transferId, call.Payload, connection);
                        break;

                    default:
                        Runtime.MessageCollector.AddMessage(
                            MessageClass.WarningMsg,
                            $"HostCallExecutor: unknown action '{call.Action}' (payload: {call.Payload})",
                            onlyLog: true);
                        TransferComplete?.Invoke(transferId, false, $"Unknown action: {call.Action}");
                        break;
                }
            });

            return transferId;
        }

        /// <summary>
        /// Convenience overload for drag-and-drop uploads where the caller supplies
        /// a specific remote destination directory and an optional pre-registered transfer ID.
        /// </summary>
        public static async Task<Guid> ExecuteUploadAsync(string localPath, string remoteDir,
            ConnectionInfo connection, Guid transferId = default)
        {
            if (transferId == default)
                transferId = Guid.NewGuid();
            await Task.Run(() => RunUploadToPath(transferId, localPath, remoteDir, connection));
            return transferId;
        }

        // -------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------

        private static void RunDownload(Guid transferId, string remotePath, ConnectionInfo connection)
        {
            string hostname = connection.Hostname ?? string.Empty;
            string username = connection.Username ?? string.Empty;
            string password = connection.Password ?? string.Empty;
            int port = connection.Port > 0 ? connection.Port : 22;

            try
            {
                Directory.CreateDirectory(DefaultDownloadDir);

                using ScpClient scp = new ScpClient(hostname, port, username, password);

                scp.Downloading += (sender, e) =>
                    TransferProgress?.Invoke(transferId, e.Downloaded, e.Size);

                scp.Connect();

                // Determine whether the remote path looks like a directory (ends with / or no extension)
                bool looksLikeDirectory = remotePath.EndsWith("/") || remotePath.EndsWith("\\")
                    || !Path.HasExtension(remotePath);

                if (looksLikeDirectory)
                {
                    string dirName = remotePath.TrimEnd('/', '\\');
                    dirName = dirName.Contains('/') ? dirName.Substring(dirName.LastIndexOf('/') + 1) : dirName;
                    DirectoryInfo localDir = new DirectoryInfo(Path.Combine(DefaultDownloadDir, dirName));
                    localDir.Create();
                    scp.Download(remotePath, localDir);
                }
                else
                {
                    string fileName = remotePath.Contains('/') ? remotePath.Substring(remotePath.LastIndexOf('/') + 1) : remotePath;
                    FileInfo localFile = new FileInfo(Path.Combine(DefaultDownloadDir, fileName));
                    scp.Download(remotePath, localFile);
                }

                scp.Disconnect();

                Runtime.MessageCollector.AddMessage(
                    MessageClass.InformationMsg,
                    $"HostCall download complete: {remotePath} → {DefaultDownloadDir}",
                    onlyLog: true);

                TransferComplete?.Invoke(transferId, true, null);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"HostCall download failed: {remotePath}", ex, MessageClass.ErrorMsg, false);

                TransferComplete?.Invoke(transferId, false, ex.Message);
            }
        }

        private static void RunUpload(Guid transferId, string localPath, ConnectionInfo connection)
        {
            string username = connection.Username ?? string.Empty;
            string remoteDest = $"/home/{username}/";
            RunUploadToPath(transferId, localPath, remoteDest, connection);
        }

        private static void RunUploadToPath(Guid transferId, string localPath, string remoteDir, ConnectionInfo connection)
        {
            string hostname = connection.Hostname ?? string.Empty;
            string username = connection.Username ?? string.Empty;
            string password = connection.Password ?? string.Empty;
            int port = connection.Port > 0 ? connection.Port : 22;

            // Ensure remoteDir ends with /
            string remoteDest = remoteDir.TrimEnd('/') + "/";

            try
            {
                FileInfo localFile = new FileInfo(localPath);
                if (!localFile.Exists)
                    throw new FileNotFoundException($"Local file not found: {localPath}");

                using ScpClient scp = new ScpClient(hostname, port, username, password);

                scp.Uploading += (sender, e) =>
                    TransferProgress?.Invoke(transferId, e.Uploaded, e.Size);

                scp.Connect();
                scp.Upload(localFile, remoteDest + localFile.Name);
                scp.Disconnect();

                Runtime.MessageCollector.AddMessage(
                    MessageClass.InformationMsg,
                    $"HostCall upload complete: {localPath} → {hostname}:{remoteDest}",
                    onlyLog: true);

                TransferComplete?.Invoke(transferId, true, null);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"HostCall upload failed: {localPath}", ex, MessageClass.ErrorMsg, false);

                TransferComplete?.Invoke(transferId, false, ex.Message);
            }
        }
    }
}
