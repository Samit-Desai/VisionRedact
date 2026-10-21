using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Detections;

// the live box store, all in memory behind one lock. saving to disk is ProjectService's job.
// detections by id + id lists per frame + a bit set of scanned frames (1 bit per frame, tiny next to a HashSet<long>).
// export never reads the live one: RedactionSnapshot Loads a private copy, so review edits can't change a running export
public sealed class InMemoryDetectionRepository : IDetectionRepository
{
    private static readonly IReadOnlyList<Detection> None = [];

    // one lock for _byId, _byFrame and _analyzed, so nobody ever sees them half updated.
    // a plain lock is fine: the work inside is short and never awaits
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Detection> _byId = [];
    private readonly Dictionary<long, List<Guid>> _byFrame = [];
    private ulong[] _analyzed = []; // bit f = frame f was scanned
    private long _frameCount;
    private int _highestTrackId; // only goes up until Reset/Load. under _gate

    // read w/o the lock, so Interlocked
    private long _analyzedCount;
    private long _version;

    // set by AnalysisJob / Load, read by the playback and UI threads. Volatile is enough for one int
    private int _holdFrames;

    public event EventHandler<DetectionsChangedEventArgs>? Changed;

    public long Version => Interlocked.Read(ref _version);

    public int Count
    {
        get
        {
            // Dictionary isn't safe to read during a write, not even Count
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    public long AnalyzedFrameCount => Interlocked.Read(ref _analyzedCount);

    public int HighestTrackId
    {
        get
        {
            lock (_gate)
            {
                return _highestTrackId;
            }
        }
    }

    public int HoldFrames
    {
        get => Volatile.Read(ref _holdFrames);
        set => Volatile.Write(ref _holdFrames, Math.Max(0, value));
    }

    public void Reset(long frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        lock (_gate)
        {
            ClearUnsafe(frameCount);
        }

        HoldFrames = 0;
        Raise(new DetectionsChangedEventArgs(None, None, [], isReset: true));
    }

    public void Load(long frameCount, IEnumerable<Detection> detections, IEnumerable<long> analyzedFrames, int holdFrames)
    {
        lock (_gate)
        {
            ClearUnsafe(frameCount);
            foreach (var frame in analyzedFrames)
            {
                MarkAnalyzedUnsafe(frame);
            }

            foreach (var detection in detections)
            {
                AddUnsafe(detection);
            }
        }

        HoldFrames = holdFrames;
        Raise(new DetectionsChangedEventArgs(None, None, [], isReset: true));
    }

    public void AddFrameResults(long frameIndex, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);
        var removed = new List<Guid>();
        // the AI task calls this while the UI may be reading or reviewing the same frame
        lock (_gate)
        {
            // drop the old boxes on this frame that nobody reviewed yet (Pending). reviewed ones stay
            if (_byFrame.TryGetValue(frameIndex, out var existing))
            {
                foreach (var id in existing.ToArray())
                {
                    if (_byId[id].Status == ReviewStatus.Pending)
                    {
                        RemoveUnsafe(id);
                        removed.Add(id);
                    }
                }
            }

            // add the new ones and mark the frame scanned, even with 0 boxes ("checked, nothing there")
            foreach (var detection in detections)
            {
                if (detection.FrameIndex != frameIndex)
                {
                    throw new ArgumentException("All detections must belong to the given frame.", nameof(detections));
                }

                AddUnsafe(detection);
            }

            MarkAnalyzedUnsafe(frameIndex);
        }

        // raise outside the lock, so a slow handler can't block everyone else on _gate
        if (detections.Count > 0 || removed.Count > 0)
        {
            Raise(new DetectionsChangedEventArgs(detections, None, removed, isReset: false));
        }
        else
        {
            Interlocked.Increment(ref _version); // no box changed, but the coverage did
        }
    }

    public Detection? Get(Guid id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<Detection> GetForFrame(long frameIndex)
    {
        lock (_gate)
        {
            return GetForFrameUnsafe(frameIndex);
        }
    }

    // called per shown frame (preview blur + overlay) and per exported frame (on export's copy).
    // so preview and export always get the same boxes
    public IReadOnlyList<Detection> GetForDisplay(long frameIndex)
    {
        var hold = HoldFrames;
        // the AI task may be adding the very frames we're about to look at
        lock (_gate)
        {
            // walk back, at most HoldFrames, to the nearest scanned frame
            for (var f = frameIndex; f >= 0 && f >= frameIndex - hold; f--)
            {
                if (IsAnalyzedUnsafe(f))
                {
                    // this frame itself was scanned -> as stored. an earlier one -> its boxes, slid toward the next scan
                    var held = GetForFrameUnsafe(f);
                    return f == frameIndex || held.Count == 0 ? held : InterpolateTowardsNextUnsafe(held, f, frameIndex, hold);
                }
            }

            // no scanned frame close enough -> nothing to draw or blur (RedactionCoverage counts these frames)
            return None;
        }
    }

    // slides each held box toward the same track (and class) on the next scan, so a moving face stays in its blur.
    // no match there (face gone, model missed it) -> the box stays where it was, so nothing blurred stops being blurred.
    // id, status and confidence stay those of the earlier box
    private IReadOnlyList<Detection> InterpolateTowardsNextUnsafe(IReadOnlyList<Detection> held, long previousFrame, long frameIndex, int hold)
    {
        // the next scan is at most one interval away. none yet (scan still running) -> plain hold
        var limit = previousFrame + hold + 1;
        long nextFrame = -1;
        for (var f = frameIndex + 1; f <= limit; f++)
        {
            if (IsAnalyzedUnsafe(f))
            {
                nextFrame = f;
                break;
            }
        }

        if (nextFrame < 0 || !_byFrame.TryGetValue(nextFrame, out var nextIds))
        {
            return held;
        }

        // t = 0 at the previous scan, 1 at the next
        var t = (double)(frameIndex - previousFrame) / (nextFrame - previousFrame);
        var result = new Detection[held.Count];
        for (var i = 0; i < held.Count; i++)
        {
            var current = held[i];
            result[i] = current;
            if (current.TrackId is not { } track)
            {
                continue; // no track (e.g. Detect this frame): nothing to match, it stays held
            }

            foreach (var id in nextIds)
            {
                var next = _byId[id];
                if (next.TrackId == track && next.Class == current.Class)
                {
                    result[i] = current with { Box = NormalizedRect.Lerp(current.Box, next.Box, t) };
                    break;
                }
            }
        }

        return result;
    }

    public IReadOnlyList<Detection> Snapshot()
    {
        // one consistent copy. after this the caller (export, save, People) needs no lock
        lock (_gate)
        {
            var all = new List<Detection>(_byId.Count);
            all.AddRange(_byId.Values);
            all.Sort(static (a, b) =>
            {
                var byFrame = a.FrameIndex.CompareTo(b.FrameIndex);
                return byFrame != 0 ? byFrame : b.Confidence.CompareTo(a.Confidence);
            });
            return all;
        }
    }

    public IReadOnlyList<long> AnalyzedFrames()
    {
        lock (_gate)
        {
            var frames = new List<long>((int)Math.Min(_analyzedCount, int.MaxValue));
            for (long f = 0; f < _frameCount; f++)
            {
                if (IsAnalyzedUnsafe(f))
                {
                    frames.Add(f);
                }
            }

            return frames;
        }
    }

    // which frames have AI boxes to rely on. same rule as GetForDisplay: a scanned frame at most HoldFrames back.
    // only RedactionSnapshot calls it, for the export warning. one pass over the bit set under the lock.
    // maxListedGaps = how many gaps to list, all of them are counted
    public RedactionCoverage GetCoverage(int maxListedGaps = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxListedGaps);
        var hold = HoldFrames;
        lock (_gate)
        {
            var gaps = new List<FrameRange>();
            long covered = 0, scanned = 0, gapCount = 0, gapStart = -1, lastAnalyzed = -1;
            var anyAnalyzed = false;
            for (long f = 0; f < _frameCount; f++)
            {
                if (IsAnalyzedUnsafe(f))
                {
                    lastAnalyzed = f;
                    anyAnalyzed = true;
                    scanned++;
                }

                if (anyAnalyzed && f - lastAnalyzed <= hold)
                {
                    covered++;
                    if (gapStart >= 0)
                    {
                        CloseGap(gapStart, f - 1);
                        gapStart = -1;
                    }
                }
                else if (gapStart < 0)
                {
                    gapStart = f;
                }
            }

            if (gapStart >= 0)
            {
                CloseGap(gapStart, _frameCount - 1);
            }

            return new RedactionCoverage(_frameCount, covered, gaps, gapCount, scanned);

            void CloseGap(long first, long last)
            {
                gapCount++;
                if (gaps.Count < maxListedGaps)
                {
                    gaps.Add(new FrameRange(first, last));
                }
            }
        }
    }

    public bool UpdateStatus(Guid id, ReviewStatus status)
    {
        Detection updated;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var current) || current.Status == status)
            {
                return false;
            }

