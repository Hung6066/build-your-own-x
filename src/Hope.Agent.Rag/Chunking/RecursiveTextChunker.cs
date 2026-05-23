using System.Text;
using System.Text.RegularExpressions;

namespace Hope.Agent.Rag.Chunking;

public sealed record TextChunk(string Content, int Ordinal, int TokenEstimate, string? SectionPath);

/// <summary>
/// Recursive splitter inspired by LangChain RecursiveCharacterTextSplitter.
/// Splits on paragraph → sentence → word boundaries, keeping target size with overlap.
/// Token estimate uses ~4 chars/token heuristic (good enough for batching; real tokens come from the embedder).
/// </summary>
public sealed partial class RecursiveTextChunker(int chunkSize, int overlap)
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", "? ", "! ", "; ", ", ", " "];
    private static readonly Regex HeadingRegex = HeadingPattern();

    public IReadOnlyList<TextChunk> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sections = ExtractSections(normalized);
        var chunks = new List<TextChunk>();
        int ordinal = 0;
        foreach (var (path, body) in sections)
        {
            foreach (var piece in SplitRecursive(body, 0))
            {
                var clean = piece.Trim();
                if (clean.Length == 0) continue;
                chunks.Add(new TextChunk(clean, ordinal++, Math.Max(1, clean.Length / 4), path));
            }
        }
        return ApplyOverlap(chunks);
    }

    private IReadOnlyList<TextChunk> ApplyOverlap(List<TextChunk> chunks)
    {
        if (overlap <= 0 || chunks.Count < 2) return chunks;
        var result = new List<TextChunk>(chunks.Count) { chunks[0] };
        for (int i = 1; i < chunks.Count; i++)
        {
            var prev = result[^1].Content;
            var head = prev.Length <= overlap ? prev : prev[^overlap..];
            var merged = head + " " + chunks[i].Content;
            result.Add(chunks[i] with { Content = merged, TokenEstimate = Math.Max(1, merged.Length / 4) });
        }
        return result;
    }

    private IEnumerable<string> SplitRecursive(string text, int sepIdx)
    {
        if (text.Length <= chunkSize) { yield return text; yield break; }
        if (sepIdx >= Separators.Length)
        {
            for (int i = 0; i < text.Length; i += chunkSize)
                yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
            yield break;
        }
        var parts = text.Split(Separators[sepIdx], StringSplitOptions.None);
        var buf = new StringBuilder();
        foreach (var part in parts)
        {
            if (buf.Length + part.Length + 1 > chunkSize)
            {
                if (buf.Length > 0)
                {
                    if (buf.Length <= chunkSize) yield return buf.ToString();
                    else foreach (var s in SplitRecursive(buf.ToString(), sepIdx + 1)) yield return s;
                    buf.Clear();
                }
                if (part.Length > chunkSize)
                {
                    foreach (var s in SplitRecursive(part, sepIdx + 1)) yield return s;
                }
                else buf.Append(part);
            }
            else
            {
                if (buf.Length > 0) buf.Append(Separators[sepIdx]);
                buf.Append(part);
            }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    private static IEnumerable<(string? path, string body)> ExtractSections(string text)
    {
        var lines = text.Split('\n');
        var path = new List<string>();
        var current = new StringBuilder();
        string? currentPath = null;
        foreach (var line in lines)
        {
            var m = HeadingRegex.Match(line);
            if (m.Success)
            {
                if (current.Length > 0)
                {
                    yield return (currentPath, current.ToString());
                    current.Clear();
                }
                var depth = m.Groups[1].Value.Length;
                while (path.Count >= depth) path.RemoveAt(path.Count - 1);
                path.Add(m.Groups[2].Value.Trim());
                currentPath = string.Join(" / ", path);
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }
        if (current.Length > 0) yield return (currentPath, current.ToString());
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex HeadingPattern();
}
