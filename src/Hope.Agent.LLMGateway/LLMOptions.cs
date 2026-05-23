namespace Hope.Agent.LLMGateway;

public sealed class LLMOptions
{
    public string DefaultChatProvider { get; set; } = "openai";
    public string DefaultEmbeddingProvider { get; set; } = "openai";
    public OpenAICompatibleOptions OpenAI { get; set; } = new();
    public OpenAICompatibleOptions Qwen { get; set; } = new() { BaseUrl = "http://localhost:8000/v1", Model = "qwen3" };
    public AnthropicOptions Anthropic { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public OpenAICompatibleOptions Ollama { get; set; } = new() { BaseUrl = "http://localhost:11434/v1", Model = "llama3.2" };
}

public sealed class OpenAICompatibleOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-3-5-sonnet-latest";
    public string Version { get; set; } = "2023-06-01";
}

public sealed class GeminiOptions
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.0-flash";
    public string EmbeddingModel { get; set; } = "text-embedding-004";
}
