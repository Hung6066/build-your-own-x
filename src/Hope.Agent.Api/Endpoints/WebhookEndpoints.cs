using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Workflows;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // No JWT required — external HIS/EMR systems authenticate via HMAC-SHA256 signature.
        var grp = app.MapGroup("/v1/webhooks").WithTags("Webhooks");

        grp.MapPost("/events", async (
            HttpContext http,
            [FromServices] IWorkflowDispatcher dispatcher,
            [FromServices] IOptions<WebhookOptions> webhookOpts,
            CancellationToken ct) =>
        {
            http.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            var bodyBytes = ms.ToArray();
            http.Request.Body.Position = 0;

            if (!ValidateHmacSignature(
                    bodyBytes,
                    webhookOpts.Value.Secret,
                    http.Request.Headers["X-Hope-Signature-256"].ToString()))
                return Results.Unauthorized();

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
    /// Constant-time HMAC-SHA256 validation. Expected header format: sha256=&lt;hex&gt;.
    /// Returns false when secret is unconfigured, ensuring all requests are rejected
    /// until a secret is explicitly set.
    /// </summary>
    private static bool ValidateHmacSignature(byte[] body, string secret, string header)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        if (!header.StartsWith("sha256=", StringComparison.Ordinal)) return false;

        var providedHex = header["sha256=".Length..];
        if (providedHex.Length != 64) return false; // SHA-256 hex is always 64 chars

        byte[] provided;
        try { provided = Convert.FromHexString(providedHex); }
        catch (FormatException) { return false; }

        var key = Encoding.UTF8.GetBytes(secret);
        var expected = HMACSHA256.HashData(key, body);

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
}
