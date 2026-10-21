using System.Text.Json;
using System.Text.Json.Serialization;
using VisionRedact.Application.Projects;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.People;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Infrastructure.Persistence;

// .vrproject = versioned JSON. ProjectService -> SaveAsync / LoadAsync -> ProjectDocument.
// DTOs on disk, not domain types: the file format has to survive domain refactors. enums as names, so reordering an
// enum can't silently change what a saved file means.
// streamed straight to/from the FileStream, never one giant string
public sealed class JsonProjectRepository : IProjectRepository
{
    // 2 added the people section. a newer file is refused w/ a clear message instead of half-read
    public const int SchemaVersion = 2;

    // one shared instance: System.Text.Json caches type metadata per options object
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    // write name.tmp, flush, rename over the target. a crash or a full disk mid-save leaves the old project as it was
    public async Task SaveAsync(string path, ProjectDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var temp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, ToDto(document), Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // closed and complete -> swap it in w/ one rename
            File.Move(temp, path, overwrite: true);
        }
        // TODO: only access denied is translated. any other IOException (disk full, file in use) escapes raw, and the
        // shell only catches ProjectException. same in LoadAsync. ok for now
        catch (UnauthorizedAccessException ex)
        {
            throw new ProjectException(ProjectErrorKind.AccessDenied, $"Cannot write '{path}': access denied.", ex);
        }
        finally
        {
            // failed before the rename -> drop the half-written temp
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public async Task<ProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new ProjectException(ProjectErrorKind.NotFound, $"The project '{path}' does not exist.");
        }

        // errors get a kind, so the UI can say "corrupt" or "access denied" instead of a raw exception
        ProjectDto? dto;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            dto = await JsonSerializer.DeserializeAsync<ProjectDto>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProjectException(ProjectErrorKind.Corrupt, $"'{Path.GetFileName(path)}' is not a valid VisionRedact project: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ProjectException(ProjectErrorKind.AccessDenied, $"Cannot read '{path}': access denied.", ex);
        }

        if (dto is null || dto.SourcePath is null)
        {
            throw new ProjectException(ProjectErrorKind.Corrupt, $"'{Path.GetFileName(path)}' is empty or incomplete.");
        }

        if (dto.SchemaVersion > SchemaVersion)
        {
            throw new ProjectException(ProjectErrorKind.UnsupportedVersion,
                $"'{Path.GetFileName(path)}' was saved by a newer version (schema {dto.SchemaVersion}); this version reads schema {SchemaVersion}.");
        }

