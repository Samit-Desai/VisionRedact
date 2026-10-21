using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;

namespace VisionRedact.Application.Preview;

// small picture of one frame, made ahead of time for the instant hover preview
public sealed record StoryboardTile(long FrameIndex, ImageBuffer Image);

// tiles built so far, sorted by frame. immutable: the builder publishes a new one per tile, so the UI reads it
// from any thread w/o a lock
public sealed class Storyboard
{
    private readonly StoryboardTile[] _tiles;

    private Storyboard(StoryboardTile[] tiles, long pixelBytes)
    {
        _tiles = tiles;
        PixelBytes = pixelBytes;
    }

    public static Storyboard Empty { get; } = new([], 0);

    public int Count => _tiles.Length;

    // managed bytes of all tiles. With carries the total along, so no loop
    public long PixelBytes { get; }

    /// <summary>The tile nearest to the frame, null while none is built. O(log n).</summary>
    public StoryboardTile? Nearest(long frameIndex)
    {
        if (_tiles.Length == 0)
        {
            return null;
        }

        // trick: a probe tile w/o an image, ByFrame only looks at FrameIndex
        var at = Array.BinarySearch(_tiles, new StoryboardTile(frameIndex, null!), ByFrame.Instance);
        if (at >= 0)
        {
            return _tiles[at];
        }

        // ~at = the first tile after the frame. compare it w/ the one before (a tie goes to the earlier one)
        var after = ~at;
        if (after == 0)
        {
            return _tiles[0];
        }

        if (after == _tiles.Length)
        {
            return _tiles[^1];
        }

        return frameIndex - _tiles[after - 1].FrameIndex <= _tiles[after].FrameIndex - frameIndex ? _tiles[after - 1] : _tiles[after];
    }

    // a copy w/ the tile inserted in frame order (it replaces a tile of the same frame)
    public Storyboard With(StoryboardTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var at = Array.BinarySearch(_tiles, tile, ByFrame.Instance);
        if (at >= 0)
        {
            var replaced = (StoryboardTile[])_tiles.Clone();
            replaced[at] = tile;
            return new Storyboard(replaced, PixelBytes - _tiles[at].Image.Pixels.Length + tile.Image.Pixels.Length);
        }

        var insert = ~at;
        var tiles = new StoryboardTile[_tiles.Length + 1];
        Array.Copy(_tiles, tiles, insert);
        tiles[insert] = tile;
        Array.Copy(_tiles, insert, tiles, insert + 1, _tiles.Length - insert);
        return new Storyboard(tiles, PixelBytes + tile.Image.Pixels.Length);
    }

    // which frames to picture, in build order: evenly spaced (>= spacing apart, <= maxTiles), coarse to fine,
    // so after a few tiles every part of the timeline already has a picture nearby
    public static IReadOnlyList<long> Plan(long frameCount, double framesPerSecond, TimeSpan spacing, int maxTiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTiles);
        if (frameCount <= 0 || framesPerSecond <= 0)
        {
            return [];
        }

        var step = Math.Max(Math.Max(1.0, spacing.TotalSeconds * framesPerSecond), frameCount / (double)maxTiles);
        var count = (int)Math.Min(maxTiles, Math.Ceiling(frameCount / step));
        var order = new List<long>(count);
        var taken = new bool[count];
        // halving over tile indices: 0, then the middle, then the middles of each half...
        // halves of the next power of two >= count, so exact halves only when count is a power of two
        for (var stride = HighestPowerOfTwoAtLeast(count); stride >= 1; stride /= 2)
        {
            for (var i = 0; i < count; i += stride)
            {
                if (!taken[i])
                {
                    taken[i] = true;
                    order.Add(Math.Min(frameCount - 1, (long)Math.Round(i * step)));
                }
            }
        }

        return order;
    }

    private static int HighestPowerOfTwoAtLeast(int value)
    {
        var power = 1;
        while (power < value)
        {
            power *= 2;
        }

        return power;
    }

    private sealed class ByFrame : IComparer<StoryboardTile>
    {
        public static readonly ByFrame Instance = new();

        public int Compare(StoryboardTile? x, StoryboardTile? y) => x!.FrameIndex.CompareTo(y!.FrameIndex);
    }
}

// builds the storyboard in the background, on its own decoder (the frame reader stays free for exact frames).
// seek -> shrink -> publish, one tile at a time, so hovering gets better after the first few.
// canceled w/ the other jobs when another video opens
public sealed class StoryboardJob(
    string videoPath,
    IVideoDecoderFactory decoders,
    IFrameImaging imaging,
    FramePreviewOptions options,
    Action<StoryboardTile> publish,
    ILogger<StoryboardJob>? logger = null) : ProcessingJob("Build timeline previews", logger: logger)
{
    // the builder thread writes, the Diagnostics panel reads w/o a lock
    private int _tilesPlanned;
    private bool _decoderOpen;
    private int _framesHeld;
    private int _peakFramesHeld;
    private long _started; // Stopwatch timestamps, 0 = not yet
    private long _finished;

    // 0 until the builder has opened the video
    public int TilesPlanned => Volatile.Read(ref _tilesPlanned);

    // only while the job runs
    public bool IsDecoderOpen => Volatile.Read(ref _decoderOpen);

    // 1 while a frame is being shrunk, else 0
    public int FramesHeld => Volatile.Read(ref _framesHeld);

    // one frame at a time, so at most 1
    public int PeakFramesHeld => Volatile.Read(ref _peakFramesHeld);

    // still growing while it runs, the final value once it ended
    public TimeSpan Elapsed
    {
        get
        {
            var started = Interlocked.Read(ref _started);
            if (started == 0)
            {
                return TimeSpan.Zero;
            }

            var finished = Interlocked.Read(ref _finished);
            return Stopwatch.GetElapsedTime(started, finished == 0 ? Stopwatch.GetTimestamp() : finished);
        }
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _started, Stopwatch.GetTimestamp());
        try
        {
            using var decoder = decoders.Open(videoPath);
            Volatile.Write(ref _decoderOpen, true);
            var frames = Storyboard.Plan(decoder.Metadata.FrameCount, decoder.Metadata.FramesPerSecond, options.TileSpacing, options.MaxTiles);
            Volatile.Write(ref _tilesPlanned, frames.Count);
            for (var i = 0; i < frames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decoder.Seek(frames[i]);
                if (Shrink(decoder.ReadNext()) is { } image)
                {
                    publish(new StoryboardTile(frames[i], image));
                }

                ReportProgress(i + 1, frames.Count, $"Timeline previews {i + 1}/{frames.Count}");
            }
        }
        finally
        {
            // the decoder is already disposed here (end of the using scope): canceled, failed or done
            Volatile.Write(ref _decoderOpen, false);
            Interlocked.Exchange(ref _finished, Stopwatch.GetTimestamp());
        }

        return Task.CompletedTask;
    }

    // one frame -> one tile, and the native frame goes right away
    private ImageBuffer? Shrink(OwnedVideoFrame? frame)
    {
        if (frame is null)
        {
            return null;
        }

        // single writer, so a plain compare + write keeps the peak exact
        var held = Interlocked.Increment(ref _framesHeld);
        if (held > Volatile.Read(ref _peakFramesHeld))
        {
            Volatile.Write(ref _peakFramesHeld, held);
        }

        try
        {
            return imaging.Resize(frame, null, options.MaxWidth, options.MaxHeight);
        }
        finally
        {
            frame.Dispose();
            Interlocked.Decrement(ref _framesHeld);
        }
    }
}
