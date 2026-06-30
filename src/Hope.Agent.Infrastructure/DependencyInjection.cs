using System.Net.Security;
using System.Security.Authentication;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Backup;
using Hope.Agent.Application.Billing;
using Hope.Agent.Application.Channels;
using Hope.Agent.Application.Compliance;
using Hope.Agent.Application.Compression;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Fhir;
using Hope.Agent.Application.Insights;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Locking;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Memory;
using Hope.Agent.Application.Personalization;
using Hope.Agent.Application.Prompts;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.SlashCommands;
using Hope.Agent.Application.Training;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.UserModeling;
using Hope.Agent.Application.Workflows;
using Hope.Agent.Application.Caching;
using Hope.Agent.Infrastructure.Backup;
using Hope.Agent.Infrastructure.Autonomy;
using Hope.Agent.Infrastructure.Billing;
using Hope.Agent.Infrastructure.Caching;
using Hope.Agent.Infrastructure.Compliance;
using Hope.Agent.Infrastructure.Compression;
using Hope.Agent.Infrastructure.Eventing;
using Hope.Agent.Infrastructure.Channels;
using Hope.Agent.Infrastructure.Channels.Email;
using Hope.Agent.Infrastructure.Channels.Slack;
using Hope.Agent.Infrastructure.Channels.Zalo;
using Hope.Agent.Infrastructure.Fhir;
using Hope.Agent.Infrastructure.Insights;
using Hope.Agent.Infrastructure.Knowledge;
using Hope.Agent.Infrastructure.Learning;
using Hope.Agent.Infrastructure.Locking;
using Hope.Agent.Infrastructure.Maintenance;
using Hope.Agent.Infrastructure.Memory;
using Hope.Agent.Infrastructure.Messaging;
using Hope.Agent.Infrastructure.Personalization;
using Hope.Agent.Infrastructure.Persistence;
using Hope.Agent.Infrastructure.Prompts;
using Hope.Agent.Infrastructure.Rag;
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
        var environment = cfg["ASPNETCORE_ENVIRONMENT"] ?? cfg["DOTNET_ENVIRONMENT"];
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

        services.AddSingleton<ITenantContext, AsyncLocalTenantContext>();
        services.AddSingleton<TenantSessionConnectionInterceptor>();

        services.AddDbContextPool<AgentDbContext>((sp, o) =>
        {
            var connStr = cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Missing Postgres connection string.");
            if (!isDevelopment && !connStr.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase))
                connStr += ";SSL Mode=Require;Trust Server Certificate=false";

            o.UseNpgsql(connStr, npg => npg.EnableRetryOnFailure(3));
            o.AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>());
        });
        services.AddPooledDbContextFactory<AgentDbContext>((sp, o) =>
        {
            var connStr = cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Missing Postgres connection string.");
            if (!isDevelopment && !connStr.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase))
                connStr += ";SSL Mode=Require;Trust Server Certificate=false";

            o.UseNpgsql(connStr, npg => npg.EnableRetryOnFailure(3));
            o.AddInterceptors(sp.GetRequiredService<TenantSessionConnectionInterceptor>());
        });
        services.AddSingleton<IReadOnlyAgentDbContextFactory, ReadReplicaAgentDbContextFactory>();

        services.AddScoped<IConversationRepository, EfConversationRepository>();
        services.AddSingleton<IAppointmentBookingStore, EfAppointmentBookingStore>();
        services.AddSingleton<IMedicalSummaryStore, EfMedicalSummaryStore>();
        services.AddSingleton<IReminderRecordStore, EfReminderRecordStore>();
        services.AddSingleton<IAuditReportStore, EfAuditReportStore>();
        services.AddSingleton<IOptimizationCostHints, EfOptimizationCostHints>();
        services.Configure<AgentOwnershipOptions>(cfg.GetSection(AgentOwnershipOptions.SectionName));
        services.Configure<AgentOpsOptions>(cfg.GetSection(AgentOpsOptions.SectionName));
        services.Configure<AccessMatrixOptions>(cfg.GetSection(AccessMatrixOptions.SectionName));
        services.Configure<ApprovalSlaOptions>(cfg.GetSection(ApprovalSlaOptions.SectionName));
        services.Configure<AgentVersionOptions>(cfg.GetSection(AgentVersionOptions.SectionName));
        services.Configure<RuntimeScaleOptions>(cfg.GetSection(RuntimeScaleOptions.SectionName));
        services.Configure<AgentRegistryOptions>(cfg.GetSection(AgentRegistryOptions.SectionName));
        services.Configure<OrchestrationDagOptions>(cfg.GetSection(OrchestrationDagOptions.SectionName));
        services.Configure<TenantIsolationOptions>(cfg.GetSection(TenantIsolationOptions.SectionName));
        services.Configure<CostControlOptions>(cfg.GetSection(CostControlOptions.SectionName));
        services.Configure<DataLifecycleOptions>(cfg.GetSection(DataLifecycleOptions.SectionName));
        services.Configure<DeploymentSafetyOptions>(cfg.GetSection(DeploymentSafetyOptions.SectionName));
        services.Configure<DatabaseScaleOptions>(cfg.GetSection(DatabaseScaleOptions.SectionName));
        services.Configure<OutboxOptions>(cfg.GetSection(OutboxOptions.SectionName));
        services.Configure<ZeroTrustOptions>(cfg.GetSection(ZeroTrustOptions.SectionName));
        services.Configure<SecretManagementOptions>(cfg.GetSection(SecretManagementOptions.SectionName));
        services.Configure<DataPerimeterOptions>(cfg.GetSection(DataPerimeterOptions.SectionName));
        services.Configure<AuditImmutabilityOptions>(cfg.GetSection(AuditImmutabilityOptions.SectionName));
        services.Configure<StorageEncryptionOptions>(cfg.GetSection(StorageEncryptionOptions.SectionName));
        services.Configure<RedisHighAvailabilityOptions>(cfg.GetSection(RedisHighAvailabilityOptions.SectionName));
        services.Configure<DlpOptions>(cfg.GetSection(DlpOptions.SectionName));
        services.Configure<EgressPolicyOptions>(cfg.GetSection(EgressPolicyOptions.SectionName));
        services.Configure<PolicyAsCodeOptions>(cfg.GetSection(PolicyAsCodeOptions.SectionName));
        services.Configure<RuntimeSandboxOptions>(cfg.GetSection(RuntimeSandboxOptions.SectionName));
        services.Configure<EnterpriseDataPerimeterOptions>(cfg.GetSection(EnterpriseDataPerimeterOptions.SectionName));
        services.Configure<SecureModelRoutingOptions>(cfg.GetSection(SecureModelRoutingOptions.SectionName));
        services.Configure<AdversarialSimulationOptions>(cfg.GetSection(AdversarialSimulationOptions.SectionName));
        services.Configure<IncidentResponseOptions>(cfg.GetSection(IncidentResponseOptions.SectionName));
        services.Configure<AutonomyOptions>(cfg.GetSection(AutonomyOptions.Section));
        services.Configure<AutonomyDailyReviewOptions>(cfg.GetSection(AutonomyDailyReviewOptions.Section));
        services.Configure<AutonomyLevel5Options>(cfg.GetSection(AutonomyLevel5Options.Section));
        services.Configure<AutonomyAgiLikeOptions>(cfg.GetSection(AutonomyAgiLikeOptions.Section));
        services.Configure<AgenticRagOptions>(cfg.GetSection(AgenticRagOptions.SectionName));
        services.AddSingleton<IAgentDecisionStore, EfAgentDecisionStore>();
        services.AddSingleton<IAutonomousActionStore, EfAutonomousActionStore>();
        services.AddSingleton<IAutonomyGoalStore, EfAutonomyGoalStore>();
        services.AddSingleton<IAutonomyReflectionStore, EfAutonomyReflectionStore>();
        services.AddSingleton<IAutonomyLearningFactStore, EfAutonomyLearningFactStore>();
        services.AddSingleton<IAutonomyDecisionService, AutonomyDecisionService>();
        services.AddSingleton<IPatientTimelineService, PatientTimelineService>();
        services.AddSingleton<IAgentSuggestionService, AgentSuggestionService>();
        services.AddSingleton<IAutonomySafetyBudget, AutonomySafetyBudget>();
        services.AddSingleton<IAutonomyOutcomeVerifier, AutonomyOutcomeVerifier>();
        services.AddSingleton<IAutonomyLevel5ControlService, AutonomyLevel5ControlService>();
        services.AddScoped<IAutonomyAgiLikeService, AutonomyAgiLikeService>();
        services.AddScoped<IAutonomyDailyReviewService, AutonomyDailyReviewService>();
        services.AddScoped<IAutonomousActionExecutor, AutonomousActionExecutor>();
        services.AddScoped<IAgenticRagService, AgenticRagService>();
        var hostedServicesEnabled = cfg.GetValue("Runtime:EnableHostedServices", true);
        if (hostedServicesEnabled)
        {
            services.AddHostedService<AutonomousActionWorker>();
            services.AddHostedService<AutonomyDailyReviewWorker>();
            services.AddHostedService<OutboxPublisherWorker>();
            services.AddHostedService<ScaleMaintenanceWorker>();
            services.AddHostedService<AuditImmutabilityWorker>();
            services.AddHostedService<AdversarialSimulationWorker>();
        }
        services.AddHostedService<ProductionSecurityValidator>();
        // Tamper-evident audit chain: EfAuditSink is the persistent sink, wrapped by
        // HashChainedAuditSink which links every event to its predecessor via SHA-256.
        services.AddScoped<EfAuditSink>();
        services.AddScoped<IAuditSink>(sp => new HashChainedAuditSink(
            sp.GetRequiredService<EfAuditSink>(),
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DataPerimeterOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HashChainedAuditSink>>()));
        services.AddSingleton<IJwtKeyProvider, RotatingJwtKeyProvider>();

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisConn = cfg.GetConnectionString("Redis") ?? "localhost:6379";
            var redisHa = cfg.GetSection(RedisHighAvailabilityOptions.SectionName).Get<RedisHighAvailabilityOptions>()
                ?? new RedisHighAvailabilityOptions();

            ConfigurationOptions options;
            if (redisHa.Enabled && redisHa.Endpoints.Length > 0)
            {
                options = new ConfigurationOptions
                {
                    AbortOnConnectFail = false,
                    ServiceName = redisHa.ServiceName,
                };
                foreach (var endpoint in redisHa.Endpoints)
                {
                    if (!string.IsNullOrWhiteSpace(endpoint))
                        options.EndPoints.Add(endpoint.Trim());
                }
            }
            else
            {
                options = ConfigurationOptions.Parse(redisConn);
                options.AbortOnConnectFail = false;
            }

            if (!isDevelopment)
            {
                options.Ssl = true;
                options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
            }

            return ConnectionMultiplexer.Connect(options);
        });

        // Embedding vector cache — Redis-backed, avoids re-embedding identical text under load.
        services.Configure<EmbeddingCacheOptions>(cfg.GetSection(EmbeddingCacheOptions.Section));
        services.AddSingleton<IEmbeddingCache, RedisEmbeddingCache>();

        var qdrant = cfg.GetSection("Qdrant").Get<QdrantOptions>() ?? new QdrantOptions();
        if (!isDevelopment)
        {
            if (qdrant.Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                qdrant.Host = "https://" + qdrant.Host["http://".Length..];
            }
            else if (!qdrant.Host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                qdrant.Host = $"https://{qdrant.Host}";
            }
        }

        services.AddSingleton(qdrant);
        services.AddSingleton(_ => new QdrantClient(qdrant.Host, qdrant.Port, apiKey: qdrant.ApiKey));
        services.AddSingleton<ISparseEncoder, Bm25SparseEncoder>();
        services.AddSingleton<EfMemoryStore>();
        services.AddSingleton<QdrantMemoryStore>();
        services.AddSingleton<IMemoryStore, HybridMemoryStore>();
        services.AddSingleton<IVectorIndex, QdrantVectorIndex>();
        services.AddScoped<IDocumentStore, EfDocumentStore>();

        // SOTA memory pipeline: Mem0/A-Mem consolidation + LLM reranking + periodic forgetting.
        services.AddScoped<IMemoryConsolidator, LlmMemoryConsolidator>();
        services.AddScoped<IMemoryReranker, LlmMemoryReranker>();
        services.Configure<MemoryMaintenanceOptions>(cfg.GetSection(MemoryMaintenanceOptions.Section));
        if (hostedServicesEnabled)
            services.AddHostedService<MemoryMaintenanceHostedService>();

        var kafka = cfg.GetSection("Kafka").Get<KafkaOptions>() ?? new KafkaOptions();
        services.AddSingleton(kafka);
        services.AddSingleton<IOutboxStore, EfOutboxStore>();
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        services.AddSingleton<IEventConsumer, KafkaEventConsumer>();

        services.AddSingleton<IPhiRedactor, RegexPhiRedactor>();
        services.AddSingleton<IPromptEgressGuard, RegexPromptEgressGuard>();
        services.AddSingleton<IEnvelopeEncryptionService, EnvelopeEncryptionService>();
        services.AddSingleton<IPolicyEngine, JsonPolicyEngine>();
        services.AddSingleton<IApiKeyLifecycleStore, EfApiKeyLifecycleStore>();
        services.AddSingleton<IDataPerimeterService, EnterpriseDataPerimeterService>();
        services.AddSingleton<ISecureModelRoutingPolicy, SecureModelRoutingPolicy>();
        services.AddScoped<IContextProvenanceStore, EfContextProvenanceStore>();
        services.AddScoped<IIncidentResponseService, IncidentResponseService>();
        services.AddSingleton<IRefreshTokenStore, RedisRefreshTokenStore>();
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        services.AddSingleton<IDpopValidator, DpopValidator>();
        // Phase 2 — AGT layered shield: heuristic inner + AGT ML outer.
        // HeuristicPromptShield registered as concrete so AgtPromptShield can inject it.
        services.AddOptions<GovernancePolicyOptions>()
            .BindConfiguration(GovernancePolicyOptions.SectionName);
        services.AddSingleton<HeuristicPromptShield>();
        services.AddSingleton<IPromptShield, AgtPromptShield>();
        services.AddSingleton<IOutputShield, RegexOutputShield>();
        // NemoClaw-inspired security rails
        services.AddSingleton<ISsrfGuard, HeuristicSsrfGuard>();
        services.AddSingleton<IRetrievalRail, PromptShieldRetrievalRail>();

        services.AddScoped<IFeedbackStore, EfFeedbackStore>();
        services.AddScoped<ISkillLibrary, EfSkillLibrary>();
        services.AddScoped<IAdaptiveRouter, BanditAdaptiveRouter>();
        services.AddScoped<IEvalCaseStore, EfEvalCaseStore>();
        services.AddScoped<IEvaluationHarness, EvaluationHarness>();
        if (hostedServicesEnabled)
            services.AddHostedService<EvaluationHarnessHostedService>();

        services.AddScoped<IShadowComparator, ShadowComparator>();
        services.AddScoped<IAdversarialPatternStore, EfAdversarialPatternStore>();
        if (hostedServicesEnabled)
            services.AddHostedService<AdversarialAutoPromoter>();

        services.Configure<ToolApprovalOptions>(cfg.GetSection(ToolApprovalOptions.Section));
        services.AddSingleton<IToolApprovalPolicy, ConfigurableToolApprovalPolicy>();
        services.AddSingleton<IToolAccessPolicy, ConfigurableToolAccessPolicy>();
        services.AddSingleton<IToolApprovalGate, SignalRApprovalGate>();
        services.AddScoped<IToolApprovalRequestStore, EfToolApprovalRequestStore>();

        services.Configure<ScheduledTaskOptions>(cfg.GetSection(ScheduledTaskOptions.Section));
        if (hostedServicesEnabled)
            services.AddHostedService<ScheduledAgentTaskRunner>();

        services.Configure<TelegramBotOptions>(cfg.GetSection(TelegramBotOptions.Section));
        if (hostedServicesEnabled)
            services.AddHostedService<TelegramBotService>();

        // Phase 10 — multi-channel gateway (Zalo, Slack, Email).
        // Channel HTTP clients: scoped timeout prevents indefinite hangs on Zalo/Slack API calls.
        services.AddHttpClient("zalo",
            c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("slack",
            c => c.Timeout = TimeSpan.FromSeconds(30));
        services.Configure<ZaloOptions>(cfg.GetSection(ZaloOptions.Section));
        services.Configure<SlackOptions>(cfg.GetSection(SlackOptions.Section));
        services.Configure<EmailOptions>(cfg.GetSection(EmailOptions.Section));
        services.AddSingleton<ZaloChannel>();
        services.AddSingleton<SlackChannel>();
        services.AddSingleton<EmailChannel>();
        services.AddSingleton<IExternalChannel>(sp => new DlpExternalChannel(
            sp.GetRequiredService<ZaloChannel>(),
            sp.GetRequiredService<IPhiRedactor>(),
            sp.GetRequiredService<IOutputShield>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DlpOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DlpExternalChannel>>()));
        services.AddSingleton<IExternalChannel>(sp => new DlpExternalChannel(
            sp.GetRequiredService<SlackChannel>(),
            sp.GetRequiredService<IPhiRedactor>(),
            sp.GetRequiredService<IOutputShield>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DlpOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DlpExternalChannel>>()));
        services.AddSingleton<IExternalChannel>(sp => new DlpExternalChannel(
            sp.GetRequiredService<EmailChannel>(),
            sp.GetRequiredService<IPhiRedactor>(),
            sp.GetRequiredService<IOutputShield>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DlpOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DlpExternalChannel>>()));
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
        if (hostedServicesEnabled)
        {
            services.AddHostedService<SessionInsightHostedService>();
            services.AddHostedService<SkillSelfImprovementHostedService>();
        }

        // Phase 12 — trajectory export for fine-tuning.
        services.Configure<TrajectoryExportOptions>(cfg.GetSection(TrajectoryExportOptions.Section));
        services.AddScoped<ITrajectoryExporter, EfTrajectoryExporter>();

        // Phase 14 — DPO / LoRA fine-tuning pipeline.
        services.Configure<FineTuningOptions>(cfg.GetSection(FineTuningOptions.Section));
        services.AddScoped<IPreferenceStore, EfPreferenceStore>();
        services.AddScoped<IDpoExporter, EfDpoExporter>();
        // Fine-tune jobs can take several minutes to submit/poll; allow 5 min per call.
        services.AddHttpClient("finetune",
            c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddScoped<IFinetuneJobService, HttpFinetuneJobService>();

        // Phase 13 — operational maturity (kanban, clinical context, migration, diagnostics).
        services.Configure<Hope.Agent.Application.Tasks.KanbanOptions>(cfg.GetSection(Hope.Agent.Application.Tasks.KanbanOptions.Section));
        services.AddScoped<Hope.Agent.Application.Tasks.IKanbanTaskStore, Hope.Agent.Infrastructure.Tasks.EfKanbanTaskStore>();
        services.Configure<Hope.Agent.Application.Context.ClinicalContextOptions>(cfg.GetSection(Hope.Agent.Application.Context.ClinicalContextOptions.Section));
        services.AddSingleton<Hope.Agent.Application.Context.IClinicalContextProvider, Hope.Agent.Infrastructure.Context.FileClinicalContextProvider>();
        services.Configure<Hope.Agent.Application.Migration.MigrationOptions>(cfg.GetSection(Hope.Agent.Application.Migration.MigrationOptions.Section));
        services.AddScoped<Hope.Agent.Application.Migration.IExternalImporter, Hope.Agent.Infrastructure.Migration.ExternalChatbotImporter>();
        services.AddScoped<Hope.Agent.Application.Diagnostics.IDiagnosticRunner, Hope.Agent.Infrastructure.Diagnostics.DiagnosticRunner>();

        // ── Global outbound TLS hardening ─────────────────────────────────────────
        // Applied to EVERY named and typed HttpClient registered in this process:
        //   • Minimum TLS 1.2 — blocks TLS 1.0/1.1 downgrade attacks.
        //   • Certificate validation ON (default, made explicit via RemoteCertificateValidationCallback = null).
        //   • ConnectTimeout 10 s — prevents thread exhaustion from stalled TCP connects.
        //   • PooledConnectionLifetime 5 min — rotates connections so DNS / cert changes propagate.
        // Individual LLM clients control their total request timeout through the Polly
        // StandardResilienceHandler (AttemptTimeout 90 s, TotalRequestTimeout 120 s);
        // channel clients use the explicit HttpClient.Timeout set above.
        services.ConfigureHttpClientDefaults(b =>
        {
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    // RemoteCertificateValidationCallback = null means the default OS validator
                    // runs — rejects expired, self-signed, and revoked certificates.
                },
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
            b.SetHandlerLifetime(TimeSpan.FromMinutes(5));
        });

        // ── Phase 19 (P0): Enterprise gaps ───────────────────────────────────
        // C-4: Activate tool result cache — replace no-op with Redis.
        services.AddSingleton<IToolResultCache, RedisToolResultCache>();
        services.AddSingleton<INotificationRateLimiter, RedisNotificationRateLimiter>();
        // C-3: Per-tenant cost attribution & budget enforcement.
        services.AddSingleton<ITenantBillingService, TenantBillingService>();
        // C-1: Backup orchestrator (pgBackRest + Qdrant + Neo4j + Redis BGSAVE).
        services.AddSingleton<IBackupOrchestrator, BackupOrchestrator>();
        // C-2: GDPR Right to Erasure (3-phase: soft-delete → crypto-shred → verify).
        services.AddSingleton<IGdprErasureService, GdprErasureService>();

        // ── Phase 20 (P1): Enterprise parity ──────────────────────────────────
        // C-7: Prompt registry & versioning (Git-based with hot-reload).
        services.Configure<PromptRegistryOptions>(cfg.GetSection(PromptRegistryOptions.Section));
        services.Configure<PromptOptimizationOptions>(cfg.GetSection(PromptOptimizationOptions.SectionName));
        services.AddSingleton<IPromptRegistry, GitPromptRegistry>();
        services.AddScoped<IPromptOptimizationService, PromptOptimizationService>();
        if (hostedServicesEnabled)
            services.AddHostedService<PromptOptimizationWorker>();
        // H-7: Distributed locking (Redis SET NX).
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();

        // ── Phase 21 (P2): Strategic integrations ─────────────────────────────
        // H-1: FHIR R4 validation.
        services.AddSingleton<IFhirValidator, FhirValidator>();

        return services;
    }
}
