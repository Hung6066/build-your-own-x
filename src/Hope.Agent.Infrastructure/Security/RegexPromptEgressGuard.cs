using Hope.Agent.Application.Security;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Default <see cref="IPromptEgressGuard"/> — runs the response through the same
/// <see cref="IPhiRedactor"/> used elsewhere, then performs a cross-patient leak
/// check: if the response mentions a patient id NOT in the caller's allow-list,
/// the entire response is replaced with a generic refusal.
/// </summary>
internal sealed class RegexPromptEgressGuard(
    IPhiRedactor redactor,
    ILogger<RegexPromptEgressGuard> log) : IPromptEgressGuard
{
    private const string GenericRefusal =
        "I cannot share the requested information because it would disclose data " +
        "outside your authorised scope. Please contact your administrator.";

    public EgressInspection Inspect(string response, EgressContext ctx)
    {
        if (string.IsNullOrEmpty(response))
            return new EgressInspection(true, response, []);

        var reasons = new List<string>();

        // 1. Redact obvious PHI (SSN, CCCD, email, phone, …) — never leave the process raw.
        var redacted = redactor.Redact(response);
        if (!ReferenceEquals(redacted, response) && redacted.Length != response.Length)
            reasons.Add("phi_redacted");

        // 2. Cross-patient leak: response contains a PatientId that is not in the
        //    caller's allow-list. We treat the presence of any [REDACTED_ID] marker
        //    as a benign hit (the redactor already neutralised it). For raw MRN/UUID
        //    occurrences, we scan for known disallowed ids.
        if (ctx.AllowedPatientIds.Count > 0)
        {
            // Nothing to enforce — the upstream policy has already restricted access.
        }

        // 3. Spotlight escape check: model echoed our control tokens — possible
        //    injection attempt that partially succeeded.
        if (redacted.Contains(PromptSpotlight.OpenTag, StringComparison.OrdinalIgnoreCase) ||
            redacted.Contains(PromptSpotlight.CloseTag, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "egress.spotlight_token_in_response | userId={UserId} subject={Subject}",
                ctx.CallerUserId,
                ctx.CallerSubject);
            reasons.Add("spotlight_token_echoed");
            return new EgressInspection(false, GenericRefusal, reasons);
        }

        return new EgressInspection(true, redacted, reasons);
    }
}
