using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Channels;
using Hope.Agent.Application.Compression;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Insights;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Personalization;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.SlashCommands;
using Hope.Agent.Application.Training;
using Hope.Agent.Application.UserModeling;
using Hope.Agent.Infrastructure.Compression;
using Hope.Agent.Infrastructure.Eventing;
using Hope.Agent.Infrastructure.Channels;
using Hope.Agent.Infrastructure.Channels.Email;
using Hope.Agent.Infrastructure.Channels.Slack;
using Hope.Agent.Infrastructure.Channels.Zalo;
using Hope.Agent.Infrastructure.Insights;
using Hope.Agent.Infrastructure.Knowledge;
using Hope.Agent.Infrastructure.Learning;
using Hope.Agent.Infrastructure.Memory;
using Hope.Agent.Infrastructure.Messaging;
using Hope.Agent.Infrastructure.Personalization;
using Hope.Agent.Infrastructure.Persistence;
using Hope.Agent.Infrastructure.Scheduling;
using Hope.Agent.Infrastructure.Security;
using Hope.Agent.Infrastructure.SlashCommands;
using Hope.Agent.Infrastructure.Training;
using Hope.Agent.Infrastructure.UserModeling;
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
        services.AddScoped<IEvalCaseStore, EfEvalCaseStore>();
        services.AddScoped<IEvaluationHarness, EvaluationHarness>();
        services.AddHostedService<EvaluationHarnessHostedService>();

        services.AddScoped<IShadowComparator, ShadowComparator>();
        services.AddScoped<IAdversarialPatternStore, EfAdversarialPatternStore>();
        services.AddHostedService<AdversarialAutoPromoter>();

        services.Configure<ToolApprovalOptions>(cfg.GetSection(ToolApprovalOptions.Section));
        services.AddSingleton<IToolApprovalPolicy, ConfigurableToolApprovalPolicy>();
        services.AddSingleton<IToolApprovalGate, SignalRApprovalGate>();
        services.AddScoped<IToolApprovalRequestStore, EfToolApprovalRequestStore>();

        services.Configure<ScheduledTaskOptions>(cfg.GetSection(ScheduledTaskOptions.Section));
        services.AddHostedService<ScheduledAgentTaskRunner>();

        services.Configure<TelegramBotOptions>(cfg.GetSection(TelegramBotOptions.Section));
        services.AddHostedService<TelegramBotService>();

        // Phase 10 — multi-channel gateway (Zalo, Slack, Email).
        services.AddHttpClient("zalo");
        services.AddHttpClient("slack");
        services.Configure<ZaloOptions>(cfg.GetSection(ZaloOptions.Section));
        services.Configure<SlackOptions>(cfg.GetSection(SlackOptions.Section));
        services.Configure<EmailOptions>(cfg.GetSection(EmailOptions.Section));
        services.AddSingleton<IExternalChannel, ZaloChannel>();
        services.AddSingleton<IExternalChannel, SlackChannel>();
        services.AddSingleton<IExternalChannel, EmailChannel>();
        services.AddSingleton<IChannelRegistry, ChannelRegistry>();
        services.AddScoped<IChannelMessageRouter, ChannelMessageRouter>();

        var neo = cfg.GetSection("Neo4j").Get<Neo4jOptions>() ?? new Neo4jOptions();
        services.AddSingleton(neo);
        services.AddSingleton<IDriver>(_ => GraphDatabase.Driver(neo.Uri, AuthTokens.Basic(neo.Username, neo.Password)));
        services.AddSingleton<IKnowledgeGraphStore, Neo4jKnowledgeGraphStore>();

        // Phase 11 — advanced learning & UX.
        services.Configure<UserModelOptions>(cfg.GetSection(UserModelOptions.Section));
        services.Configure<SessionInsightOptions>(cfg.GetSection(SessionInsightOptions.Section));
        services.Configure<ConversationCompressorOptions>(cfg.GetSection(ConversationCompressorOptions.Section));
        services.Configure<SkillSelfImprovementOptions>(cfg.GetSection(SkillSelfImprovementOptions.Section));
        services.AddScoped<IUserModelService, LlmUserModelService>();
        services.AddScoped<ISessionInsightService, EfSessionInsightService>();
        services.AddScoped<IUserPreferenceStore, EfUserPreferenceStore>();
        services.AddScoped<IConversationCompressor, LlmConversationCompressor>();
        services.AddScoped<ISlashCommandHandler, HelpCommand>();
        services.AddScoped<ISlashCommandHandler, PersonalityCommand>();
        services.AddScoped<ISlashCommandHandler, ModelCommand>();
        services.AddScoped<ISlashCommandHandler, UndoCommand>();
        services.AddScoped<ISlashCommandHandler, CompressCommand>();
        services.AddScoped<ISlashCommandHandler, WhoamiCommand>();
        services.AddScoped<ISlashCommandRouter, SlashCommandRouter>();
        services.AddHostedService<SessionInsightHostedService>();
        services.AddHostedService<SkillSelfImprovementHostedService>();

        // Phase 12 — trajectory export for fine-tuning.
        services.Configure<TrajectoryExportOptions>(cfg.GetSection(TrajectoryExportOptions.Section));
        services.AddScoped<ITrajectoryExporter, EfTrajectoryExporter>();

        // Phase 13 — operational maturity (kanban, clinical context, migration, diagnostics).
        services.Configure<Hope.Agent.Application.Tasks.KanbanOptions>(cfg.GetSection(Hope.Agent.Application.Tasks.KanbanOptions.Section));
        services.AddScoped<Hope.Agent.Application.Tasks.IKanbanTaskStore, Hope.Agent.Infrastructure.Tasks.EfKanbanTaskStore>();
        services.Configure<Hope.Agent.Application.Context.ClinicalContextOptions>(cfg.GetSection(Hope.Agent.Application.Context.ClinicalContextOptions.Section));
        services.AddSingleton<Hope.Agent.Application.Context.IClinicalContextProvider, Hope.Agent.Infrastructure.Context.FileClinicalContextProvider>();
        services.Configure<Hope.Agent.Application.Migration.MigrationOptions>(cfg.GetSection(Hope.Agent.Application.Migration.MigrationOptions.Section));
        services.AddScoped<Hope.Agent.Application.Migration.IExternalImporter, Hope.Agent.Infrastructure.Migration.ExternalChatbotImporter>();
        services.AddScoped<Hope.Agent.Application.Diagnostics.IDiagnosticRunner, Hope.Agent.Infrastructure.Diagnostics.DiagnosticRunner>();

        return services;
    }
}
