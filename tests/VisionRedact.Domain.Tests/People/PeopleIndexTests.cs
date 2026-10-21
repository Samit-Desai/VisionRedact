using VisionRedact.Domain.People;

namespace VisionRedact.Domain.Tests.People;

public class PeopleIndexTests
{
    [Fact]
    public void Grouping_creates_numbered_people_with_suggested_tracks()
    {
        var index = PeopleIndex.Empty.ApplyGroups([[1, 4], [2]]);

        Assert.Equal(["Person 1", "Person 2"], index.Persons.Select(p => p.Name));
        Assert.Equal(index.PersonOf(1), index.PersonOf(4));
        Assert.NotEqual(index.PersonOf(1), index.PersonOf(2));
        Assert.All(index.Assignments, a => Assert.Equal(AssignmentSource.Suggested, a.Source));
    }

    [Fact]
    public void Regrouping_never_moves_confirmed_tracks()
    {
        var index = PeopleIndex.Empty.ApplyGroups([[1, 2], [3]]);
        var officer = index.PersonOf(1)!.Id;
        index = index.Rename(officer, "Officer Smith");

        // the matcher now (wrongly) thinks track 2 goes with track 3
        index = index.ApplyGroups([[1], [2, 3]]);

        Assert.Equal("Officer Smith", index.PersonOf(2)!.Name);
        Assert.True(index.AssignmentOf(2)!.Value.IsConfirmed);
    }

    [Fact]
    public void Regrouping_keeps_person_ids_and_numbers_stable()
    {
        var first = PeopleIndex.Empty.ApplyGroups([[1, 2], [3, 4]]);
        var person = first.PersonOf(3)!;

        var second = first.ApplyGroups([[3, 4, 5], [1, 2]]);

        Assert.Equal(person, second.PersonOf(5));
        Assert.Equal(3, second.NextNumber); // nobody new was created
    }

    [Fact]
    public void Rename_confirms_and_blank_restores_the_automatic_name()
    {
        var index = PeopleIndex.Empty.ApplyGroups([[7]]);
        var id = index.PersonOf(7)!.Id;

        var named = index.Rename(id, "  Witness  ");
        var unnamed = named.Rename(id, " ");

        Assert.Equal("Witness", named.Find(id)!.Name);
        Assert.True(named.AssignmentOf(7)!.Value.IsConfirmed);
        Assert.Equal("Person 1", unnamed.Find(id)!.Name);
    }

    [Fact]
    public void Merge_moves_every_track_confirms_them_and_removes_the_source()
    {
        var index = PeopleIndex.Empty.ApplyGroups([[1], [2, 3]]);
        var source = index.PersonOf(2)!.Id;
        var target = index.PersonOf(1)!.Id;

        var merged = index.Merge(source, target);

        Assert.Single(merged.Persons);
        Assert.Equal([1, 2, 3], merged.TracksOf(target));
        Assert.All(merged.Assignments, a => Assert.True(a.IsConfirmed));
        Assert.Null(merged.Find(source));
    }

    [Fact]
    public void Moving_a_track_out_creates_a_new_person_and_drops_emptied_ones()
    {
        var index = PeopleIndex.Empty.ApplyGroups([[1, 2], [3]]);
        var alone = index.PersonOf(3)!.Id;

        var split = index.MoveTrack(2, target: null);
        var joined = split.MoveTrack(3, split.PersonOf(1)!.Id);

        Assert.Equal("Person 3", split.PersonOf(2)!.Name);
        Assert.Null(joined.Find(alone)); // its only track left
        Assert.Equal(2, joined.Persons.Count);
    }

    [Fact]
    public void Index_is_immutable()
    {
        var before = PeopleIndex.Empty.ApplyGroups([[1]]);

        _ = before.Rename(before.PersonOf(1)!.Id, "Changed");

        Assert.Equal("Person 1", before.PersonOf(1)!.Name);
    }

    [Fact]
    public void Prune_forgets_deleted_tracks_and_their_empty_people()
    {
        var index = PeopleIndex.Empty.ApplyGroups([[1], [2]]);

        var pruned = index.Prune(new HashSet<int> { 1 });

        Assert.Single(pruned.Persons);
        Assert.Null(pruned.PersonOf(2));
    }

    [Fact]
    public void Restore_drops_dangling_data_and_keeps_numbers_unique()
    {
        var known = new Person(PersonId.New(), 4, "Anna");
        var empty = new Person(PersonId.New(), 9, null);

        var index = PeopleIndex.Restore(
            [known, empty],
            [new TrackAssignment(1, known.Id, AssignmentSource.Confirmed), new TrackAssignment(2, PersonId.New(), AssignmentSource.Suggested)],
            nextNumber: 2);

        Assert.Equal([known], index.Persons);
        Assert.Null(index.PersonOf(2));
        Assert.Equal(10, index.NextNumber); // never reuses a number that was handed out
    }
}
