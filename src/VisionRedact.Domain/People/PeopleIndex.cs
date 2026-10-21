namespace VisionRedact.Domain.People;

// who is who: the people, and which track belongs to whom.
// immutable - edits return a new index, so undo = go back to the old one, and readers need no lock (ADR-0022).
// grouping job -> ApplyGroups (suggested). People tab -> Rename / Merge / MoveTrack / Confirm (confirmed)
// TODO: per video only. matching people across the videos of a case would need a store above this (ADR-0022)
public sealed class PeopleIndex
{
    private readonly Dictionary<PersonId, Person> _persons;
    private readonly Dictionary<int, TrackAssignment> _assignments;

    private PeopleIndex(Dictionary<PersonId, Person> persons, Dictionary<int, TrackAssignment> assignments, int nextNumber)
    {
        _persons = persons;
        _assignments = assignments;
        NextNumber = nextNumber;
    }

    public static PeopleIndex Empty { get; } = new([], [], 1);

    // by Number = the order they were first found
    public IReadOnlyList<Person> Persons => _persons.Values.OrderBy(p => p.Number).ToArray();

    public IReadOnlyCollection<TrackAssignment> Assignments => _assignments.Values;

    // the N of the next "Person N"
    public int NextNumber { get; }

    public bool IsEmpty => _persons.Count == 0;

    // from a saved project. drops assignments to unknown people, and people w/o tracks
    public static PeopleIndex Restore(IEnumerable<Person> persons, IEnumerable<TrackAssignment> assignments, int nextNumber)
    {
        var byId = persons.ToDictionary(p => p.Id);
        var byTrack = assignments.Where(a => byId.ContainsKey(a.PersonId)).ToDictionary(a => a.TrackId);
        // never hand out a number that's taken, even if the saved NextNumber is too low
        var next = Math.Max(nextNumber, byId.Count == 0 ? 1 : byId.Values.Max(p => p.Number) + 1);
        return Build(byId, byTrack, next);
    }

    public Person? Find(PersonId id) => _persons.GetValueOrDefault(id);

    public TrackAssignment? AssignmentOf(int trackId) => _assignments.TryGetValue(trackId, out var a) ? a : null;

    public Person? PersonOf(int trackId) => _assignments.TryGetValue(trackId, out var a) ? _persons[a.PersonId] : null;

    public IReadOnlyList<int> TracksOf(PersonId id) =>
        _assignments.Values.Where(a => a.PersonId == id).Select(a => a.TrackId).Order().ToArray();

    // blank name = back to "Person N". naming someone means a human looked, so all their tracks get confirmed
    public PeopleIndex Rename(PersonId id, string? name)
    {
        var person = Require(id);
        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var persons = new Dictionary<PersonId, Person>(_persons) { [id] = person with { CustomName = trimmed } };
        return new PeopleIndex(persons, ConfirmAll(_assignments, id), NextNumber);
    }

    public PeopleIndex Confirm(PersonId id)
    {
        Require(id);
        return new PeopleIndex(_persons, ConfirmAll(_assignments, id), NextNumber);
    }

    // source's tracks go to target (confirmed), source disappears.
    // TrackIds don't change, only this map, so the box sliding (which follows TrackIds) isn't affected
    public PeopleIndex Merge(PersonId source, PersonId target)
    {
        Require(source);
        Require(target);
        if (source == target)
        {
            return this;
        }

        var assignments = _assignments.Values
            .Select(a => a.PersonId == source || a.PersonId == target ? a with { PersonId = target, Source = AssignmentSource.Confirmed } : a)
            .ToDictionary(a => a.TrackId);
        return Build(new Dictionary<PersonId, Person>(_persons), assignments, NextNumber);
    }

