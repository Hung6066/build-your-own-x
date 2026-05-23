using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.Activities;
using Hope.Agent.Workflows.WorkflowsImpl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;

namespace Hope.Agent.Workflows;

public static class DependencyInjection
{
    public static IServiceCollection AddWorkflows(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TemporalOptions.SectionName);
        var options = section.Get<TemporalOptions>() ?? new TemporalOptions();
        services.AddSingleton(options);

        services.AddTemporalClient(clientTargetHost: options.TargetHost, clientNamespace: options.Namespace);

        services.AddScoped<IWorkflowDispatcher, TemporalWorkflowDispatcher>();

        if (options.EnableWorker)
        {
            services
                .AddHostedTemporalWorker(options.TaskQueue)
                .AddScopedActivities<ClinicalActivities>()
                .AddWorkflow<PatientAdmissionWorkflow>()
                .AddWorkflow<EmergencyTriageWorkflow>();
        }

        return services;
    }
}
