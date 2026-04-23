using System.Runtime.Versioning;

namespace mRemoteNG.Orchestrator
{
    [SupportedOSPlatform("windows")]
    public class OrchestratorOptions
    {
        public bool AutoAnswerGsdPrompts { get; set; } = true;
        public bool StopOnError         { get; set; } = false;
        public bool NotifyOnDone        { get; set; } = true;
        public bool NotifyOnBlocked     { get; set; } = true;
    }
}
