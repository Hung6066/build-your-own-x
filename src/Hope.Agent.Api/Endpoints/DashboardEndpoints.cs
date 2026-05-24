using Hope.Agent.Domain.Conversations;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Api.Endpoints;

/// <summary>Read-only admin aggregator endpoints designed to back a Blazor dashboard.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/dashboard").RequireAuthorization().WithTags("Dashboard");

        grp.MapGet("/overview", async (AgentDbContext db, CancellationToken ct) =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var convs7d = await db.Conversations.AsNoTracking().Where(c => c.CreatedAt >= since).CountAsync(ct);
            var msgs7d = await db.Messages.AsNoTracking().Where(m => m.CreatedAt >= since).CountAsync(ct);
            var pendingApprovals = await db.ToolApprovalRequests.AsNoTracking().CountAsync(t => t.Status == Domain.Security.ToolApprovalStatus.Pending, ct);
            var skills = await db.LearnedSkills.AsNoTracking().CountAsync(ct);
            var blockedPatterns = await db.AdversarialPatterns.AsNoTracking().CountAsync(p => p.Active, ct);
            return Results.Ok(new
            {
                window_days = 7,
                conversations_7d = convs7d,
                messages_7d = msgs7d,
                pending_approvals = pendingApprovals,
                learned_skills = skills,
                active_adversarial_patterns = blockedPatterns,
            });
        });

        grp.MapGet("/conversations", async (
            AgentDbContext db,
            Guid? userId,
            int? take,
            CancellationToken ct) =>
        {
            var q = db.Conversations.AsNoTracking();
            if (userId is Guid u) q = q.Where(c => c.UserId == u);
            var rows = await q.OrderByDescending(c => c.UpdatedAt)
                .Take(Math.Clamp(take ?? 50, 1, 200))
                .Select(c => new
                {
                    c.Id,
                    c.UserId,
                    c.Title,
                    c.UpdatedAt,
                    Messages = c.Messages.Count,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/conversations/{id:guid}", async (Guid id, AgentDbContext db, CancellationToken ct) =>
        {
            var conv = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => new
                {
                    c.Id,
                    c.UserId,
                    c.Title,
                    c.CreatedAt,
                    c.UpdatedAt,
                    Messages = c.Messages.OrderBy(m => m.CreatedAt).Select(m => new
                    {
                        m.Id,
                        m.Role,
                        m.Content,
                        m.ToolName,
                        m.CreatedAt,
                    }).ToList(),
                })
                .FirstOrDefaultAsync(ct);
            return conv is null ? Results.NotFound() : Results.Ok(conv);
        });

        grp.MapGet("/audit", async (AgentDbContext db, int? take, CancellationToken ct) =>
        {
            var rows = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(a => a.OccurredAt)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/skills", async (AgentDbContext db, int? take, CancellationToken ct) =>
        {
            var rows = await db.LearnedSkills.AsNoTracking()
                .OrderByDescending(s => s.LastUsed)
                .Take(Math.Clamp(take ?? 50, 1, 200))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        return app;
    }
}
