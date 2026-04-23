using System;
using System.Runtime.Versioning;

namespace mRemoteNG.Orchestrator
{
    [SupportedOSPlatform("windows")]
    public class JournalEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        /// <summary>"command" | "observation" | "decision" | "waiting" | "blocked" | "done"</summary>
        public string Type    { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
