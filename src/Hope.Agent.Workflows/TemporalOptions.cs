namespace Hope.Agent.Workflows;

public sealed class TemporalOptions
{
    public const string SectionName = "Temporal";
    public string TargetHost { get; set; } = "localhost:7233";
    public string[] TargetHosts { get; set; } = [];
    public string Namespace { get; set; } = "default";
    public string TaskQueue { get; set; } = "hope-agent-clinical";
    public string WorkflowVersion { get; set; } = "v1";
    public bool EnforceWorkflowVersionGate { get; set; } = true;
    public string[] AllowedWorkflowVersions { get; set; } = [];
    public bool EnableCanaryMultiVersionRollout { get; set; }
    public string[] CanaryAllowedWorkflowVersions { get; set; } = [];
    public DateTimeOffset? CutoverAtUtc { get; set; }
    public bool AutoBlockPreviousVersionsAfterCutover { get; set; } = true;
    public bool EnableWorker { get; set; }
}