            // swap in a changed copy. lists handed out earlier keep the old one
            updated = current with { Status = status };
            _byId[id] = updated;
        }

        Raise(new DetectionsChangedEventArgs(None, [updated], [], isReset: false));
        return true;
    }

    public int UpdateStatusWhere(Func<Detection, bool> predicate, ReviewStatus status)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var updated = new List<Detection>();
        lock (_gate)
        {
            // ToArray first, we write to _byId inside the loop
            foreach (var detection in _byId.Values.Where(d => d.Status != status && predicate(d)).ToArray())
            {
                var changed = detection with { Status = status };
                _byId[detection.Id] = changed;
                updated.Add(changed);
            }
        }

        if (updated.Count > 0)
        {
            Raise(new DetectionsChangedEventArgs(None, updated, [], isReset: false));
        }

        return updated.Count;
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (!RemoveUnsafe(id))
            {
                return false;
            }
        }

        Raise(new DetectionsChangedEventArgs(None, None, [id], isReset: false));
        return true;
    }

    // ...Unsafe = the caller already holds _gate
    private void ClearUnsafe(long frameCount)
    {
        _byId.Clear();
        _byFrame.Clear();
        _frameCount = frameCount;
        _highestTrackId = 0;
        _analyzed = new ulong[(frameCount + 63) / 64]; // 64 frames per ulong, rounded up
        Interlocked.Exchange(ref _analyzedCount, 0);
    }

    private void AddUnsafe(Detection detection)
    {
        _byId[detection.Id] = detection;
        // a null TrackId compares false here, so the .Value is safe
        if (detection.TrackId > _highestTrackId)
        {
            _highestTrackId = detection.TrackId.Value;
        }

        if (!_byFrame.TryGetValue(detection.FrameIndex, out var ids))
        {
            ids = new List<Guid>(2);
            _byFrame[detection.FrameIndex] = ids;
        }

        ids.Add(detection.Id);
    }

    private bool RemoveUnsafe(Guid id)
    {
        if (!_byId.Remove(id, out var detection))
        {
            return false;
        }

        if (_byFrame.TryGetValue(detection.FrameIndex, out var ids))
        {
            ids.Remove(id);
            if (ids.Count == 0)
            {
                _byFrame.Remove(detection.FrameIndex);
            }
        }

        return true;
    }

    private IReadOnlyList<Detection> GetForFrameUnsafe(long frameIndex)
    {
        if (!_byFrame.TryGetValue(frameIndex, out var ids))
        {
            return None;
        }

        var result = new Detection[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            result[i] = _byId[ids[i]];
        }

        return result;
    }

    private void MarkAnalyzedUnsafe(long frame)
    {
        if (frame < 0 || frame >= _frameCount)
        {
            return;
        }

        // frame >> 6 = which ulong, frame & 63 = which bit in it
        ref var word = ref _analyzed[frame >> 6];
        var bit = 1UL << (int)(frame & 63);
        if ((word & bit) == 0)
        {
            word |= bit;
            // new bits only, so a re-scan doesn't count a frame twice
            Interlocked.Increment(ref _analyzedCount);
        }
    }

    private bool IsAnalyzedUnsafe(long frame) =>
        frame >= 0 && frame < _frameCount && (_analyzed[frame >> 6] & (1UL << (int)(frame & 63))) != 0;

    private void Raise(DetectionsChangedEventArgs args)
    {
        // the AI task and the UI can bump it at the same time
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, args);
    }
}
