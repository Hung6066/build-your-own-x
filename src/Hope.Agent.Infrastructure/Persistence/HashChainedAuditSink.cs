using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Domain.Audit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Persistence;

/// <summary>
/// Tamper-evident audit-sink decorator implementing a SHA-256 hash chain
/// (HIPAA § 164.312(b) — integrity of audit controls).
/// <para>
/// Each event's <c>PayloadJson</c> is wrapped in an envelope:
/// <code>{ "chain": { "prev": "...", "hash": "..." }, "data": &lt;original&gt; }</code>
/// where <c>hash = SHA-256(prev || canonical(data))</c>.
/// The chain head is stored in Redis under <c>audit:chain:head</c>.
/// </para>
/// <para>
/// To verify integrity, replay events in <c>OccurredAt</c> order and recompute
/// the chain — any mismatch indicates tampering.
/// </para>
/// </summary>
internal sealed class HashChainedAuditSink(
    IAuditSink inner,
    IConnectionMultiplexer redis,
    ILogger<HashChainedAuditSink> log) : IAuditSink
{
    private const string HeadKey = "audit:chain:head";
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
        // Property name casing is preserved — important for deterministic hashing.
    };

    public async Task WriteAsync(AuditEvent evt, CancellationToken ct)
    {
        var db = redis.GetDatabase();

        var prevHash = (string?)await db.StringGetAsync(HeadKey) ?? Genesis;

        var data = string.IsNullOrWhiteSpace(evt.PayloadJson) ? "{}" : evt.PayloadJson;
        var hashInput = prevHash + "|" + evt.Id.ToString("N") + "|" + data;
        var currHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();

        var envelope = JsonSerializer.Serialize(new
        {
            chain = new { prev = prevHash, hash = currHash, alg = "SHA-256" },
            data = JsonDocument.Parse(data).RootElement,
        }, CanonicalJson);

        var wrapped = new AuditEvent
        {
            Id = evt.Id,
            OccurredAt = evt.OccurredAt,
            UserId = evt.UserId,
            Actor = evt.Actor,
            Action = evt.Action,
            ResourceType = evt.ResourceType,
            ResourceId = evt.ResourceId,
            PatientId = evt.PatientId,
            CorrelationId = evt.CorrelationId,
            Reason = evt.Reason,
            PayloadJson = envelope,
        };

        await inner.WriteAsync(wrapped, ct);

        // Advance the head only after successful persistence.
        var advanced = await db.StringSetAsync(HeadKey, currHash);
        if (!advanced)
        {
            log.LogError(
                "audit.chain.head_advance_failed | id={Id} prev={Prev} curr={Curr}",
                evt.Id, prevHash, currHash);
        }
    }
}
