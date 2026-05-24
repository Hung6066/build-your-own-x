using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Channels;
using Hope.Agent.Application.Personalization;
using Hope.Agent.Application.SlashCommands;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Channels;

internal sealed class ChannelMessageRouter(
    IAgentRuntime runtime,
    ISlashCommandRouter slash,
    IUserPreferenceStore prefs,
    ILogger<ChannelMessageRouter> log) : IChannelMessageRouter
{
    public async Task<string> RouteAsync(InboundChannelMessage msg, CancellationToken ct)
    {
        var userId = DeriveAgentUserId(msg.Channel, msg.ExternalUserId);
        var corr = msg.CorrelationId ?? $"{msg.Channel}:{msg.ExternalChatId}";
        try
        {
            var slashResult = await slash.TryHandleAsync(userId, null, msg.Channel, msg.Text, ct);
            if (slashResult.Handled) return slashResult.Reply;

            var pref = await prefs.GetAsync(userId, ct);
            var profile = pref?.AgentProfile ?? msg.AgentProfile;

            var response = await runtime.RunAsync(
                new AgentRequest(userId, null, msg.Text, profile, corr),
                ct);
            return response.Reply;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Channel {Channel}: agent run failed for chat {ChatId}", msg.Channel, msg.ExternalChatId);
            return "Xin lỗi, có lỗi xảy ra. Vui lòng thử lại.";
        }
    }

    internal static Guid DeriveAgentUserId(string channel, string externalUserId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{channel}:{externalUserId}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

internal sealed class ChannelRegistry(IEnumerable<IExternalChannel> channels) : IChannelRegistry
{
    private readonly Dictionary<string, IExternalChannel> _byName =
        channels.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IExternalChannel> All => _byName.Values.ToList();
    public IExternalChannel? Find(string name) => _byName.GetValueOrDefault(name);
}
