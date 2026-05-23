using Hope.Agent.Application.LLM;

namespace Hope.Agent.LLMGateway;

internal sealed class LLMRouter(IEnumerable<IChatCompletionProvider> chat, IEnumerable<IEmbeddingProvider> embed, LLMOptions options) : ILLMRouter
{
    private readonly Dictionary<string, IChatCompletionProvider> _chat = chat.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IEmbeddingProvider> _embed = embed.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IChatCompletionProvider SelectChat(string? hint = null)
    {
        var key = hint ?? options.DefaultChatProvider;
        return _chat.TryGetValue(key, out var p) ? p
            : _chat.Values.FirstOrDefault() ?? throw new InvalidOperationException("No chat provider configured");
    }

    public IEmbeddingProvider SelectEmbedding(string? hint = null)
    {
        var key = hint ?? options.DefaultEmbeddingProvider;
        return _embed.TryGetValue(key, out var p) ? p
            : _embed.Values.FirstOrDefault() ?? throw new InvalidOperationException("No embedding provider configured");
    }
}
