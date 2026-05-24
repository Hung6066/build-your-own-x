using Hope.Agent.Application.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Hope.Agent.Infrastructure.Messaging;

/// <summary>
/// Telegram long-polling bot for clinical staff on mobile.
/// Uses Telegram.Bot v22 event-based polling (bot.OnMessage).
/// Only authorized chat IDs (configured via AllowedChatIds) can interact with the bot.
/// Each message is forwarded to IAgentRuntime and the reply is sent back to the chat.
/// </summary>
internal sealed class TelegramBotService(
    IServiceScopeFactory scopes,
    IOptions<TelegramBotOptions> opts,
    ILogger<TelegramBotService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = opts.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.BotToken))
        {
            log.LogInformation("Telegram bot is disabled or no token configured.");
            return;
        }

        if (options.AllowedChatIds.Length == 0)
            log.LogWarning("Telegram: AllowedChatIds is empty — all incoming messages will be rejected.");

        var bot = new TelegramBotClient(options.BotToken, cancellationToken: stoppingToken);

        bot.OnMessage += async (msg, _) =>
        {
            if (msg.Voice is { } voice)
            {
                await using var scope = scopes.CreateAsyncScope();
                var stt = scope.ServiceProvider.GetService<Hope.Agent.Application.Voice.ISpeechToText>();
                if (stt is null)
                {
                    try { await bot.SendMessage(msg.Chat.Id, "Voice messages are not enabled.", cancellationToken: stoppingToken); }
                    catch { /* best effort */ }
                    return;
                }
                try
                {
                    using var ms = new MemoryStream();
                    await bot.GetInfoAndDownloadFile(voice.FileId, ms, stoppingToken);
                    ms.Position = 0;
                    var tr = await stt.TranscribeAsync(ms, voice.MimeType ?? "audio/ogg", "vi", stoppingToken);
                    if (!string.IsNullOrWhiteSpace(tr.Text))
                        await HandleMessageAsync(bot, msg, tr.Text, options, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Telegram: voice transcription failed.");
                }
                return;
            }
            if (msg.Text is null) return;
            await HandleMessageAsync(bot, msg, msg.Text, options, stoppingToken);
        };

        var me = await bot.GetMe(stoppingToken);
        log.LogInformation("Telegram bot @{Username} (id={Id}) started polling.", me.Username, me.Id);

        // Block until the host signals shutdown; OperationCanceledException is swallowed here.
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* expected on shutdown */ }

        log.LogInformation("Telegram bot stopped.");
    }

    private async Task HandleMessageAsync(
        TelegramBotClient bot,
        Message msg,
        string text,
        TelegramBotOptions options,
        CancellationToken ct)
    {
        var chatId = msg.Chat.Id;

        if (!options.AllowedChatIds.Contains(chatId))
        {
            log.LogWarning("Telegram: rejected message from unauthorized chat {ChatId}.", chatId);
            try { await bot.SendMessage(chatId, "Unauthorized.", cancellationToken: ct); }
            catch { /* best effort */ }
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var runtime = scope.ServiceProvider.GetRequiredService<IAgentRuntime>();
            var slash = scope.ServiceProvider.GetRequiredService<Hope.Agent.Application.SlashCommands.ISlashCommandRouter>();
            var prefs = scope.ServiceProvider.GetRequiredService<Hope.Agent.Application.Personalization.IUserPreferenceStore>();

            // Map Telegram user ID to a deterministic Guid for agent runtime session tracking.
            var userId = msg.From?.Id is long tid ? DeriveAgentUserId(tid) : Guid.Empty;
            var corrId = $"tg:{msg.MessageId}:{chatId}";

            var slashResult = await slash.TryHandleAsync(userId, null, "telegram", text, ct);
            if (slashResult.Handled)
            {
                await bot.SendMessage(chatId, slashResult.Reply, cancellationToken: ct);
                return;
            }

            var pref = await prefs.GetAsync(userId, ct);
            var profile = pref?.AgentProfile ?? options.AgentProfile;

            var response = await runtime.RunAsync(
                new AgentRequest(userId, null, text, profile, corrId),
                ct);

            var reply = response.Reply.Length > options.MaxReplyLength
                ? string.Concat(response.Reply.AsSpan(0, options.MaxReplyLength), "\n…")
                : response.Reply;

            await bot.SendMessage(chatId, reply, cancellationToken: ct);
            log.LogInformation("Telegram: replied to {ChatId}. Tokens={Tok}.",
                chatId, response.PromptTokens + response.CompletionTokens);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Telegram: failed to handle message from {ChatId}.", chatId);
            try { await bot.SendMessage(chatId, "Xin lỗi, có lỗi xảy ra. Vui lòng thử lại.", cancellationToken: ct); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Maps a Telegram user ID deterministically to a Guid for agent runtime session tracking.</summary>
    private static Guid DeriveAgentUserId(long telegramUserId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"tg:{telegramUserId}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
