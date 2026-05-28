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
        // VN-specific patterns are run BEFORE the generic phone/card regexes so the
        // more specific marker wins (e.g. a 12-digit CCCD must not be redacted as CARD).
        s = CccdRx().Replace(s, "[REDACTED_CCCD]");
        s = CmndRx().Replace(s, "[REDACTED_CMND]");
        s = BhytRx().Replace(s, "[REDACTED_BHYT]");
        s = PhoneVnRx().Replace(s, "[REDACTED_PHONE_VN]");
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

    // ── Vietnamese-specific PII patterns ───────────────────────────────────────

    /// <summary>Căn cước công dân — exactly 12 digits, not preceded/followed by digits.</summary>
    [GeneratedRegex(@"(?<!\d)\d{12}(?!\d)")]
    private static partial Regex CccdRx();

    /// <summary>CMND — legacy 9-digit national id.</summary>
    [GeneratedRegex(@"(?<!\d)\d{9}(?!\d)")]
    private static partial Regex CmndRx();

    /// <summary>BHYT card number — 2 uppercase letters + 13 digits (with optional separators).</summary>
    [GeneratedRegex(@"\b[A-Z]{2}[ -]?\d[ -]?\d[ -]?\d{2}[ -]?\d{2}[ -]?\d{7}\b")]
    private static partial Regex BhytRx();

    /// <summary>VN mobile: +84 or 0 prefix, then 3/5/7/8/9 lead, 8 trailing digits.</summary>
    [GeneratedRegex(@"(?:\+84|0)[\s.-]?(?:3|5|7|8|9)(?:[\s.-]?\d){8}")]
    private static partial Regex PhoneVnRx();
}
