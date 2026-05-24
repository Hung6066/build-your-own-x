using System.Text;
using Hope.Agent.Application.Compression;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Personalization;
using Hope.Agent.Application.SlashCommands;
using Hope.Agent.Application.UserModeling;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.SlashCommands;

internal sealed class SlashCommandRouter(
    IEnumerable<ISlashCommandHandler> handlers,
    ILogger<SlashCommandRouter> log) : ISlashCommandRouter
{
    private readonly Dictionary<string, ISlashCommandHandler> _byName =
        handlers.ToDictionary(h => h.Name.ToLowerInvariant(), h => h, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ISlashCommandHandler> Handlers => _byName.Values.ToList();

    public async Task<SlashCommandResult> TryHandleAsync(Guid userId, Guid? conversationId, string channel, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return SlashCommandResult.NotHandled;
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('/')) return SlashCommandResult.NotHandled;

        var body = trimmed[1..];
        var spaceIdx = body.IndexOf(' ');
        var cmd = (spaceIdx < 0 ? body : body[..spaceIdx]).Trim().ToLowerInvariant();
        var args = spaceIdx < 0 ? string.Empty : body[(spaceIdx + 1)..].Trim();
        if (cmd.Length == 0) return SlashCommandResult.NotHandled;

        if (!_byName.TryGetValue(cmd, out var handler))
        {
            var known = string.Join(", ", _byName.Keys.OrderBy(k => k).Select(k => "/" + k));
            return SlashCommandResult.Ok($"Unknown command /{cmd}. Available: {known}");
        }

        var ctx = new SlashCommandContext(userId, conversationId, cmd, args, text, channel);
        try
        {
            var result = await handler.ExecuteAsync(ctx, ct);
            HopeMeters.SlashCommandsExecuted.Add(1, new KeyValuePair<string, object?>("command", cmd));
            return result;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Slash command /{Cmd} failed", cmd);
            return SlashCommandResult.Ok($"Command /{cmd} failed: {ex.Message}");
        }
    }
}

internal sealed class HelpCommand(IServiceProvider sp) : ISlashCommandHandler
{
    public string Name => "help";
    public string Description => "List available slash commands.";
    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var sb = new StringBuilder("Available commands:\n");
        foreach (var h in sp.GetServices<ISlashCommandHandler>().OrderBy(h => h.Name))
            sb.Append('/').Append(h.Name).Append(" — ").AppendLine(h.Description);
        return Task.FromResult(SlashCommandResult.Ok(sb.ToString().TrimEnd()));
    }
}

internal sealed class PersonalityCommand(IUserPreferenceStore prefs) : ISlashCommandHandler
{
    public string Name => "personality";
    public string Description => "Switch the agent profile mid-conversation: /personality <name> (or 'reset').";
    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var arg = ctx.Arguments.Trim();
        if (arg.Length == 0)
        {
            var cur = await prefs.GetAsync(ctx.UserId, ct);
            return SlashCommandResult.Ok($"Current personality: {cur?.AgentProfile ?? "(default)"}.");
        }
        if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            await prefs.SetAgentProfileAsync(ctx.UserId, null, ct);
            return SlashCommandResult.Ok("Personality reset to default.");
        }
        await prefs.SetAgentProfileAsync(ctx.UserId, arg, ct);
        return SlashCommandResult.Ok($"Personality set to '{arg}'.");
    }
}

internal sealed class ModelCommand(IUserPreferenceStore prefs) : ISlashCommandHandler
{
    public string Name => "model";
    public string Description => "Set preferred model: /model <provider> <model> (or 'reset').";
    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var arg = ctx.Arguments.Trim();
        if (arg.Length == 0)
        {
            var cur = await prefs.GetAsync(ctx.UserId, ct);
            return SlashCommandResult.Ok($"Current model: {cur?.PreferredProvider ?? "(default)"} / {cur?.PreferredModel ?? "(default)"}.");
        }
        if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
        {
            await prefs.SetModelAsync(ctx.UserId, null, null, ct);
            return SlashCommandResult.Ok("Model preference cleared.");
        }
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var provider = parts[0];
        var model = parts.Length > 1 ? parts[1] : null;
        await prefs.SetModelAsync(ctx.UserId, provider, model, ct);
        return SlashCommandResult.Ok($"Model preference set: provider={provider}, model={model ?? "(default)"}");
    }
}

internal sealed class UndoCommand(AgentDbContext db) : ISlashCommandHandler
{
    public string Name => "undo";
    public string Description => "Delete the last assistant + user turn from the current conversation.";
    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        if (ctx.ConversationId is not { } convId)
            return SlashCommandResult.Ok("No active conversation to undo.");

        var tail = await db.Messages
            .Where(m => m.ConversationId == convId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(4)
            .ToListAsync(ct);
        if (tail.Count == 0) return SlashCommandResult.Ok("Nothing to undo.");

        var removed = 0;
        foreach (var m in tail)
        {
            if (m.Role == MessageRole.User || m.Role == MessageRole.Assistant || m.Role == MessageRole.Tool)
            {
                db.Messages.Remove(m);
                removed++;
            }
            if (m.Role == MessageRole.User) break;
        }
        await db.SaveChangesAsync(ct);
        return SlashCommandResult.Ok($"Undone: removed {removed} message(s).");
    }
}

internal sealed class CompressCommand(AgentDbContext db, IConversationCompressor compressor) : ISlashCommandHandler
{
    public string Name => "compress";
    public string Description => "Force-compress the current conversation now.";
    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        if (ctx.ConversationId is not { } convId)
            return SlashCommandResult.Ok("No active conversation to compress.");

        var conv = await db.Conversations.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == convId, ct);
        if (conv is null) return SlashCommandResult.Ok("Conversation not found.");

        var result = await compressor.MaybeCompressAsync(conv, ct);
        return result is null
            ? SlashCommandResult.Ok("Nothing to compress (below threshold or feature disabled).")
            : SlashCommandResult.Ok($"Compressed {result.CompressedMessageCount} message(s).");
    }
}

internal sealed class WhoamiCommand(IUserModelService userModel, IUserPreferenceStore prefs) : ISlashCommandHandler
{
    public string Name => "whoami";
    public string Description => "Show what the agent knows about you.";
    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var traits = await userModel.GetAsync(ctx.UserId, ct);
        var pref = await prefs.GetAsync(ctx.UserId, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"User: {ctx.UserId}");
        sb.AppendLine($"Profile: {pref?.AgentProfile ?? "(default)"}");
        sb.AppendLine($"Model: {pref?.PreferredProvider ?? "(default)"} / {pref?.PreferredModel ?? "(default)"}");
        if (traits is null || traits.IsEmpty)
            sb.AppendLine("Traits: (none extracted yet)");
        else
        {
            sb.AppendLine($"Role: {traits.Role ?? "?"}");
            sb.AppendLine($"Specialty: {traits.Specialty ?? "?"}");
            sb.AppendLine($"Style: {traits.CommunicationStyle ?? "?"}");
            sb.AppendLine($"Language: {traits.PreferredLanguage ?? "?"}");
        }
        return SlashCommandResult.Ok(sb.ToString().TrimEnd());
    }
}
