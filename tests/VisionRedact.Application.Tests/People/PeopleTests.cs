using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.People;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Jobs;
using VisionRedact.Domain.People;

namespace VisionRedact.Application.Tests.People;

internal static class PeopleTestData
{
    public static Detection Face(int track, long frame, double x, float confidence = 0.9f, double size = 0.2) =>
        Detection.Create(frame, new NormalizedRect(x, 0.2, size, size), confidence, ObjectClass.Face) with { TrackId = track };

    public static FaceEmbedding Look(params float[] v) => new("test", v);
}

public class FaceShotSelectorTests
{
    private static readonly IdentityOptions Options = new() { ShotsPerTrack = 3, MinFacePixels = 20 };

    [Fact]
    public void Takes_the_best_face_of_each_part_of_the_track()
    {
        // 9 detections in 3 parts. the best of each part (area * confidence) is frame 1, 4 and 8
        var track = Enumerable.Range(0, 9).Select(f => PeopleTestData.Face(1, f, 0.1, confidence: f is 1 or 4 or 8 ? 0.95f : 0.5f)).ToArray();

        var shots = FaceShotSelector.Select(track, 1000, 1000, Options);

        Assert.Equal([1L, 4, 8], shots.Select(s => s.FrameIndex));
    }

    [Fact]
    public void Never_uses_rejected_tiny_untracked_or_non_face_detections()
    {
        Detection[] detections =
        [
            PeopleTestData.Face(1, 0, 0.1) with { Status = ReviewStatus.Rejected },
            PeopleTestData.Face(2, 0, 0.1, size: 0.01), // 10 px tall at 1000 px
            PeopleTestData.Face(3, 0, 0.1) with { TrackId = null },
            PeopleTestData.Face(4, 0, 0.1) with { Class = ObjectClass.Person },
        ];

        Assert.Empty(FaceShotSelector.Select(detections, 1000, 1000, Options));
    }
}

public class TrackClustererTests
{
    private static readonly LinkConstraints NoRules = new(new HashSet<(int, int)>(), []);

    [Fact]
    public void Similar_tracks_join_and_different_ones_stay_apart()
    {
        var tracks = new Dictionary<int, FaceEmbedding>
        {
            [1] = PeopleTestData.Look(1, 0.1f), [2] = PeopleTestData.Look(1, 0.15f), [3] = PeopleTestData.Look(0, 1),
        };

        var groups = TrackClusterer.Cluster(tracks, NoRules, threshold: 0.6f);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.SequenceEqual([1, 2]));
    }

    [Fact]
    public void Two_tracks_on_the_same_frame_are_never_one_person_even_if_they_look_alike()
    {
        // identical twins side by side
        Detection[] frame = [PeopleTestData.Face(1, 7, 0.1), PeopleTestData.Face(2, 7, 0.6)];
        var tracks = new Dictionary<int, FaceEmbedding> { [1] = PeopleTestData.Look(1, 0), [2] = PeopleTestData.Look(1, 0) };

        var groups = TrackClusterer.Cluster(tracks, LinkConstraints.Build(frame, PeopleIndex.Empty, 0.3), 0.6f);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Overlapping_boxes_on_one_frame_are_duplicates_and_may_join()
    {
        Detection[] frame = [PeopleTestData.Face(1, 7, 0.10), PeopleTestData.Face(2, 7, 0.11)];
        var tracks = new Dictionary<int, FaceEmbedding> { [1] = PeopleTestData.Look(1, 0), [2] = PeopleTestData.Look(1, 0) };

        var groups = TrackClusterer.Cluster(tracks, LinkConstraints.Build(frame, PeopleIndex.Empty, 0.3), 0.6f);

        Assert.Single(groups);
    }

    [Fact]
    public void Human_decisions_override_the_embeddings()
    {
        // 1 and 2 look nothing alike but a human confirmed them as one person. 3 looks like 1 but was confirmed
        // as somebody else
        var people = PeopleIndex.Empty.ApplyGroups([[1, 2], [3]]);
        people = people.Confirm(people.PersonOf(1)!.Id).Confirm(people.PersonOf(3)!.Id);
        var tracks = new Dictionary<int, FaceEmbedding>
        {
            [1] = PeopleTestData.Look(1, 0), [2] = PeopleTestData.Look(0, 1), [3] = PeopleTestData.Look(1, 0),
        };

        var groups = TrackClusterer.Cluster(tracks, LinkConstraints.Build([], people, 0.3), 0.6f);

        Assert.Contains(groups, g => g.SequenceEqual([1, 2]));
        Assert.Contains(groups, g => g.SequenceEqual([3]));
    }
}

public class InMemoryPeopleStoreTests
{
    [Fact]
    public void Edits_can_be_undone_and_redone()
    {
        var store = new InMemoryPeopleStore();
        store.Apply(i => i.ApplyGroups([[1]]));
        var id = store.Current.PersonOf(1)!.Id;
        store.Apply(i => i.Rename(id, "Anna"));

        Assert.True(store.Undo());
        Assert.Equal("Person 1", store.Current.PersonOf(1)!.Name);
        Assert.True(store.Redo());
        Assert.Equal("Anna", store.Current.PersonOf(1)!.Name);
    }

