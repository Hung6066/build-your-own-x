using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.Activities;
using Hope.Agent.Workflows.WorkflowsImpl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace Hope.Agent.Workflows;

public static class DependencyInjection
{
    public static IServiceCollection AddWorkflows(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TemporalOptions.SectionName);
        var options = section.Get<TemporalOptions>() ?? new TemporalOptions();
        services.AddSingleton(options);

        // Register ITemporalClient lazily so a missing Temporal server does not crash startup.
        // ConnectAsync is deferred to first resolution; if it fails, startup still succeeds.
        services.AddSingleton<ITemporalClient>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<TemporalWorkflowDispatcher>>();
            try
            {
                return TemporalClient.ConnectAsync(new TemporalClientConnectOptions(options.TargetHost)
                {
                    Namespace = options.Namespace,
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Temporal server unavailable at {Host}; workflow dispatch will be degraded until reconnected.", options.TargetHost);
                throw; // let callers handle unavailability; do not swallow so IWorkflowDispatcher can 503
            }
        });

        services.AddScoped<IWorkflowDispatcher, TemporalWorkflowDispatcher>();

        if (options.EnableWorker)
        {
            services
                .AddHostedTemporalWorker(options.TaskQueue)
                .AddScopedActivities<ClinicalActivities>()
                .AddWorkflow<PatientAdmissionWorkflow>()
                .AddWorkflow<EmergencyTriageWorkflow>()
                .AddWorkflow<AppointmentSchedulingWorkflow>()
                .AddWorkflow<MedicationReminderWorkflow>()
                .AddWorkflow<AuditReportWorkflow>();
        }

        return services;
    }
}