        return FromDto(dto);
    }

    // boxes become compact [x, y, w, h] arrays
    private static ProjectDto ToDto(ProjectDocument d) => new()
    {
        SchemaVersion = SchemaVersion,
        SourcePath = d.SourcePath,
        Source = new SourceDto(d.Source.FileSizeBytes, d.Source.Width, d.Source.Height, d.Source.FrameCount, d.Source.FramesPerSecond),
        Models = [.. d.Models],
        Detections = d.Detections.Select(x => new DetectionDto(x.Id, x.FrameIndex, Box(x.Box), x.Confidence, x.Class, x.Status, x.TrackId)).ToList(),
        AnalyzedFrames = [.. d.AnalyzedFrames],
        HoldFrames = d.HoldFrames,
        Regions = d.Regions.Select(r => new RegionDto(r.Id, Box(r.Box), r.StartFrame, r.EndFrame, r.Effect, r.Label, r.Path?.Select(p => new TrackPointDto(p.Frame, Box(p.Box))).ToList())).ToList(),
        Settings = new SettingsDto(d.Settings.DetectionEffect, d.Settings.Padding, d.Settings.Strength),
        SavedAt = d.SavedAt,
        People = d.People.IsEmpty ? null : new PeopleDto(
            d.People.NextNumber,
            d.People.Persons.Select(p => new PersonDto(p.Id.Value, p.Number, p.CustomName)).ToList(),
            d.People.Assignments.OrderBy(a => a.TrackId).Select(a => new AssignmentDto(a.TrackId, a.PersonId.Value, a.Source)).ToList()),
    };

    // missing sections -> empty lists / defaults. tracked paths re-sorted by frame, RedactionRegion.BoxAt binary-searches them
    private static ProjectDocument FromDto(ProjectDto d) => new(
        d.SourcePath!,
        d.Source is { } s ? new SourceFingerprint(s.FileSizeBytes, s.Width, s.Height, s.FrameCount, s.FramesPerSecond) : new SourceFingerprint(0, 0, 0, 0, 0),
        d.Models ?? [],
        (d.Detections ?? []).Select(x => new Detection(x.Id, x.Frame, Rect(x.Box), x.Confidence, x.Class, x.Status, x.TrackId)).ToArray(),
        d.AnalyzedFrames ?? [],
        d.HoldFrames,
        (d.Regions ?? []).Select(r => new RedactionRegion(r.Id, Rect(r.Box), r.StartFrame, r.EndFrame, r.Effect, r.Label ?? string.Empty, r.Path?.OrderBy(p => p.Frame).Select(p => new TrackPoint(p.Frame, Rect(p.Box))).ToArray())).ToArray(),
        d.Settings is { } st ? new RedactionSettings(st.DetectionEffect, st.Padding, st.Strength) : RedactionSettings.Default,
        d.SavedAt)
    {
        // schema 1 has no people section -> nobody identified
        People = d.People is { } people
            ? PeopleIndex.Restore(
                (people.Persons ?? []).Select(p => new Person(new PersonId(p.Id), p.Number, p.Name)),
                (people.Assignments ?? []).Select(a => new TrackAssignment(a.Track, new PersonId(a.Person), a.Source)),
                people.NextNumber)
            : PeopleIndex.Empty,
    };

    // 6 decimals is far below a pixel even at 4K: smaller files, nothing visible lost
    private static double[] Box(NormalizedRect r) => [Math.Round(r.X, 6), Math.Round(r.Y, 6), Math.Round(r.Width, 6), Math.Round(r.Height, 6)];

    // the NormalizedRect ctor clamps again, so a hand-edited or broken box can't leave [0,1]
    private static NormalizedRect Rect(double[]? b) => b is { Length: 4 } ? new NormalizedRect(b[0], b[1], b[2], b[3]) : NormalizedRect.Empty;

    // ---- the file shape. private: only this class reads or writes it ----
    // nullable, so an older or hand-edited file w/ parts missing still loads
    private sealed class ProjectDto
    {
        public int SchemaVersion { get; set; }

        public string? SourcePath { get; set; }

        public SourceDto? Source { get; set; }

        public List<string>? Models { get; set; }

        public List<DetectionDto>? Detections { get; set; }

        public List<long>? AnalyzedFrames { get; set; }

        public int HoldFrames { get; set; }

        public List<RegionDto>? Regions { get; set; }

        public SettingsDto? Settings { get; set; }

        public DateTimeOffset SavedAt { get; set; }

        // since schema 2. names + track membership only, never face fingerprints or images (ADR-0022)
        public PeopleDto? People { get; set; }
    }

    private sealed record SourceDto(long FileSizeBytes, int Width, int Height, long FrameCount, double FramesPerSecond);

    private sealed record DetectionDto(Guid Id, long Frame, double[] Box, float Confidence, ObjectClass Class, ReviewStatus Status, int? TrackId);

    private sealed record RegionDto(Guid Id, double[] Box, long StartFrame, long EndFrame, RedactionEffectKind Effect, string? Label, List<TrackPointDto>? Path);

    private sealed record TrackPointDto(long Frame, double[] Box);

    private sealed record SettingsDto(RedactionEffectKind DetectionEffect, double Padding, double Strength);

    private sealed record PeopleDto(int NextNumber, List<PersonDto>? Persons, List<AssignmentDto>? Assignments);

    private sealed record PersonDto(Guid Id, int Number, string? Name);

    private sealed record AssignmentDto(int Track, Guid Person, AssignmentSource Source);
}
