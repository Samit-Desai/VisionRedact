using VisionRedact.Application.Detections;
using VisionRedact.Application.People;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Projects;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.People;
using VisionRedact.Domain.Redaction;
using VisionRedact.Infrastructure.Persistence;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests;

public sealed class ProjectPersistenceTests : IClassFixture<SyntheticVideo>, IDisposable
{
    private readonly SyntheticVideo _video;
    private readonly string _directory = Directory.CreateTempSubdirectory("vr-project-").FullName;
    private readonly JsonProjectRepository _repository = new();

    public ProjectPersistenceTests(SyntheticVideo video)
    {
        _video = video;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Save_and_reload_round_trips_every_field()
    {
        var detection = new Detection(Guid.NewGuid(), 42, new NormalizedRect(0.1, 0.2, 0.3, 0.4), 0.87f, ObjectClass.Face, ReviewStatus.Rejected, TrackId: 7);
        var region = new RedactionRegion(Guid.NewGuid(), new NormalizedRect(0.5, 0.5, 0.2, 0.1), 10, 99, RedactionEffectKind.Pixelate, "badge");
        var document = new ProjectDocument(
            @"D:\videos\bodycam.mp4",
            new SourceFingerprint(123_456, 1920, 1080, 5_000, 29.97),
            ["YOLO11n face (accurate)"],
            [detection],
            [0, 5, 42],
            4,
            [region],
            new RedactionSettings(RedactionEffectKind.SolidBlack, 0.25, 0.6),
            new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(_directory, "case.vrproject");

        await _repository.SaveAsync(path, document);
        var loaded = await _repository.LoadAsync(path);

        Assert.Equal(document.SourcePath, loaded.SourcePath);
        Assert.Equal(document.Source, loaded.Source);
        Assert.Equal(document.Models, loaded.Models);
        Assert.Equal(detection, Assert.Single(loaded.Detections));
        Assert.Equal(document.AnalyzedFrames, loaded.AnalyzedFrames);
        Assert.Equal(4, loaded.HoldFrames);
        Assert.Equal(region, Assert.Single(loaded.Regions));
        Assert.Equal(document.Settings, loaded.Settings);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task People_round_trip_with_names_and_confirmation_but_no_biometric_data()
    {
        var people = PeopleIndex.Empty.ApplyGroups([[1, 2], [3]]);
        people = people.Rename(people.PersonOf(1)!.Id, "Officer Smith");
        var document = new ProjectDocument("v.mp4", new SourceFingerprint(1, 1, 1, 100, 30), [], [], [], 0, [], RedactionSettings.Default, DateTimeOffset.UtcNow)
        {
            People = people,
        };
        var path = Path.Combine(_directory, "people.vrproject");

        await _repository.SaveAsync(path, document);
        var loaded = (await _repository.LoadAsync(path)).People;
        var json = await File.ReadAllTextAsync(path);

        Assert.Equal(people.Persons, loaded.Persons);
        Assert.Equal(people.Assignments.OrderBy(a => a.TrackId), loaded.Assignments.OrderBy(a => a.TrackId));
        Assert.Equal(people.NextNumber, loaded.NextNumber);
        Assert.Contains("Officer Smith", json);
        Assert.DoesNotContain("embedding", json, StringComparison.OrdinalIgnoreCase); // fingerprints are never saved
    }

    [Fact]
    public async Task Schema_1_projects_load_with_nobody_identified()
    {
        // schema 2 added people, so old files must still open
        var path = Path.Combine(_directory, "v1.vrproject");
        await File.WriteAllTextAsync(path, """{"schemaVersion":1,"sourcePath":"x.mp4"}""");

        Assert.True((await _repository.LoadAsync(path)).People.IsEmpty);
    }

    [Fact]
    public async Task Fifty_thousand_detections_save_and_load_quickly()
    {
        var detections = Enumerable.Range(0, 50_000)
            .Select(i => Detection.Create(i / 2, new NormalizedRect(0.1, 0.1, 0.05, 0.05), 0.9f, ObjectClass.Face))
            .ToArray();
        var document = new ProjectDocument("v.mp4", new SourceFingerprint(1, 1, 1, 25_000, 30), [], detections, [], 0, [], RedactionSettings.Default, DateTimeOffset.UtcNow);
        var path = Path.Combine(_directory, "big.vrproject");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await _repository.SaveAsync(path, document);
        var loaded = await _repository.LoadAsync(path);

        Assert.Equal(50_000, loaded.Detections.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Round trip took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Corrupt_missing_and_future_files_are_reported_clearly()
    {
        var corrupt = Path.Combine(_directory, "corrupt.vrproject");
        await File.WriteAllTextAsync(corrupt, "{ not json");
        var future = Path.Combine(_directory, "future.vrproject");
        await File.WriteAllTextAsync(future, """{"schemaVersion": 99, "sourcePath": "x.mp4"}""");

        Assert.Equal(ProjectErrorKind.Corrupt, (await Assert.ThrowsAsync<ProjectException>(() => _repository.LoadAsync(corrupt))).Kind);
        Assert.Equal(ProjectErrorKind.UnsupportedVersion, (await Assert.ThrowsAsync<ProjectException>(() => _repository.LoadAsync(future))).Kind);
        Assert.Equal(ProjectErrorKind.NotFound, (await Assert.ThrowsAsync<ProjectException>(() => _repository.LoadAsync(Path.Combine(_directory, "none.vrproject")))).Kind);
    }

    [Fact]
    public async Task Hand_edited_out_of_range_boxes_are_clamped_on_load()
    {
        var path = Path.Combine(_directory, "edited.vrproject");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":1,"sourcePath":"x.mp4","detections":[{"id":"00000000-0000-0000-0000-000000000001","frame":3,"box":[-0.5,0.9,2,2],"confidence":0.9,"class":"Face","status":"Pending"}]}
            """);

        var loaded = await _repository.LoadAsync(path);

        var box = Assert.Single(loaded.Detections).Box;
        Assert.Equal(0, box.X);
        Assert.True(box.Right <= 1 && box.Bottom <= 1);
    }

    [Fact]
    public async Task Project_service_saves_reopens_and_relinks_moved_media()
    {
        var detections = new InMemoryDetectionRepository();
        var regions = new RedactionRegionStore();
        await using var workspace = new VideoWorkspace(new EmguVideoDecoderFactory(), new NullSink(), TimeProvider.System, detections: detections, regions: regions);
        var service = new ProjectService(_repository, workspace, detections, regions);

        var moved = Path.Combine(_directory, "moved.avi");
        File.Copy(_video.FilePath, moved);
        await workspace.OpenAsync(moved);
        detections.AddFrameResults(12, [Detection.Create(12, new NormalizedRect(0.2, 0.2, 0.1, 0.1), 0.8f, ObjectClass.Face)]);
        regions.Add(RedactionRegion.Create(new NormalizedRect(0, 0, 0.1, 0.1), 0, 30, RedactionEffectKind.SolidBlack));
        var project = Path.Combine(_directory, "p.vrproject");
        await service.SaveAsync(project, ["test"]);

        // move the video: opening the project must ask for it, then relink brings everything back
        await workspace.CloseAsync(); // release the decoder's file handle before moving the file
        var relocated = Path.Combine(_directory, "relocated.avi");
        File.Move(moved, relocated);
        var first = await service.OpenAsync(project);
        Assert.True(first.SourceMissing);

        var relinked = await service.RelinkAsync(first.Document, relocated);

        Assert.False(relinked.SourceMissing);
        Assert.False(relinked.SourceChanged); // same fingerprint
        Assert.Single(detections.GetForFrame(12));
        Assert.Single(regions.Regions);
        Assert.Equal(relocated, workspace.CurrentSession!.Metadata.SourcePath);
    }

    [Fact]
    public async Task Project_service_restores_people_and_drops_tracks_that_no_longer_exist()
    {
        var detections = new InMemoryDetectionRepository();
        var regions = new RedactionRegionStore();
        var people = new InMemoryPeopleStore();
        await using var workspace = new VideoWorkspace(new EmguVideoDecoderFactory(), new NullSink(), TimeProvider.System, detections: detections, regions: regions, people: people);
        var service = new ProjectService(_repository, workspace, detections, regions, people: people);
        await workspace.OpenAsync(_video.FilePath);
        detections.AddFrameResults(5, [Detection.Create(5, new NormalizedRect(0.2, 0.2, 0.1, 0.1), 0.8f, ObjectClass.Face) with { TrackId = 3 }]);
        people.Apply(i => i.ApplyGroups([[3], [99]])); // track 99 has no detections
        var id = people.Current.PersonOf(3)!.Id;
        people.Apply(i => i.Rename(id, "Witness"));
        var project = Path.Combine(_directory, "with-people.vrproject");
        await service.SaveAsync(project, []);

        await service.OpenAsync(project); // reopening resets the stores, then loads the project

        Assert.Equal("Witness", people.Current.PersonOf(3)!.Name);
        Assert.Null(people.Current.PersonOf(99));
        Assert.False(people.CanUndo); // a freshly opened project starts with a clean history
    }

    // stands in for the WPF presenter: drops every frame
    private sealed class NullSink : IPreviewFrameSink
    {
        public void Present(Application.Media.OwnedVideoFrame frame) => frame.Dispose();

        public void Clear()
        {
        }
    }
}
