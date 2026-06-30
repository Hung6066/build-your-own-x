using Hope.Agent.AgentRuntime;
using Hope.Agent.Application.Observability;
using Hope.Agent.Infrastructure;
using Hope.Agent.LLMGateway;
using Hope.Agent.MultiAgent;
using Hope.Agent.Rag;
using Hope.Agent.Realtime;
using Hope.Agent.Tools;
using Hope.Agent.Workflows;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration["Runtime:EnableHostedServices"] ??= "true";
builder.Configuration["Runtime:ApiAcceptsBackgroundJobs"] ??= "false";

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Services.AddSerilog();

builder.Services.AddAgentInfrastructure(builder.Configuration);
builder.Services.AddLLMGateway(builder.Configuration);
builder.Services.AddAgentTools(builder.Configuration);
builder.Services.AddRag(builder.Configuration);
builder.Services.AddMultiAgent();
builder.Services.AddRealtime();
builder.Services.AddWorkflows(builder.Configuration);
builder.Services.AddAgentRuntime(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("Hope.Agent.Worker"))
    .WithMetrics(m => m
        .AddMeter(HopeMeters.MeterName)
        .AddRuntimeInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithTracing(t => t
        .AddSource("Hope.Agent.Runtime")
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();
await app.RunAsync();
