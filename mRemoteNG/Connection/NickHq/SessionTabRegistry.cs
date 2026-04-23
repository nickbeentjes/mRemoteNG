using System.Collections.Concurrent;
using System.Runtime.Versioning;
using mRemoteNG.UI.Tabs;

namespace mRemoteNG.Connection.NickHq
{
    /// <summary>
    /// Maps NickHQ session IDs to the <see cref="ConnectionTab"/> that hosts them.
    /// Thread-safe. Used by <see cref="NickHqClient"/> to look up the tab HWND
    /// for paste and screenshot commands.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class SessionTabRegistry
    {
        private static readonly ConcurrentDictionary<string, ConnectionTab> _registry =
            new ConcurrentDictionary<string, ConnectionTab>(System.StringComparer.Ordinal);

        /// <summary>
        /// Associates a NickHQ session ID with the tab that hosts it.
        /// </summary>
        public static void Register(string sessionId, ConnectionTab tab)
        {
            if (!string.IsNullOrEmpty(sessionId) && tab != null)
                _registry[sessionId] = tab;
        }

        /// <summary>
        /// Removes the mapping for the given session ID.
        /// </summary>
        public static void Unregister(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
                _registry.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// Tries to retrieve the <see cref="ConnectionTab"/> for a session ID.
        /// Returns <c>false</c> if not found.
        /// </summary>
        public static bool TryGet(string sessionId, out ConnectionTab tab)
        {
            tab = null!;
            if (string.IsNullOrEmpty(sessionId))
                return false;
            return _registry.TryGetValue(sessionId, out tab!);
        }
    }
}
