using VisionRedact.Domain.People;

namespace VisionRedact.Domain.Tests.People;

public class FaceEmbeddingTests
{
    [Fact]
    public void Vectors_are_normalized_so_similarity_is_the_cosine()
    {
        var a = new FaceEmbedding("m", [3, 0]);
        var b = new FaceEmbedding("m", [1, 1]);

        Assert.Equal(1f, a.CosineSimilarity(a), 5);
        Assert.Equal(MathF.Sqrt(0.5f), a.CosineSimilarity(b), 5);
        Assert.Equal(-1f, a.CosineSimilarity(new FaceEmbedding("m", [-2, 0])), 5);
    }

    [Fact]
    public void Mean_points_between_its_inputs()
    {
        var mean = FaceEmbedding.Mean([new FaceEmbedding("m", [1, 0]), new FaceEmbedding("m", [0, 1])]);

        Assert.Equal(mean.Vector[0], mean.Vector[1], 5);
        Assert.Equal(1f, mean.CosineSimilarity(mean), 5);
    }

    [Fact]
    public void Embeddings_of_different_models_cannot_be_compared()
    {
        var arcFace = new FaceEmbedding("arcface", [1, 0]);
        var other = new FaceEmbedding("sface", [1, 0]);

        Assert.Throws<InvalidOperationException>(() => arcFace.CosineSimilarity(other));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(float.NaN, 1f)]
    public void Zero_or_invalid_vectors_are_rejected(float x, float y)
    {
        Assert.Throws<ArgumentException>(() => new FaceEmbedding("m", [x, y]));
    }
}
