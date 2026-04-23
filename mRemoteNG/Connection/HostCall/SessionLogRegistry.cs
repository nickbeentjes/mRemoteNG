using System.Collections.Concurrent;

namespace mRemoteNG.Connection.HostCall
{
    /// <summary>
    /// Maps session log file paths to the <see cref="ConnectionInfo"/> that produced them.
    /// PuttyBase registers a path here immediately after launching PuTTY with -sessionlog.
    /// SessionLogWindow watches registered paths for host-call tags.
    /// </summary>
    public static class SessionLogRegistry
    {
        private static readonly ConcurrentDictionary<string, ConnectionInfo> _registry =
            new ConcurrentDictionary<string, ConnectionInfo>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Associates a log file path with its originating connection.
        /// </summary>
        public static void Register(string logPath, ConnectionInfo info)
        {
            if (!string.IsNullOrEmpty(logPath) && info != null)
                _registry[logPath] = info;
        }

        /// <summary>
        /// Removes the registration for a log file path (called when the session ends).
        /// </summary>
        public static void Unregister(string logPath)
        {
            if (!string.IsNullOrEmpty(logPath))
                _registry.TryRemove(logPath, out _);
        }

        /// <summary>
        /// Tries to find the <see cref="ConnectionInfo"/> for the given log path.
        /// Returns <c>null</c> if the path is not registered.
        /// </summary>
        public static ConnectionInfo? TryGet(string logPath)
        {
            if (string.IsNullOrEmpty(logPath))
                return null;

            return _registry.TryGetValue(logPath, out ConnectionInfo? info) ? info : null;
        }
    }
}
