using System.Text.RegularExpressions;
using Hope.Agent.Application.Security;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Regex-based PHI redactor covering common patterns: SSN, US/VN phone numbers,
/// email, credit-card-like sequences, generic MRN/Patient-ID labels, and dates of birth.
/// Replacement preserves length category but never the original value.
/// </summary>
internal sealed partial class RegexPhiRedactor : IPhiRedactor
{
    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var s = input;
        s = SsnRx().Replace(s, "[REDACTED_SSN]");
        s = EmailRx().Replace(s, "[REDACTED_EMAIL]");
        s = PhoneRx().Replace(s, "[REDACTED_PHONE]");
        s = CardRx().Replace(s, "[REDACTED_CARD]");
        s = MrnRx().Replace(s, "[REDACTED_ID]");
        s = DobRx().Replace(s, "[REDACTED_DOB]");
        return s;
    }

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SsnRx();

    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+\.[\w.-]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"\b(?:\+?\d{1,3}[\s.-]?)?(?:\(\d{2,4}\)[\s.-]?|\d{2,4}[\s.-])\d{3,4}[\s.-]?\d{3,4}\b")]
    private static partial Regex PhoneRx();

    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b")]
    private static partial Regex CardRx();

    [GeneratedRegex(@"\b(?:MRN|PatientId|Patient ID|BHYT|CCCD)[:\s#]*[\w-]{4,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex MrnRx();

    [GeneratedRegex(@"\b(?:0?[1-9]|[12]\d|3[01])[/-](?:0?[1-9]|1[0-2])[/-](?:19|20)\d{2}\b")]
    private static partial Regex DobRx();
}
