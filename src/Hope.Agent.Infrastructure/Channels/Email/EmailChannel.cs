using System.Net;
using System.Net.Mail;
using Hope.Agent.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Channels.Email;

/// <summary>
/// Outbound SMTP email channel. Recipient id is the destination email address.
/// First line of the body before a blank line is used as the Subject; if no
/// blank line is present, <see cref="EmailOptions.DefaultSubject"/> is used.
/// </summary>
internal sealed class EmailChannel(
    IOptionsMonitor<EmailOptions> opts,
    ILogger<EmailChannel> log) : IExternalChannel
{
    public const string ChannelName = "email";
    public string Name => ChannelName;

    public async Task SendAsync(string recipientId, string text, CancellationToken ct)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.SmtpHost) || string.IsNullOrWhiteSpace(o.FromAddress))
        {
            log.LogDebug("Email channel disabled or not configured; skipping send to {Recipient}", recipientId);
            return;
        }

        var (subject, body) = SplitSubjectAndBody(text, o.DefaultSubject);

        using var smtp = new SmtpClient(o.SmtpHost, o.SmtpPort)
        {
            EnableSsl = o.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = Math.Max(1_000, o.TimeoutSeconds * 1_000),
        };
        if (!string.IsNullOrEmpty(o.Username))
            smtp.Credentials = new NetworkCredential(o.Username, o.Password);

        using var msg = new MailMessage(
            new MailAddress(o.FromAddress, o.FromDisplayName),
            new MailAddress(recipientId))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };

        try
        {
            await smtp.SendMailAsync(msg, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Email send to {Recipient} failed", recipientId);
        }
    }

    private static (string Subject, string Body) SplitSubjectAndBody(string text, string defaultSubject)
    {
        var idx = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (idx > 0 && idx <= 200)
        {
            return (text[..idx].Trim(), text[(idx + 2)..].TrimStart());
        }
        return (defaultSubject, text);
    }
}
