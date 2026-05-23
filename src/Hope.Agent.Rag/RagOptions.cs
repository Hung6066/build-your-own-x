namespace Hope.Agent.Rag;

public sealed class RagOptions
{
    public int ChunkSize { get; set; } = 800;
    public int ChunkOverlap { get; set; } = 120;
    public int EmbedBatchSize { get; set; } = 32;
    public int IngestionChannelCapacity { get; set; } = 256;
    public int IngestionWorkers { get; set; } = 2;
    public bool RerankByDefault { get; set; } = true;
    public string DefaultCollection { get; set; } = "clinical_guidelines";
}
