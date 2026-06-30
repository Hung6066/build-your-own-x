using Temporalio.Workflows;
using Temporalio.Common;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Workflows.WorkflowsImpl;

internal static class WorkflowCommon
{
    public static ActivityOptions DefaultActivityOptions(TimeSpan? startToCloseTimeout = null) => new()
    {
        StartToCloseTimeout = startToCloseTimeout ?? TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2.0F,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 5,
        },
    };

    public static void Step(List<string> stepLog, string status)
    {
        stepLog.Add(status);
        Workflow.Logger.LogInformation("Workflow step: {Status}", status);
    }
}
