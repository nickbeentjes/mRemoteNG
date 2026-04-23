using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace mRemoteNG.Connection.NickHq
{
    /// <summary>
    /// Represents one NickHQ server entry.
    /// </summary>
    public class NickHqServer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";           // e.g. https://keess-mac-mini.taile6c48b.ts.net
        public string BearerToken { get; set; } = "";   // for Mac Claude reads (NICKHQ_API_TOKEN)
        public string AgentToken { get; set; } = "";    // for ?token= writes (AGENT_TOKEN)
        public bool Enabled { get; set; } = true;
        public bool AutoConnect { get; set; } = true;
    }

    /// <summary>
    /// Internal JSON root that holds both the server list and global settings.
    /// </summary>
    internal class NickHqConfigRoot
    {
        [JsonPropertyName("connectOnStartup")]
        public bool ConnectOnStartup { get; set; } = true;

        [JsonPropertyName("servers")]
        public List<NickHqServer> Servers { get; set; } = new();
    }

    /// <summary>
    /// Loads and saves NickHQ server configuration from
    ///   %APPDATA%\mRemoteNG\nickhq_servers.json
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class NickHqConfig
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "mRemoteNG", "nickhq_servers.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // Cached root so ConnectOnStartup and Servers stay in sync
        private static NickHqConfigRoot? _cached;

        private static NickHqConfigRoot LoadRoot()
        {
            if (_cached != null) return _cached;

            if (!File.Exists(_path))
            {
                _cached = new NickHqConfigRoot();
                return _cached;
            }

            try
            {
                string json = File.ReadAllText(_path);
                _cached = JsonSerializer.Deserialize<NickHqConfigRoot>(json, _jsonOpts)
                          ?? new NickHqConfigRoot();
            }
            catch
            {
                _cached = new NickHqConfigRoot();
            }

            return _cached;
        }

        /// <summary>Returns all servers from config. Returns empty list if file is missing.</summary>
        public static List<NickHqServer> Load() => LoadRoot().Servers;

        /// <summary>
        /// Whether to auto-connect enabled servers on startup.
        /// Defaults to true if the key is absent.
        /// </summary>
        public static bool ConnectOnStartup
        {
            get => LoadRoot().ConnectOnStartup;
            set
            {
                LoadRoot().ConnectOnStartup = value;
                Save(LoadRoot().Servers);
            }
        }

        /// <summary>Persists the full server list (and current ConnectOnStartup flag) to disk.</summary>
        public static void Save(List<NickHqServer> servers)
        {
            var root = LoadRoot();
            root.Servers = servers;
            _cached = root;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string json = JsonSerializer.Serialize(root, _jsonOpts);
                File.WriteAllText(_path, json);
            }
            catch
            {
                // Swallow — non-fatal
            }
        }

        /// <summary>
        /// Saves ConnectOnStartup independently without touching the server list.
        /// </summary>
        internal static void SaveConnectOnStartup(bool value, List<NickHqServer> currentServers)
        {
            var root = LoadRoot();
            root.ConnectOnStartup = value;
            root.Servers = currentServers;
            _cached = root;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string json = JsonSerializer.Serialize(root, _jsonOpts);
                File.WriteAllText(_path, json);
            }
            catch { }
        }

        /// <summary>Forces reload from disk on next access (used after external edits).</summary>
        internal static void Invalidate() => _cached = null;
    }
}