    // target null = "not this person" -> new person. the moved track is confirmed
    public PeopleIndex MoveTrack(int trackId, PersonId? target)
    {
        var persons = new Dictionary<PersonId, Person>(_persons);
        var next = NextNumber;
        PersonId destination;
        if (target is { } id)
        {
            Require(id);
            destination = id;
        }
        else
        {
            destination = PersonId.New();
            persons[destination] = new Person(destination, next++, null);
        }

        var assignments = new Dictionary<int, TrackAssignment>(_assignments)
        {
            [trackId] = new TrackAssignment(trackId, destination, AssignmentSource.Confirmed),
        };
        return Build(persons, assignments, next);
    }

    // auto grouping. each group = tracks that look like one person. confirmed tracks never move.
    // the rest join the person already confirmed in the group, else the one most of them had before
    // (keeps ids and names stable between runs), else a new person
    public PeopleIndex ApplyGroups(IEnumerable<IReadOnlyCollection<int>> groups)
    {
        var persons = new Dictionary<PersonId, Person>(_persons);
        var confirmed = _assignments.Values.Where(a => a.IsConfirmed).ToDictionary(a => a.TrackId);
        var assignments = new Dictionary<int, TrackAssignment>(confirmed);
        var next = NextNumber;
        // so two groups can't both take over the same old person in PreviousMajority
        var claimed = new HashSet<PersonId>();

        foreach (var group in groups)
        {
            var open = group.Where(t => !confirmed.ContainsKey(t)).ToArray();
            if (open.Length == 0)
            {
                continue;
            }

            var target = ConfirmedPersonIn(group, confirmed) ?? PreviousMajority(open, claimed);
            if (target is not { } person)
            {
                person = PersonId.New();
                persons[person] = new Person(person, next++, null);
            }

            claimed.Add(person);
            foreach (var track in open)
            {
                assignments[track] = new TrackAssignment(track, person, AssignmentSource.Suggested);
            }
        }

        return Build(persons, assignments, next);
    }

    // forget tracks that are gone (e.g. their detections were deleted)
    public PeopleIndex Prune(IReadOnlySet<int> liveTrackIds)
    {
        var assignments = _assignments.Values.Where(a => liveTrackIds.Contains(a.TrackId)).ToDictionary(a => a.TrackId);
        return assignments.Count == _assignments.Count ? this : Build(new Dictionary<PersonId, Person>(_persons), assignments, NextNumber);
    }

    private static Dictionary<int, TrackAssignment> ConfirmAll(Dictionary<int, TrackAssignment> assignments, PersonId id) =>
        assignments.Values.Select(a => a.PersonId == id ? a with { Source = AssignmentSource.Confirmed } : a).ToDictionary(a => a.TrackId);

    private Person Require(PersonId id) =>
        _persons.TryGetValue(id, out var person) ? person : throw new KeyNotFoundException($"Unknown person {id}.");

    private static PersonId? ConfirmedPersonIn(IEnumerable<int> group, Dictionary<int, TrackAssignment> confirmed) =>
        group.Select(t => confirmed.TryGetValue(t, out var a) ? a.PersonId : (PersonId?)null).FirstOrDefault(p => p is not null);

    private PersonId? PreviousMajority(IEnumerable<int> tracks, HashSet<PersonId> claimed) =>
        tracks.Select(t => _assignments.TryGetValue(t, out var a) ? a.PersonId : (PersonId?)null)
            .OfType<PersonId>()
            .Where(id => !claimed.Contains(id))
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .Select(g => (PersonId?)g.Key)
            .FirstOrDefault();

    // drops people with no tracks left, so every person always has at least one.
    // every edit that changes who owns a track goes through here (Rename/Confirm don't need to)
    private static PeopleIndex Build(Dictionary<PersonId, Person> persons, Dictionary<int, TrackAssignment> assignments, int nextNumber)
    {
        var used = assignments.Values.Select(a => a.PersonId).ToHashSet();
        foreach (var id in persons.Keys.Where(id => !used.Contains(id)).ToArray())
        {
            persons.Remove(id);
        }

        return new PeopleIndex(persons, assignments, nextNumber);
    }
}
