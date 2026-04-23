using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace mRemoteNG.Orchestrator
{
    /// <summary>
    /// Global registry of active <see cref="OrchestratorEngine"/> instances, keyed by session ID.
    /// Thread-safe. All mutations stop any existing engine first so there is never more than
    /// one engine per session at a time.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class OrchestratorRegistry
    {
        private static readonly ConcurrentDictionary<string, OrchestratorEngine> _engines = new();

        /// <summary>
        /// Starts a new orchestration for <paramref name="sessionId"/>, stopping any existing one first.
        /// </summary>
        public static void Start(string sessionId, string context, OrchestratorOptions options)
        {
            Stop(sessionId);   // stop any existing engine for this session
            var engine = new OrchestratorEngine(sessionId, context, options);
            _engines[sessionId] = engine;
            engine.Start();
        }

        /// <summary>
        /// Stops and removes the engine for <paramref name="sessionId"/>. No-op if none exists.
        /// </summary>
        public static void Stop(string sessionId)
        {
            if (_engines.TryRemove(sessionId, out var engine))
                engine.Stop();
        }

        /// <summary>Returns <c>true</c> if an active orchestrator exists for the session.</summary>
        public static bool IsOrchestrated(string sessionId) =>
            _engines.ContainsKey(sessionId);

        /// <summary>Returns the engine for <paramref name="sessionId"/>, or <c>null</c> if none.</summary>
        public static OrchestratorEngine? Get(string sessionId) =>
            _engines.TryGetValue(sessionId, out var e) ? e : null;

        /// <summary>Snapshot of all currently active (sessionId, engine) pairs.</summary>
        public static IEnumerable<KeyValuePair<string, OrchestratorEngine>> GetAll() =>
            _engines.AsEnumerable();
    }
}
