using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Redaction;

// the drawn regions + the blur settings of the open video.
// copy-on-write, no lock: regions are few, edits are rare, and the playback redaction task reads them every frame.
// writers: the UI (draw, move, Redaction tab), RegionTrackingJob, and Load/Clear when a video or project opens
public interface IRedactionRegionStore
{
    // raised on the writer's thread, not always the UI thread
    event EventHandler? Changed;

    // never changed after it's handed out
    IReadOnlyList<RedactionRegion> Regions { get; }

    RedactionSettings Settings { get; set; }

    void Add(RedactionRegion region);

    bool Update(RedactionRegion region);

    bool Remove(Guid id);

    void Load(IEnumerable<RedactionRegion> regions, RedactionSettings settings);

    void Clear();
}

public sealed class RedactionRegionStore : IRedactionRegionStore
{
    // both point to immutable objects. a change swaps the whole reference, never the contents
    private RedactionRegion[] _regions = [];
    private RedactionSettings _settings = RedactionSettings.Default;

    public event EventHandler? Changed;

    public IReadOnlyList<RedactionRegion> Regions => Volatile.Read(ref _regions);

    public RedactionSettings Settings
    {
        // one immutable record, so a Volatile swap is enough (last write wins)
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Add(RedactionRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        Mutate(current => [.. current, region]);
    }

    // swaps in the region w/ the same Id. false if it's gone (deleted meanwhile)
    public bool Update(RedactionRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Mutate(current => Array.FindIndex(current, r => r.Id == region.Id) is var i and >= 0
            ? [.. current[..i], region, .. current[(i + 1)..]]
            : null);
    }

    public bool Remove(Guid id) =>
        Mutate(current => current.Any(r => r.Id == id) ? current.Where(r => r.Id != id).ToArray() : null);

    // everything at once: a project opens (ProjectService), or a new video (Clear, from VideoWorkspace).
    // both run after ConfigureAwait(false), so usually on a pool thread
    public void Load(IEnumerable<RedactionRegion> regions, RedactionSettings settings)
    {
        // two separate writes: a reader can see new settings w/ old regions for a frame. harmless
        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _regions, regions.ToArray());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => Load([], RedactionSettings.Default);

    // null from change = nothing to do
    private bool Mutate(Func<RedactionRegion[], RedactionRegion[]?> change)
    {
        while (true)
        {
            // build a new array from the current one, the old one is never touched
            var current = Volatile.Read(ref _regions);
            var next = change(current);
            if (next is null)
            {
                return false;
            }

            // swap only if nobody published since we read it. else another writer won: loop and redo the change
            if (Interlocked.CompareExchange(ref _regions, next, current) == current)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
    }
}
