using System.Reflection;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Serilog <see cref="IDestructuringPolicy"/> that scrubs PHI from any structured-log
/// object whose type lives in the Hope.Agent namespace hierarchy.
/// <para>
/// Whenever a developer writes <c>log.LogInformation("{@Request}", request)</c> with a
/// domain object, Serilog calls this policy to convert it to a <see cref="StructureValue"/>.
/// All <see langword="string"/> properties are passed through the embedded regex redactor
/// before being stored in the log event, so SSNs, emails, phone numbers, card numbers,
/// MRNs, and dates of birth are never written to any sink.
/// </para>
/// <para>
/// Non-string properties (GUIDs, numerics, enums, nested objects) are forwarded to the
/// Serilog <paramref name="propertyValueFactory"/> unchanged — nested Hope.Agent types
/// are recursively re-intercepted by this policy.
/// </para>
/// </summary>
internal sealed partial class PhiDestructuringPolicy : IDestructuringPolicy
{
    // ── Namespace scope ──────────────────────────────────────────────────────
    // Only types in the Hope.Agent namespace are intercepted — this avoids
    // inadvertent performance cost on third-party or framework types.
    private const string TargetNamespacePrefix = "Hope.Agent.";

    // ── Per-policy reflection cache ──────────────────────────────────────────
    // Keyed by Type so GetProperties() is only called once per type per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]>
        _propCache = new();

    // ── Regex patterns (identical to RegexPhiRedactor) ───────────────────────
    // Embedded here so this class has no DI/Infrastructure coupling —
    // the policy is instantiated during Host building, before DI resolves.

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

    // VN-specific PII — kept in lock-step with RegexPhiRedactor.
    [GeneratedRegex(@"(?<!\d)\d{12}(?!\d)")]
    private static partial Regex CccdRx();

    [GeneratedRegex(@"(?<!\d)\d{9}(?!\d)")]
    private static partial Regex CmndRx();

    [GeneratedRegex(@"\b[A-Z]{2}[ -]?\d[ -]?\d[ -]?\d{2}[ -]?\d{2}[ -]?\d{7}\b")]
    private static partial Regex BhytRx();

    [GeneratedRegex(@"(?:\+84|0)[\s.-]?(?:3|5|7|8|9)(?:[\s.-]?\d){8}")]
    private static partial Regex PhoneVnRx();

    // ── IDestructuringPolicy ─────────────────────────────────────────────────

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        var type = value.GetType();

        // Only intercept Hope.Agent.* types.
        if (type.Namespace is null || !type.Namespace.StartsWith(TargetNamespacePrefix, StringComparison.Ordinal))
        {
            result = null!;
            return false;
        }

        var properties = _propCache.GetOrAdd(
            type,
            t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                  .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                  .ToArray());

        var logProps = new List<LogEventProperty>(properties.Length);
        foreach (var prop in properties)
        {
            object? rawValue;
            try { rawValue = prop.GetValue(value); }
            catch { rawValue = null; }

            LogEventPropertyValue propValue;
            if (rawValue is string s)
            {
                // Scrub PHI from every string property value.
                propValue = new ScalarValue(Redact(s));
            }
            else
            {
                // Recursively destructure nested objects — non-strings pass through
                // unchanged but nested Hope.Agent types will re-trigger this policy.
                propValue = propertyValueFactory.CreatePropertyValue(rawValue, destructureObjects: true);
            }

            logProps.Add(new LogEventProperty(prop.Name, propValue));
        }

        // Use a short type tag (class name only) to avoid fully-qualified type leakage.
        result = new StructureValue(logProps, type.Name);
        return true;
    }

    // ── Redaction helpers ─────────────────────────────────────────────────────

    private static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var s = SsnRx().Replace(input, "[REDACTED_SSN]");
        s = EmailRx().Replace(s, "[REDACTED_EMAIL]");
        // VN patterns first — must beat the generic phone/card regex.
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
}
