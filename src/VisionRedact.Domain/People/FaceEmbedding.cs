namespace VisionRedact.Domain.People;

// face fingerprint, scaled to length 1 so cosine similarity = a plain dot product.
// ArcFaceEmbedder makes one per face shot, IdentificationJob takes the Mean per track, TrackClusterer compares those.
// biometric data: memory only, inside the grouping job, never saved to a project (ADR-0022)
public sealed class FaceEmbedding
{
    private readonly float[] _vector;

    /// <summary>Copies the vector and scales it to length 1.</summary>
    public FaceEmbedding(string modelId, ReadOnlySpan<float> vector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (vector.IsEmpty)
        {
            throw new ArgumentException("An embedding needs at least one value.", nameof(vector));
        }

        var length = MathF.Sqrt(Dot(vector, vector));
        // !(length > 0) catches NaN too
        if (!(length > 0) || float.IsInfinity(length))
        {
            throw new ArgumentException("An embedding must be a finite, non-zero vector.", nameof(vector));
        }

        _vector = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            _vector[i] = vector[i] / length;
        }

        ModelId = modelId;
    }

    // different models = different spaces, so never compare across them (see EnsureComparable).
    // don't forget: a new model means tuning SamePersonThreshold again. not calibrated on labeled footage yet
    public string ModelId { get; }

    public int Dimensions => _vector.Length;

    public ReadOnlySpan<float> Vector => _vector;

    // average direction of several shots of one person
    public static FaceEmbedding Mean(IReadOnlyList<FaceEmbedding> embeddings)
    {
        ArgumentOutOfRangeException.ThrowIfZero(embeddings.Count);
        var first = embeddings[0];
        var sum = new float[first.Dimensions];
        foreach (var embedding in embeddings)
        {
            first.EnsureComparable(embedding);
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += embedding._vector[i];
            }
        }

        // trick: no divide by count needed, the ctor scales the sum back to length 1 anyway
        return new FaceEmbedding(first.ModelId, sum);
    }

    /// <summary>-1..1, higher = more likely the same person.</summary>
    public float CosineSimilarity(FaceEmbedding other)
    {
        EnsureComparable(other);
        return Dot(_vector, other._vector);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private void EnsureComparable(FaceEmbedding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.ModelId != ModelId || other.Dimensions != Dimensions)
        {
            throw new InvalidOperationException($"Cannot compare embeddings of '{ModelId}' ({Dimensions}) and '{other.ModelId}' ({other.Dimensions}).");
        }
    }
}
