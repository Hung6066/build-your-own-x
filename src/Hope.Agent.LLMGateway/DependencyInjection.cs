using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.Learning;
using Hope.Agent.LLMGateway.Knowledge;
using Hope.Agent.LLMGateway.Learning;
using Hope.Agent.LLMGateway.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Hope.Agent.LLMGateway;

public static class DependencyInjection
{
    public static IServiceCollection AddLLMGateway(this IServiceCollection services, IConfiguration cfg)
    {
        var options = cfg.GetSection("LLM").Get<LLMOptions>() ?? new LLMOptions();
        services.AddSingleton(options);

        AddOpenAICompat(services, "openai", options.OpenAI, isEmbedding: true);
        AddOpenAICompat(services, "qwen", options.Qwen, isEmbedding: true);
        AddOpenAICompat(services, "ollama", options.Ollama, isEmbedding: false);

        services.AddHttpClient<AnthropicProvider>((sp, c) => AnthropicProvider.Configure(c, options.Anthropic))
            .AddStandardResilienceHandler(ConfigureResilience);
        services.AddSingleton<IChatCompletionProvider>(sp => sp.GetRequiredService<AnthropicProvider>());

        services.AddHttpClient<GeminiProvider>((sp, c) => GeminiProvider.Configure(c, options.Gemini))
            .AddStandardResilienceHandler(ConfigureResilience);
        services.AddSingleton<IChatCompletionProvider>(sp => sp.GetRequiredService<GeminiProvider>());
        services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<GeminiProvider>());

        services.AddSingleton<ILLMRouter, LLMRouter>();
        services.AddSingleton<IReflector, LlmReflector>();
        services.AddSingleton<IJudge, LlmJudge>();
        services.AddSingleton<IKnowledgeExtractor, LlmKnowledgeExtractor>();
        return services;
    }

    private static void AddOpenAICompat(IServiceCollection services, string name, OpenAICompatibleOptions opts, bool isEmbedding)
    {
        services.AddHttpClient($"llm:{name}", c => OpenAICompatibleProvider.Configure(c, opts))
            .AddStandardResilienceHandler(ConfigureResilience);

        services.AddSingleton<IChatCompletionProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient($"llm:{name}");
            return new OpenAICompatibleProvider(http, opts, name);
        });
        if (isEmbedding)
        {
            services.AddSingleton<IEmbeddingProvider>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient($"llm:{name}");
                return new OpenAICompatibleProvider(http, opts, name);
            });
        }
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions opts)
    {
        opts.Retry.MaxRetryAttempts = 2;
        opts.Retry.BackoffType = DelayBackoffType.Exponential;
        opts.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
        opts.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
    }
}
