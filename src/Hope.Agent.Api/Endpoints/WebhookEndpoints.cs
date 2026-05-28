using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Application.Workflows;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Hope.Agent.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // No JWT required — external HIS/EMR systems authenticate via HMAC-SHA256 signature.
        var grp = app.MapGroup("/v1/webhooks")
            .WithTags("Webhooks")
            .WithBodySizeLimit(256 * 1024)
            .WithRequestValidation();  // 256 KB — webhook events are HMAC-signed JSON blobs

        grp.MapPost("/events", async (
            HttpContext http,
            [FromServices] IWorkflowDispatcher dispatcher,
            [FromServices] IOptions<WebhookOptions> webhookOpts,
            [FromServices] IConnectionMultiplexer redis,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Hope.Agent.Webhooks");
            http.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            var bodyBytes = ms.ToArray();
            http.Request.Body.Position = 0;

            var opts = webhookOpts.Value;

            // ── Timestamp check (replay protection) ───────────────────────────────
            // Sender must include X-Hope-Timestamp: <unix-seconds>. Any request older
            // (or newer) than TimestampToleranceSeconds is rejected regardless of
            // whether the HMAC is valid, preventing captured-request replay attacks.
            var tsHeader = http.Request.Headers["X-Hope-Timestamp"].ToString();
            if (!long.TryParse(tsHeader, out var tsUnix))
                return Results.Unauthorized();

            var delta = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsUnix);
            if (delta > opts.TimestampToleranceSeconds)
                return Results.Unauthorized();

            // ── HMAC validation ───────────────────────────────────────────────────
            // Signed payload = "{timestamp}.{body}" — binds timestamp to signature
            // so replaying with a fresh timestamp breaks the HMAC.
            var signatureHeader = http.Request.Headers["X-Hope-Signature-256"].ToString();
            if (!ValidateHmacSignature(
                    bodyBytes,
                    tsHeader,
                    opts.Secret,
                    signatureHeader))
                return Results.Unauthorized();

            // ── Nonce dedup (defence-in-depth replay protection) ──────────────────
            // Even within the timestamp tolerance window, the same signed request
            // must not be processed twice. The signature itself is the natural nonce:
            // unique per body+timestamp and infeasible to forge without the secret.
            // TTL is set to 2× the timestamp tolerance so the key outlives any window
            // in which a replay could be considered valid.
            var nonceKey = $"seen-webhook:{signatureHeader["sha256=".Length..]}";
            var nonceTtl = TimeSpan.FromSeconds(Math.Max(60, opts.TimestampToleranceSeconds * 2));
            var firstSeen = await redis.GetDatabase().StringSetAsync(
                nonceKey, "1", nonceTtl, When.NotExists);
            if (!firstSeen)
            {
                log.LogWarning("webhook.replay_blocked sig={Sig} ts={Ts} ip={Ip}",
                    nonceKey, tsHeader, http.Connection.RemoteIpAddress);
                return Results.Unauthorized();
            }

            WebhookEventPayload? evt;
            try { evt = JsonSerializer.Deserialize<WebhookEventPayload>(bodyBytes, s_json); }
            catch { return Results.BadRequest(new { error = "Invalid JSON payload." }); }
            if (evt is null) return Results.BadRequest(new { error = "Empty payload." });

            return await RouteEventAsync(evt, dispatcher, http.TraceIdentifier, ct);
        });

        return app;
    }

    private static async Task<IResult> RouteEventAsync(
        WebhookEventPayload evt,
        IWorkflowDispatcher dispatcher,
        string correlationId,
        CancellationToken ct)
    {
        var p = evt.Payload ?? new Dictionary<string, string>();

        switch (evt.Event)
        {
            case "patient.emergency_admission":
                {
                    if (!Guid.TryParse(p.GetValueOrDefault("patient_id"), out var patientId))
                        return Results.BadRequest(new { error = "Missing or invalid patient_id." });

                    var input = new EmergencyTriageInput(
                        PatientId: patientId,
                        UserId: Guid.Empty,
                        Symptoms: p.GetValueOrDefault("symptoms") ?? "emergency admission",
                        Location: p.GetValueOrDefault("location"));

                    var res = await dispatcher.StartEmergencyTriageAsync(input, ct: ct);
                    return Results.Accepted(
                        $"/v1/workflows/{res.WorkflowId}",
                        new { res.WorkflowId, res.RunId, correlationId });
                }

            case "patient.admission":
                {
                    if (!Guid.TryParse(p.GetValueOrDefault("patient_id"), out var patientId))
                        return Results.BadRequest(new { error = "Missing or invalid patient_id." });

                    var input = new PatientAdmissionInput(
                        PatientId: patientId,
                        UserId: Guid.Empty,
                        ReasonForAdmission: p.GetValueOrDefault("reason") ?? "admission",
                        InsuranceProvider: p.GetValueOrDefault("insurance"),
                        PreferredDoctorId: p.GetValueOrDefault("doctor_id"),
                        Priority: int.TryParse(p.GetValueOrDefault("priority"), out var pri) ? pri : 3);

                    var res = await dispatcher.StartPatientAdmissionAsync(input, ct: ct);
                    return Results.Accepted(
                        $"/v1/workflows/{res.WorkflowId}",
                        new { res.WorkflowId, res.RunId, correlationId });
                }

            default:
                return Results.UnprocessableEntity(new { error = $"Unknown event type: '{evt.Event}'." });
        }
    }

    /// <summary>
    /// Constant-time HMAC-SHA256 validation with timestamp binding.
    /// Signed payload = "{timestamp}.{body}" — a replayed request with a modified
    /// timestamp breaks the HMAC; one with the original timestamp is rejected by the
    /// clock window check performed before this call.
    /// Expected header format: sha256=&lt;hex&gt;.
    /// Returns false when secret is unconfigured, ensuring all requests are rejected
    /// until a secret is explicitly set.
    /// </summary>
    private static bool ValidateHmacSignature(byte[] body, string timestamp, string secret, string header)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        if (!header.StartsWith("sha256=", StringComparison.Ordinal)) return false;

        var providedHex = header["sha256=".Length..];
        if (providedHex.Length != 64) return false; // SHA-256 hex is always 64 chars

        byte[] provided;
        try { provided = Convert.FromHexString(providedHex); }
        catch (FormatException) { return false; }

        // Build signed payload: timestamp bytes + '.' + body bytes
        var key = Encoding.UTF8.GetBytes(secret);
        var tsBytes = Encoding.UTF8.GetBytes(timestamp);
        var separator = "."u8.ToArray();
        var signedPayload = new byte[tsBytes.Length + separator.Length + body.Length];
        tsBytes.CopyTo(signedPayload, 0);
        separator.CopyTo(signedPayload, tsBytes.Length);
        body.CopyTo(signedPayload, tsBytes.Length + separator.Length);

        var expected = HMACSHA256.HashData(key, signedPayload);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
}

public sealed record WebhookEventPayload(
    string Event,
    Dictionary<string, string>? Payload = null);

public sealed class WebhookOptions
{
    public const string Section = "Webhook";
    /// <summary>
    /// HMAC-SHA256 shared secret for validating incoming webhook requests.
    /// Empty string disables all webhooks (safe default).
    /// </summary>
    public string Secret { get; init; } = "";

    /// <summary>
    /// Maximum age (and future skew) of the X-Hope-Timestamp header in seconds.
    /// Requests outside this window are rejected regardless of HMAC validity.
    /// Default: 300 s (5 minutes).
    /// </summary>
    public int TimestampToleranceSeconds { get; init; } = 300;
}
