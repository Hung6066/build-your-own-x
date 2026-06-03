namespace Hope.Agent.Workflows;

public sealed class TemporalOptions
{
    public const string SectionName = "Temporal";
    public string TargetHost { get; set; } = "localhost:7233";
    public string Namespace { get; set; } = "default";
    public string TaskQueue { get; set; } = "hope-agent-clinical";
    public bool EnableWorker { get; set; }
}
