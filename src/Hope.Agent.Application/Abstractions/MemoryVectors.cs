namespace Hope.Agent.Application.Abstractions;

/// <summary>
/// A sparse term vector (BM25-style) used for hybrid lexical retrieval alongside dense embeddings.
/// <paramref name="Indices"/> and <paramref name="Values"/> are parallel arrays of equal length;
/// each index is a hashed term id and each value its weighted term frequency.
/// </summary>
public readonly record struct SparseVectorData(uint[] Indices, float[] Values)
{
    public static readonly SparseVectorData Empty = new([], []);
    public bool IsEmpty => Indices.Length == 0;
}

/// <summary>
/// Encodes free text into a sparse lexical vector. Deterministic: the same text always produces
/// the same indices/values, so vectors written at upsert time match those built from a query.
/// </summary>
public interface ISparseEncoder
{
    SparseVectorData Encode(string text);
}
