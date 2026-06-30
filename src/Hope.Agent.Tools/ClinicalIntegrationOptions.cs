namespace Hope.Agent.Tools;

public sealed class ClinicalIntegrationOptions
{
    public const string Section = "ClinicalIntegrations";

    public string? FhirBaseUrl { get; set; }
    public string? FhirApiKey { get; set; }
    public string? HisBaseUrl { get; set; }
    public string? HisApiKey { get; set; }
}
