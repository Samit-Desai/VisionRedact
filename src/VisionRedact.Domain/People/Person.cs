namespace VisionRedact.Domain.People;

// stays the same through renames and merges
public readonly record struct PersonId(Guid Value)
{
    public static PersonId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N")[..8];
}

// "Person 3" or a typed name. shown in the People tab and on the box labels in the player.
// Number is never reused, so auto names don't shift when someone gets merged away.
// take care here: no blur decision on the person. decisions stay on the detections, which export reads (ADR-0022)
public sealed record Person(PersonId Id, int Number, string? CustomName)
{
    /// <summary>The typed name, or "Person N".</summary>
    public string Name => CustomName ?? $"Person {Number}";

    public bool HasCustomName => CustomName is not null;
}

public enum AssignmentSource
{
    // from face matching, can change on the next grouping run
    Suggested = 0,

    // a human touched it (rename, merge, move, confirm). grouping never changes these
    Confirmed,
}

// "track 12 is Person 3" + whether a human confirmed it
public readonly record struct TrackAssignment(int TrackId, PersonId PersonId, AssignmentSource Source)
{
    public bool IsConfirmed => Source == AssignmentSource.Confirmed;
}
