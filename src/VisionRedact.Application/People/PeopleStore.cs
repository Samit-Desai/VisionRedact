using VisionRedact.Domain.People;

namespace VisionRedact.Application.People;

// both versions, in case a listener wants to diff
public sealed class PeopleChangedEventArgs(PeopleIndex previous, PeopleIndex current) : EventArgs
{
    public PeopleIndex Previous { get; } = previous;

    public PeopleIndex Current { get; } = current;
}

// the open video's PeopleIndex + undo/redo. Reset on a new video (VideoWorkspace), Load on project open (ProjectService)
public interface IPeopleStore
{
    // raised on the editing thread, outside the lock. UI listeners must marshal
    event EventHandler<PeopleChangedEventArgs>? Changed;

    // immutable, so any thread can read it and keep it w/o a lock
    PeopleIndex Current { get; }

    bool CanUndo { get; }

    bool CanRedo { get; }

    // edit = old index -> new index. it runs under the lock on the NEWEST index and goes on the undo list,
    // so a rename made while IdentificationJob runs is never lost
    void Apply(Func<PeopleIndex, PeopleIndex> edit);

    bool Undo();

    bool Redo();

    // clears the undo history
    void Load(PeopleIndex index);

    // forget everyone, clears the undo history
    void Reset();
}

// undo by snapshots: the index is immutable, so an undo step is just "the index before".
// no inverse logic per edit that could get out of sync
public sealed class InMemoryPeopleStore : IPeopleStore
{
    private const int HistoryLimit = 100;

    // one lock for _current and both histories: an edit and its undo step change together
    private readonly object _gate = new();
    private readonly LinkedList<PeopleIndex> _undo = new(); // newest last. a LinkedList so the oldest can be dropped
    private readonly Stack<PeopleIndex> _redo = new();
    private PeopleIndex _current = PeopleIndex.Empty;

    public event EventHandler<PeopleChangedEventArgs>? Changed;

    public PeopleIndex Current => Volatile.Read(ref _current);

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count > 0;
            }
        }
    }

    public void Apply(Func<PeopleIndex, PeopleIndex> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        PeopleIndex previous, next;
        lock (_gate)
        {
            previous = _current;
            // careful: this runs under the lock. keep edits a pure index -> index function
            next = edit(previous);
            if (ReferenceEquals(next, previous))
            {
                return; // e.g. the person was already gone. no undo step for nothing
            }

            _undo.AddLast(previous);
            if (_undo.Count > HistoryLimit)
            {
                _undo.RemoveFirst();
            }

            _redo.Clear();
            Volatile.Write(ref _current, next);
        }

        Changed?.Invoke(this, new PeopleChangedEventArgs(previous, next));
    }

    public bool Undo() => Step(fromUndo: true);

    public bool Redo() => Step(fromUndo: false);

    public void Load(PeopleIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        Replace(index);
    }

    public void Reset() => Replace(PeopleIndex.Empty);

    // undo and redo mirror each other: take from one history, put the current index on the other
    private bool Step(bool fromUndo)
    {
        PeopleIndex previous, next;
        lock (_gate)
        {
            if (fromUndo ? _undo.Count == 0 : _redo.Count == 0)
            {
                return false;
            }

            previous = _current;
            if (fromUndo)
            {
                next = _undo.Last!.Value;
                _undo.RemoveLast();
                _redo.Push(previous);
            }
            else
            {
                next = _redo.Pop();
                _undo.AddLast(previous);
            }

            Volatile.Write(ref _current, next);
        }

        Changed?.Invoke(this, new PeopleChangedEventArgs(previous, next));
        return true;
    }

    private void Replace(PeopleIndex index)
    {
        PeopleIndex previous;
        lock (_gate)
        {
            previous = _current;
            _undo.Clear();
            _redo.Clear();
            Volatile.Write(ref _current, index);
        }

        Changed?.Invoke(this, new PeopleChangedEventArgs(previous, index));
    }
}
