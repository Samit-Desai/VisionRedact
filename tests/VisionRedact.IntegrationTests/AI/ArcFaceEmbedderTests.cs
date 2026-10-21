using Emgu.CV;
using Emgu.CV.CvEnum;
using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Infrastructure.AI;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests.AI;

// real YuNet + ArcFace: no face = no fingerprint, the same person in nearby frames matches.
// TODO: all three tests return early and pass when a model or the demo video is missing - give them a skip
// attribute like RequiresModelFact
public sealed class ArcFaceEmbedderTests
{
    private static readonly ArcFaceEmbedderFactory Factory = new(ModelFiles.Options);

    [Fact]
    public void A_box_without_a_face_gives_no_fingerprint()
    {
        if (!Factory.IsAvailable)
        {
            return; // no face models (scripts/download-face-models.ps1), so nothing is checked
        }

        using var embedder = Factory.Create();
        using var blank = new Mat(480, 640, DepthType.Cv8U, 3);
        blank.SetTo(new Emgu.CV.Structure.MCvScalar(90, 90, 90));
        using var frame = new MatVideoFrame(blank, 0, TimeSpan.Zero);

        var result = embedder.Embed(frame, [new NormalizedRect(0.4, 0.4, 0.2, 0.2)]);

        Assert.Null(Assert.Single(result));
    }

    [Fact]
    public void The_same_person_in_nearby_frames_has_a_similar_fingerprint()
    {
        // Tears of Steel (scripts/download-demo-video.ps1): at 04:32 a close-up face is on screen for several seconds
        var video = DemoVideo.Path;
        if (!Factory.IsAvailable || video is null || !File.Exists(ModelFiles.YoloFace))
        {
            return; // a model or the demo video is missing, nothing checked
        }

        using var decoder = new EmguVideoDecoderFactory().Open(video);
        using var detector = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(OnnxModelCatalog.YoloFaceId), InferenceTarget.Cpu);
        using var embedder = Factory.Create();
        var start = decoder.Metadata.FrameAtTime(TimeSpan.FromSeconds(272));

        var fingerprints = new[] { start, start + 6 }.Select(index =>
        {
            decoder.Seek(index);
            using var frame = decoder.ReadNext()!;
            var largest = detector.Detect(frame).Where(c => c.Class == ObjectClass.Face).MaxBy(c => c.Box.Width * c.Box.Height);
            Assert.True(largest.Box.Width > 0, $"No face found on frame {index}.");
            return Assert.Single(embedder.Embed(frame, [largest.Box]));
        }).ToArray();

        Assert.All(fingerprints, Assert.NotNull);
        Assert.Equal(512, fingerprints[0]!.Dimensions);
        Assert.True(fingerprints[0]!.CosineSimilarity(fingerprints[1]!) > 0.5f, $"Similarity {fingerprints[0]!.CosineSimilarity(fingerprints[1]!):0.00}");
    }

    [Fact]
    public void All_three_built_in_detectors_find_the_close_up_face()
    {
        var video = DemoVideo.Path;
        var catalog = new OnnxModelCatalog(ModelFiles.Options).GetModels();
        if (video is null || catalog.Any(m => !m.IsAvailable))
        {
            return; // the demo video or a model is missing, nothing checked
        }

        using var decoder = new EmguVideoDecoderFactory().Open(video);
        decoder.Seek(decoder.Metadata.FrameAtTime(TimeSpan.FromSeconds(272)));
        using var frame = decoder.ReadNext()!;
        foreach (var model in catalog.Where(m => m.Id is OnnxModelCatalog.UltraFaceId or OnnxModelCatalog.YoloFaceId or OnnxModelCatalog.Yolo26FaceId))
        {
            using var detector = new OnnxDetectorFactory(ModelFiles.Options).Create(model, InferenceTarget.Cpu);
            Assert.True(detector.Detect(frame).Any(c => c.Class == ObjectClass.Face), $"{model.DisplayName} found no face.");
        }
    }

    private static class DemoVideo
    {
        // media/ is at the repo root, a few levels above the test binaries
        public static string? Path
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                {
                    var candidate = System.IO.Path.Combine(dir.FullName, "media", "tears_of_steel_1080p.mov");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return null;
            }
        }
    }
}
