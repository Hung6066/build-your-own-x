using Hope.Agent.Application.Tasks;
using Hope.Agent.Domain.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hope.Agent.Api.Endpoints;

public static class KanbanEndpoints
{
    public static IEndpointRouteBuilder MapKanbanEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/kanban").RequireAuthorization().WithTags("Kanban");

        grp.MapGet("", async (
            IKanbanTaskStore store,
            Guid? userId,
            KanbanColumn? column,
            string? patientRef,
            string? assignedTo,
            int? take,
            CancellationToken ct) =>
        {
            var rows = await store.QueryAsync(new KanbanTaskFilter(userId, column, patientRef, assignedTo, take ?? 100), ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/{id:guid}", async (Guid id, IKanbanTaskStore store, CancellationToken ct) =>
        {
            var task = await store.GetAsync(id, ct);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        grp.MapPost("", async (KanbanCreateRequest req, IKanbanTaskStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "title required" });
            var created = await store.CreateAsync(new KanbanTask
            {
                UserId = req.UserId,
                ConversationId = req.ConversationId,
                PatientRef = req.PatientRef,
                Title = req.Title,
                Description = req.Description,
                Column = req.Column ?? KanbanColumn.Todo,
                Priority = req.Priority ?? KanbanPriority.Normal,
                DueAt = req.DueAt,
                AssignedTo = req.AssignedTo,
                Tags = req.Tags,
            }, ct);
            return Results.Created($"/v1/kanban/{created.Id}", created);
        });

        grp.MapPatch("/{id:guid}", async (Guid id, KanbanUpdateRequest req, IKanbanTaskStore store, CancellationToken ct) =>
        {
            var updated = await store.UpdateAsync(id, t =>
            {
                if (req.Title is not null) t.Title = req.Title;
                if (req.Description is not null) t.Description = req.Description;
                if (req.Column is KanbanColumn c) t.Column = c;
                if (req.Priority is KanbanPriority p) t.Priority = p;
                if (req.DueAt is DateTimeOffset d) t.DueAt = d;
                if (req.AssignedTo is not null) t.AssignedTo = req.AssignedTo;
                if (req.Tags is not null) t.Tags = req.Tags;
                if (req.PatientRef is not null) t.PatientRef = req.PatientRef;
            }, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        grp.MapDelete("/{id:guid}", async (Guid id, IKanbanTaskStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}

public sealed record KanbanCreateRequest(
    string Title,
    string? Description,
    Guid? UserId,
    Guid? ConversationId,
    string? PatientRef,
    KanbanColumn? Column,
    KanbanPriority? Priority,
    DateTimeOffset? DueAt,
    string? AssignedTo,
    string? Tags);

public sealed record KanbanUpdateRequest(
    string? Title,
    string? Description,
    KanbanColumn? Column,
    KanbanPriority? Priority,
    DateTimeOffset? DueAt,
    string? AssignedTo,
    string? Tags,
    string? PatientRef);
