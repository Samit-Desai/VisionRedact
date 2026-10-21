using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;

namespace VisionRedact.Application.Export;

// writes a new, redacted copy: every frame, in order, at source size. no AI here, the decisions are in the snapshot.
// ExportJob -> ExportAsync (paths) -> decode | redact | encode -> Complete -> written = read? -> rename into place
// promises: the source is only read, no frame is ever skipped, and the target name only ever holds a finished file.
// the first version was one loop per frame; overlapping the 3 stages worked great (docs/Performance.md, optimization 6)
public sealed class VideoExportService : IVideoExportService
{
    // frames per channel. small on purpose: at most 2 * StageCapacity + 3 frames are alive
    private const int StageCapacity = 4;

    private readonly IVideoDecoderFactory _decoderFactory;
    private readonly IVideoWriterFactory _writerFactory;
    private readonly IRedactionProcessor _processor;
    private readonly ILogger _logger;

    public VideoExportService(IVideoDecoderFactory decoderFactory, IVideoWriterFactory writerFactory, IRedactionProcessor processor, ILogger<VideoExportService>? logger = null)
    {
        _decoderFactory = decoderFactory;
        _writerFactory = writerFactory;
        _processor = processor;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // paths are checked right here, so a bad one fails before any decoding
    public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePaths(request);

        // blocking native work from here to the end, so it gets its own pool thread
        return Task.Run(() => ExportAsyncCore(request, progress, cancellationToken), cancellationToken);
    }

    // e.g. clip.partial-<guid>.mp4 next to the target. same folder = the final move is a quick rename,
    // same extension = the writer picks the right container
    internal static string TempPathFor(string outputPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            $"{Path.GetFileNameWithoutExtension(outputPath)}.partial-{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");

