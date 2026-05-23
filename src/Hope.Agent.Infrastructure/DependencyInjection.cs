using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Security;
using Hope.Agent.Infrastructure.Eventing;
using Hope.Agent.Infrastructure.Knowledge;
using Hope.Agent.Infrastructure.Learning;
using Hope.Agent.Infrastructure.Memory;
using Hope.Agent.Infrastructure.Messaging;
using Hope.Agent.Infrastructure.Persistence;
using Hope.Agent.Infrastructure.Scheduling;
using Hope.Agent.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using Qdrant.Client;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDbContextPool<AgentDbContext>(o =>
            o.UseNpgsql(cfg.GetConnectionString("Postgres"), npg => npg.EnableRetryOnFailure(3)));

        services.AddScoped<IConversationRepository, EfConversationRepository>();
        services.AddScoped<IAuditSink, EfAuditSink>();

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(cfg.GetConnectionString("Redis") ?? "localhost:6379"));

        var qdrant = cfg.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
        services.AddSingleton(qdrant);
        services.AddSingleton(_ => new QdrantClient(qdrant.Host, qdrant.Port, apiKey: qdrant.ApiKey));
        services.AddSingleton<IMemoryStore, QdrantMemoryStore>();
        services.AddSingleton<IVectorIndex, QdrantVectorIndex>();
        services.AddScoped<IDocumentStore, EfDocumentStore>();

        var kafka = cfg.GetSection("Kafka").Get<KafkaOptions>() ?? new KafkaOptions();
        services.AddSingleton(kafka);
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        services.AddSingleton<IEventConsumer, KafkaEventConsumer>();

        services.AddSingleton<IPhiRedactor, RegexPhiRedactor>();
        services.AddSingleton<IPromptShield, HeuristicPromptShield>();

        services.AddScoped<IFeedbackStore, EfFeedbackStore>();
        services.AddScoped<ISkillLibrary, EfSkillLibrary>();
        services.AddScoped<IAdaptiveRouter, BanditAdaptiveRouter>();
        services.AddScoped<IEvaluationHarness, EvaluationHarness>();
        services.AddHostedService<EvaluationHarnessHostedService>();

        services.AddScoped<IShadowComparator, ShadowComparator>();
        services.AddScoped<IAdversarialPatternStore, EfAdversarialPatternStore>();
        services.AddHostedService<AdversarialAutoPromoter>();

        services.Configure<ScheduledTaskOptions>(cfg.GetSection(ScheduledTaskOptions.Section));
        services.AddHostedService<ScheduledAgentTaskRunner>();

        services.Configure<TelegramBotOptions>(cfg.GetSection(TelegramBotOptions.Section));
        services.AddHostedService<TelegramBotService>();

        var neo = cfg.GetSection("Neo4j").Get<Neo4jOptions>() ?? new Neo4jOptions();
        services.AddSingleton(neo);
        services.AddSingleton<IDriver>(_ => GraphDatabase.Driver(neo.Uri, AuthTokens.Basic(neo.Username, neo.Password)));
        services.AddSingleton<IKnowledgeGraphStore, Neo4jKnowledgeGraphStore>();

        return services;
    }
}
