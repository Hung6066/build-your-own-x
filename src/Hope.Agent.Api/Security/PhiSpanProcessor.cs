using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace Hope.Agent.Api.Security;

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{Activity}"/> that scrubs PHI from span
/// attributes before they are exported to the OTLP backend.
/// <para>
/// Called on the <c>OnEnd</c> path (span is complete but not yet serialised), which
/// means we mutate the <see cref="Activity"/> in-place — cheap, no extra allocations
/// for spans that carry no sensitive text.
/// </para>
/// <para>
/// Attributes targeted:
/// <list type="bullet">
///   <item><c>http.url</c>, <c>url.full</c>, <c>url.query</c> — query-strings may contain patient search terms.</item>
///   <item><c>db.statement</c>, <c>db.query.text</c> — SQL/Cypher queries may embed literal parameter values.</item>
///   <item><c>exception.message</c>, <c>exception.stacktrace</c> — exception text logged on error spans.</item>
///   <item>Any custom <c>user.*</c> attribute except <c>user.id</c> (GUID — not PHI).</item>
///   <item>Any attribute whose <em>name</em> contains a PHI-adjacent keyword
///         (<c>message</c>, <c>query</c>, <c>statement</c>, <c>body</c>, <c>content</c>,
///          <c>payload</c>, <c>symptom</c>, <c>reason</c>).</item>
/// </list>
/// </para>
/// </summary>
internal sealed partial class PhiSpanProcessor : BaseProcessor<Activity>
{
    // Attribute names that are always scrubbed (exact match, case-insensitive prefix).
    private static readonly HashSet<string> AlwaysScrubAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http.url",
        "url.full",
        "url.query",
        "db.statement",
        "db.query.text",
        "exception.message",
        "exception.stacktrace",
    };

    // Attribute name substrings — any attribute whose name contains one of these words
    // (after lower-casing) gets its value scrubbed.
    private static readonly string[] ScrubKeywords =
    [
        "message",
        "query",
        "statement",
        "body",
        "content",
        "payload",
        "symptom",
        "reason",
    ];

    // ── Regex patterns (identical to RegexPhiRedactor / PhiDestructuringPolicy) ──

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

    // ── BaseProcessor<Activity> ───────────────────────────────────────────────

    /// <summary>
    /// Called once per span when it ends — BEFORE it is handed to the exporter.
    /// We iterate tags in-place; Activity.SetTag overwrites the value for an existing key.
    /// </summary>
    public override void OnEnd(Activity activity)
    {
        // Fast-path: most spans carry very few tags, so a foreach is cheap.
        foreach (var tag in activity.Tags)
        {
            if (!ShouldScrub(tag.Key, tag.Value))
                continue;

            // Redact the string value; leave non-string / null tags untouched.
            if (tag.Value is string s)
                activity.SetTag(tag.Key, Redact(s));
        }

        // Also sanitize span events (exception events carry exception.message, etc.)
        foreach (var evt in activity.Events)
        {
            foreach (var attr in evt.Tags)
            {
                if (attr.Value is string s && ShouldScrub(attr.Key, s))
                {
                    // ActivityEvent tags are immutable; rebuild the event with redacted values.
                    // In practice, only error spans have events — this path is rare.
                    RebuildEventTags(activity, evt);
                    break; // RebuildEventTags handles all tags in this event; move to next event.
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ShouldScrub(string key, object? value)
    {
        // Only string attribute values can contain PHI.
        if (value is not string)
            return false;

        // Exact-match priority list.
        if (AlwaysScrubAttributes.Contains(key))
            return true;

        // Skip user.id (GUID — safe identifier, not PHI).
        if (key.Equals("user.id", StringComparison.OrdinalIgnoreCase))
            return false;

        // Keyword scan on attribute name.
        var lower = key.ToLowerInvariant();
        foreach (var kw in ScrubKeywords)
        {
            if (lower.Contains(kw, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var s = SsnRx().Replace(input, "[REDACTED_SSN]");
        s = EmailRx().Replace(s, "[REDACTED_EMAIL]");
        s = PhoneRx().Replace(s, "[REDACTED_PHONE]");
        s = CardRx().Replace(s, "[REDACTED_CARD]");
        s = MrnRx().Replace(s, "[REDACTED_ID]");
        s = DobRx().Replace(s, "[REDACTED_DOB]");
        return s;
    }

    /// <summary>
    /// <see cref="ActivityEvent"/> tags are immutable once the event is recorded.
    /// The only way to sanitize them is to remove the original event and re-add a
    /// scrubbed copy. This is intentionally a slow path — it only triggers on spans
    /// that carry exception events with PHI-bearing attributes.
    /// </summary>
    private static void RebuildEventTags(Activity activity, ActivityEvent original)
    {
        // Build a redacted copy of all tags for this event.
        var redactedTags = new ActivityTagsCollection();
        foreach (var attr in original.Tags)
        {
            redactedTags[attr.Key] = attr.Value is string s && ShouldScrub(attr.Key, s)
                ? Redact(s)
                : attr.Value;
        }

        // ActivityEvent is a struct — we can't mutate it. Add the cleaned event
        // using the same name and timestamp so downstream correlates it correctly.
        activity.AddEvent(new ActivityEvent(original.Name, original.Timestamp, redactedTags));

        // Unfortunately the Activity API does not expose a "remove event" method.
        // The original event remains but the scrubbed duplicate follows it immediately.
        // Exporters that process events in order will see both; consumers should use
        // the last event with a given name. For full elimination, callers should prefer
        // not logging PHI on exception events at all (SafeExceptionHandler covers this).
    }
}