    [Fact]
    public void A_new_edit_clears_redo_and_load_clears_all_history()
    {
        var store = new InMemoryPeopleStore();
        store.Apply(i => i.ApplyGroups([[1]]));
        store.Undo();
        store.Apply(i => i.ApplyGroups([[2]]));
        Assert.False(store.CanRedo);

        store.Load(PeopleIndex.Empty);

        Assert.False(store.CanUndo);
    }

    [Fact]
    public void Changed_reports_both_versions()
    {
        var store = new InMemoryPeopleStore();
        PeopleChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;

        store.Apply(i => i.ApplyGroups([[1]]));

        Assert.True(seen!.Previous.IsEmpty);
        Assert.Same(store.Current, seen.Current);
    }
}

public class IdentificationJobTests
{
    [Fact]
    public async Task Tracks_of_the_same_face_at_different_times_become_one_person()
    {
        // track 1 (frames 0-9) and track 3 (frames 50-59) are the same face at x = 0.1. track 2 is somebody else
        // next to track 1
        var repository = new InMemoryDetectionRepository();
        repository.Reset(100);
        AddTrack(repository, track: 1, frames: 0..10, x: 0.1);
        AddTrack(repository, track: 2, frames: 0..10, x: 0.6);
        AddTrack(repository, track: 3, frames: 50..60, x: 0.1);
        var people = new InMemoryPeopleStore();
        var decoder = new FakeVideoDecoder(frameCount: 100);
        var job = new IdentificationJob(
            "video.mp4", new FakeVideoDecoderFactory(_ => decoder), new PositionEmbedderFactory(), repository, people,
            new IdentityOptions { MinFacePixels = 0, SamePersonThreshold = 0.6f });

        var manager = new ProcessingJobManager();
        manager.Start(job);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ProcessingJobState.Completed, job.State);
        Assert.Equal(people.Current.PersonOf(1), people.Current.PersonOf(3));
        Assert.NotEqual(people.Current.PersonOf(1), people.Current.PersonOf(2));
        Assert.Equal(2, people.Current.Persons.Count);
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    private static void AddTrack(InMemoryDetectionRepository repository, int track, Range frames, double x)
    {
        // AddFrameResults replaces a frame's pending results, so keep what's there and add the new face
        for (var f = frames.Start.Value; f < frames.End.Value; f++)
        {
            repository.AddFrameResults(f, [.. repository.GetForFrame(f), PeopleTestData.Face(track, f, x)]);
        }
    }

    // stands in for ArcFace: "recognizes" a face by where it stands (left half = one person, right half = another)
    private sealed class PositionEmbedderFactory : IFaceEmbedderFactory
    {
        public bool IsAvailable => true;

        public IFaceEmbedder Create() => new PositionEmbedder();
    }

    private sealed class PositionEmbedder : IFaceEmbedder
    {
        public string ModelId => "test";

        public IReadOnlyList<FaceEmbedding?> Embed(OwnedVideoFrame frame, IReadOnlyList<NormalizedRect> faces, CancellationToken cancellationToken = default) =>
            faces.Select(b => (FaceEmbedding?)(b.X < 0.5 ? PeopleTestData.Look(1, 0) : PeopleTestData.Look(0, 1))).ToArray();

        public void Dispose()
        {
        }
    }
}

public class PeopleServiceTests
{
    private readonly InMemoryDetectionRepository _detections = new();
    private readonly InMemoryPeopleStore _people = new();
    private readonly PeopleService _service;

    public PeopleServiceTests()
    {
        _detections.Reset(100);
        _detections.AddFrameResults(0, [PeopleTestData.Face(1, 0, 0.1), PeopleTestData.Face(2, 0, 0.6)]);
        _people.Apply(i => i.ApplyGroups([[1, 2]]));
        _service = new PeopleService(_people, _detections, new ProcessingJobManager(), new FakeVideoDecoderFactory(_ => new FakeVideoDecoder()), new UnavailableEmbedders());
    }

    [Fact]
    public void Keep_visible_never_unredacts_a_suggested_match()
    {
        var person = _people.Current.PersonOf(1)!.Id;
        _people.Apply(i => i.MoveTrack(1, person)); // a human confirmed track 1 only

        var changed = _service.KeepVisible(person);

        Assert.Equal(1, changed);
        Assert.False(_detections.GetForFrame(0).Single(d => d.TrackId == 1).IsRedacted);
        Assert.True(_detections.GetForFrame(0).Single(d => d.TrackId == 2).IsRedacted); // only suggested: stays blurred
        Assert.Equal((1, 1), _service.CountTracks(person));
    }

    [Fact]
    public void Redact_all_covers_every_track_of_the_person()
    {
        var changed = _service.RedactAll(_people.Current.PersonOf(1)!.Id);

        Assert.Equal(2, changed);
        Assert.All(_detections.GetForFrame(0), d => Assert.Equal(ReviewStatus.Approved, d.Status));
    }

    [Fact]
    public void Identification_needs_the_face_models()
    {
        Assert.False(_service.IsAvailable);
        Assert.Throws<InvalidOperationException>(() => _service.StartIdentification("video.mp4"));
    }

    // face models not installed
    private sealed class UnavailableEmbedders : IFaceEmbedderFactory
    {
        public bool IsAvailable => false;

        public IFaceEmbedder Create() => throw new InvalidOperationException();
    }
}
