using System.Globalization;
using System.Text;
using Hope.Agent.Application.Abstractions;

namespace Hope.Agent.Infrastructure.Memory;

/// <summary>
/// Deterministic, dependency-free sparse encoder. Tokenises text into lowercased alphanumeric terms,
/// hashes each term into a fixed index space (FNV-1a mod <see cref="Dimensions"/>), and weights it with
/// a sub-linear BM25-style term frequency (<c>1 + ln(tf)</c>). Colliding indices have their weights
/// summed. Stable across processes so vectors written at upsert time match query-time vectors.
/// </summary>
public sealed class Bm25SparseEncoder : ISparseEncoder
{
    private const uint Dimensions = 1u << 20; // ~1M buckets — negligible collision rate for clinical vocab

    // Minimal English/Vietnamese stopword set: drop only high-frequency function words that add no
    // retrieval signal. Kept tiny on purpose so clinical terms, codes and names are never removed.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were",
        "be", "with", "as", "at", "by", "it", "this", "that", "i", "you", "he", "she", "they",
        "va", "la", "cua", "co", "khong", "cho", "voi", "den", "trong", "mot", "nhung", "da", "se",
    };

    public SparseVectorData Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SparseVectorData.Empty;

        var counts = new Dictionary<uint, int>();
        foreach (var term in Tokenize(text))
        {
            if (term.Length < 2 || Stopwords.Contains(term)) continue;
            var idx = Hash(term) % Dimensions;
            counts[idx] = counts.TryGetValue(idx, out var c) ? c + 1 : 1;
        }

        if (counts.Count == 0)
            return SparseVectorData.Empty;

        var indices = new uint[counts.Count];
        var values = new float[counts.Count];
        var i = 0;
        foreach (var (idx, tf) in counts)
        {
            indices[i] = idx;
            // Sub-linear TF saturation: repeated terms matter less than the first occurrence.
            values[i] = 1f + MathF.Log(tf);
            i++;
        }
        return new SparseVectorData(indices, values);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static uint Hash(string term)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var ch in term)
        {
            hash ^= ch;
            hash *= prime;
        }
        return hash;
    }
}