    private static void ValidatePaths(ExportRequest request)
    {
        // a malformed path becomes a clear ExportException
        string output;
        try
        {
            output = Path.GetFullPath(request.OutputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExportException(ExportErrorKind.InvalidOutputPath, $"'{request.OutputPath}' is not a valid output path.", ex);
        }

        // never overwrite the original. ignore case, like Windows paths
        if (string.Equals(output, Path.GetFullPath(request.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportException(ExportErrorKind.SameAsSource, "The export must be written to a new file; the source video is never modified.");
        }

        // we don't create folders silently
        if (!Directory.Exists(Path.GetDirectoryName(output)))
        {
            throw new ExportException(ExportErrorKind.InvalidOutputPath, $"The folder '{Path.GetDirectoryName(output)}' does not exist.");
        }
    }

    private async Task<ExportResult> ExportAsyncCore(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        // completed tells the finally block whether to delete the temp file
        var stopwatch = Stopwatch.StartNew();
        var tempPath = TempPathFor(request.OutputPath);
        var completed = false;
        try
        {
            var counters = new Counters();
            string codec;
            ExportAudio audio;
            long expected;

            // its own decoder, read only. never the player's: the preview may skip frames (ADR-0007)
            using (var decoder = _decoderFactory.Open(request.SourcePath))
            {
                var metadata = decoder.Metadata;
                expected = metadata.FrameCount;
                using var writer = _writerFactory.Create(tempPath, metadata, request.Format);
                codec = writer.Codec;
                audio = writer.Audio;

                await RunPipelineAsync(decoder, writer, request, progress, counters, cancellationToken).ConfigureAwait(false);
                writer.Complete(); // flush + close the file. an encoder failure shows up here, not in Dispose

                // every decoded frame must be in the file. a silent mismatch = a broken evidence copy
                var read = Interlocked.Read(ref counters.Decoded);
                if (writer.FramesWritten != read)
                {
                    throw new ExportException(ExportErrorKind.IncompleteOutput, $"Encoded {writer.FramesWritten} of {read} frames.");
                }

                if (read == 0)
                {
                    throw new ExportException(ExportErrorKind.IncompleteOutput, "The source produced no frames.");
                }

                // last report, the encode stage only reports every 30 frames
                progress?.Report(new ExportProgress(read, metadata.FrameCount, Interlocked.Read(ref counters.Redacted)));
            } // writer and decoder released here, so the temp file can be moved

            var (frames, redacted) = (Interlocked.Read(ref counters.Decoded), Interlocked.Read(ref counters.Redacted));

            // first time the target name is touched, and only by a finished, checked file
            File.Move(tempPath, request.OutputPath, overwrite: true);
            completed = true;
            _logger.LogInformation("Exported {Frames} frames ({Redacted} redacted) to {Output} with {Codec} in {Elapsed} ({Fps:0.0} frames/s).",
                frames, redacted, request.OutputPath, codec, stopwatch.Elapsed, frames / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-3));

            // OpenCV reports a damaged packet just like the end of the file, so a truncated source used to export as
            // a shorter video that still passed the check above (ADR-0019). the file is valid, the result says it's short
            var endedEarly = SourceEndedEarly(frames, expected);
            if (endedEarly)
            {
                _logger.LogWarning("The source ended after {Frames} of {Expected} frames: truncated or damaged. Exported what could be read.",
                    frames, expected);
            }

            return new ExportResult(request.OutputPath, codec, frames, redacted, stopwatch.Elapsed)
            {
                Audio = audio,
                FramesExpected = expected,
                SourceEndedEarly = endedEarly,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ExportException(ExportErrorKind.AccessDenied, $"Cannot write '{request.OutputPath}': access denied.", ex);
        }
        finally
        {
            // failed or canceled: no half-written temp file is left behind
            if (!completed)
            {
                TryDelete(tempPath);
            }
        }
    }

    // the container's count is only an estimate for some formats (e.g. MPEG-TS), so allow max(3 frames, 0.5 %).
    // a damaged packet mid-file loses everything after it, far past that
    internal static bool SourceEndedEarly(long decoded, long expected) =>
        expected > 0 && expected - decoded > Math.Max(3, expected / 200);

    // decode -> Channel(4) -> redact -> Channel(4) -> encode, one task per stage.
    // a frame costs the slowest stage instead of the sum. one reader + one writer per channel keeps the order.
    // a stage owns a frame until the next channel takes it, encode frees it. the decoder and the writer
    // each have exactly one stage, so no lock
    private async Task RunPipelineAsync(IVideoDecoder decoder, IVideoWriter writer, ExportRequest request, IProgress<ExportProgress>? progress, Counters counters, CancellationToken cancellationToken)
    {
        // FullMode.Wait = backpressure: a full channel makes the stage before it wait. nothing is ever dropped
        var options = new BoundedChannelOptions(StageCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true };
        var decoded = Channel.CreateBounded<OwnedVideoFrame>(options);
        var planned = Channel.CreateBounded<OwnedVideoFrame>(options);
        var total = decoder.Metadata.FrameCount;
        var strength = request.Redactions.Settings.Strength;

        // one stop switch for all three: the user's Cancel, or any stage that fails
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        // A, decode: from frame 0 in order, never seeks, never skips
        var decode = Task.Run(() => StageAsync(decoded.Writer, linked, async () =>
        {
            while (decoder.ReadNext() is { } frame)
            {
                await Hand(frame, decoded.Writer, token).ConfigureAwait(false);

                // the export thread reads it at the end
                Interlocked.Increment(ref counters.Decoded);
            }
        }));

        // B, redact: ask the snapshot what to blur on this frame, blur in place, pass it on
        var redact = Task.Run(() => StageAsync(planned.Writer, linked, async () =>
        {
            await foreach (var frame in decoded.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var plan = request.Redactions.PlanFor(frame.FrameIndex, frame.Width, frame.Height);
                    if (plan.Count > 0)
                    {
                        _processor.Apply(frame, plan, strength);

                        // read by the encode task (progress) and the export thread (result)
                        Interlocked.Increment(ref counters.Redacted);
                    }
                }
                catch
                {
                    // still ours, so free it before giving up
                    frame.Dispose();
                    throw;
                }

                await Hand(frame, planned.Writer, token).ConfigureAwait(false);
            }
        }));

        // C, encode: write the frame, then free it. the last owner of every frame
        var encode = Task.Run(() => StageAsync(null, linked, async () =>
        {
            long written = 0;
            await foreach (var frame in planned.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    writer.Write(frame);
                }
                finally
                {
                    frame.Dispose();
                }

                // every 30 frames is plenty for a progress bar
                if (++written % 30 == 0)
                {
                    progress?.Report(new ExportProgress(written, total, Interlocked.Read(ref counters.Redacted)));
                }
            }
        }));

        var all = Task.WhenAll(decode, redact, encode);
        try
        {
            await all.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (all.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } root)
        {
            // one stage failed and canceled the others: throw the real error (w/ its own stack), not the cancel
            ExceptionDispatchInfo.Throw(root);
        }
        finally
        {
            // after a cancel or a failure frames can still sit in the channels. nobody reads them now, so free them
            foreach (var channel in new[] { decoded, planned })
            {
                while (channel.Reader.TryRead(out var leftover))
                {
                    leftover.Dispose();
                }
            }
        }
    }

    // ok: complete the output channel, so the next stage's await foreach ends.
    // failed: pass the error down the channel and cancel the shared token, so the others stop instead of waiting forever
    private static async Task StageAsync(ChannelWriter<OwnedVideoFrame>? output, CancellationTokenSource linked, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);

            output?.TryComplete();
        }
        catch (Exception ex)
        {
            output?.TryComplete(ex);
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    // once the channel takes the frame, the next stage owns it. refused (cancel, closed) = still ours, so free it
    private static async ValueTask Hand(OwnedVideoFrame frame, ChannelWriter<OwnedVideoFrame> channel, CancellationToken token)
    {
        try
        {
            // waits while the channel is full
            await channel.WriteAsync(frame, token).ConfigureAwait(false);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    // shared by the stage tasks and the export thread, always through Interlocked (fields, so they can go by ref)
    private sealed class Counters
    {
        public long Decoded;
        public long Redacted;
    }

    // best effort: a failed delete is only logged, it must never hide the real error
    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete partial export {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete partial export {Path}.", path);
        }
    }
}
