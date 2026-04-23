using System.Runtime.Versioning;

namespace mRemoteNG.Orchestrator
{
    [SupportedOSPlatform("windows")]
    public enum OrchestratorStatus
    {
        Running,
        Waiting,
        Blocked,
        Done,
        Error,
        Stopped
    }
}
