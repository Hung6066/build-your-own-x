using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Security;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class EfAdversarialPatternStore(AgentDbContext db) : IAdversarialPatternStore
{
    public async Task<AdversarialPattern> ObserveAsync(string sample, string reason, CancellationToken ct)
    {
        var normalized = Normalize(sample);
        var signature = Signature(normalized);

        var existing = await db.AdversarialPatterns.FirstOrDefaultAsync(p => p.Signature == signature, ct);
        if (existing is null)
        {
            existing = new AdversarialPattern
            {
                Id = Guid.CreateVersion7(),
                Signature = signature,
                Sample = Truncate(sample, 512),
                Reason = Truncate(reason, 128),
                Hits = 1,
                Active = false,
                Confidence = 0.1,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            };
            db.AdversarialPatterns.Add(existing);
        }
        else
        {
            existing.Hits += 1;
            existing.LastSeen = DateTimeOffset.UtcNow;
            existing.Confidence = Math.Min(1.0, existing.Hits / 20.0);
        }
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<IReadOnlyList<AdversarialPattern>> ActivePatternsAsync(CancellationToken ct) =>
        await db.AdversarialPatterns.AsNoTracking().Where(p => p.Active).ToListAsync(ct);

    public async Task<IReadOnlyList<AdversarialPattern>> AllAsync(int take, CancellationToken ct) =>
        await db.AdversarialPatterns.AsNoTracking().OrderByDescending(p => p.Hits).Take(take).ToListAsync(ct);

    public async Task PromoteAsync(Guid id, CancellationToken ct)
    {
        var p = await db.AdversarialPatterns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return;
        p.Active = true;
        p.PromotedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DemoteAsync(Guid id, CancellationToken ct)
    {
        var p = await db.AdversarialPatterns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return;
        p.Active = false;
        await db.SaveChangesAsync(ct);
    }

    private static string Normalize(string s)
    {
        var lower = s.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch) || ch == ' ') sb.Append(ch);
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Signature(string normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
