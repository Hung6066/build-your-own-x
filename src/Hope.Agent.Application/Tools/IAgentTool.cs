using Hope.Agent.Application.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.Application.Tools;

public interface IAgentTool
{
    ToolDefinition Definition { get; }

    /// <summary>
    /// When <c>true</c>, identical (toolName, argumentsJson, userId) invocations may be served
    /// from <see cref="Hope.Agent.Application.Caching.IToolResultCache"/>. Default <c>false</c> —
    /// only enable on tools whose outputs depend solely on inputs (idempotent reads).
    /// </summary>
    bool IsCacheable => false;

    /// <summary>Time-to-live for cached results when <see cref="IsCacheable"/> is <c>true</c>.</summary>
    TimeSpan CacheTtl => TimeSpan.FromMinutes(15);

    Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct);
}

public sealed record ToolInvocationContext(
    Guid UserId,
    Guid ConversationId,
    string CorrelationId,
    IReadOnlyList<string>? Roles = null,
    Guid? TenantId = null,
    string? IdempotencyKey = null);

public interface IToolRegistry
{
    IReadOnlyList<IAgentTool> All { get; }
    IAgentTool? Find(string name);
    void Register(IAgentTool tool);
}

public interface IToolExecutor
{
    Task<string> InvokeAsync(IAgentTool tool, string argumentsJson, ToolInvocationContext context, CancellationToken ct);
}

/// <summary>
/// Nhóm đăng ký dịch vụ (tool hoặc role) thuộc về một workflow cụ thể.
/// <para>
/// Quy ước:
/// - Implement trong <c>Hope.Agent.Tools/Modules/</c> để đăng ký <see cref="IAgentTool"/>.
/// - Implement trong <c>Hope.Agent.MultiAgent/Modules/</c> để đăng ký <see cref="Hope.Agent.Application.Agents.Multi.IAgentRole"/>.
/// - Dùng cùng <see cref="WorkflowName"/> ở cả hai bên để thể hiện chúng thuộc một workflow.
/// </para>
/// <para>
/// Auto-discovery: <c>DependencyInjection.cs</c> của mỗi project tự tìm toàn bộ
/// implementation trong assembly và gọi <see cref="RegisterServices"/> — không cần
/// chỉnh sửa DI file khi thêm workflow mới, chỉ cần thêm class module mới.
/// </para>
/// </summary>
public interface IWorkflowModule
{
    /// <summary>
    /// Tên định danh workflow, ví dụ <c>"appointment-scheduling"</c>.
    /// Dùng để log khi khởi động và giúp lập trình viên trace mapping.
    /// </summary>
    string WorkflowName { get; }

    void RegisterServices(IServiceCollection services);
}
