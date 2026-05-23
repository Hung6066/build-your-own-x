using System.Threading.Channels;
using Hope.Agent.Application.Rag;
using Hope.Agent.Rag.Ingestion;
using Hope.Agent.Rag.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.Rag;

public static class DependencyInjection
{
    public static IServiceCollection AddRag(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<RagOptions>(cfg.GetSection("Rag"));

        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagOptions>>().Value;
            return Channel.CreateBounded<IngestRequest>(new BoundedChannelOptions(opts.IngestionChannelCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        });

        services.AddScoped<IIngestionService, IngestionService>();
        services.AddSingleton<IReranker, LlmReranker>();
        services.AddScoped<IRetriever, HybridRetriever>();
        services.AddHostedService<IngestionWorker>();
        return services;
    }
}
