using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Export;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Infrastructure.Video;

// MP4 export through an external ffmpeg.exe (ADR-0020): raw BGR frames into its stdin, the first H.264 encoder that
// passes a probe, the source's first audio stream re-encoded to AAC. anything it can't do -> the OpenCV writer.
// a process, not FFmpeg's libraries via P/Invoke: a native crash or leak stays outside the app, no bindings to keep,
// and the LGPL build is used as is. the cost is one pipe copy per frame, not the bottleneck (ADR-0020).
// AAC b/c MP4 players don't reliably play camcorder AC-3. subtitles and data streams are dropped
public sealed class FfmpegVideoWriterFactory : IVideoWriterFactory
{
    // probe order, the first that works wins. each: encoder, extra args, the pixel format it's fed.
    // h264_mf first: NVENC used to lead, but inside this pipeline it measured slower than h264_mf at 1080p and no
    // faster at 4K60 (Performance.md, optimization 7).
    // careful: h264_mf as called here (default -hw_encoding 0) is the SOFTWARE "H264 Encoder MFT" on CPU cores, not a
    // GPU encoder. it was first written up as hardware, until the verbose log and the GPU counters said otherwise.
    // -hw_encoding 1 picks a hardware MFT, but that measured slower standalone.
    // nv12 / yuv420p = 4:2:0 layouts, ffmpeg converts our BGR. the hw encoders like nv12, OpenH264 needs yuv420p
    private static readonly EncoderCandidate[] Candidates =
    [
        new("h264_mf", [], "nv12"),
        // p4 = NVENC's middle preset (p1 fastest ... p7 best quality)
        new("h264_nvenc", ["-preset", "p4"], "nv12"),
        new("h264_qsv", [], "nv12"),
        new("h264_amf", [], "nv12"),
        new("libopenh264", [], "yuv420p"),
    ];

    // the OpenCV writer
    private readonly IVideoWriterFactory _fallback;
    private readonly ILogger _logger;

    // the probe runs ffmpeg once per candidate until one works, so: once per process, and only when someone first
    // asks (an MP4 export, or SelectedEncoder). ExecutionAndPublication = two callers at once still get one probe
    private readonly Lazy<EncoderCandidate?> _encoder;

    public FfmpegVideoWriterFactory(IVideoWriterFactory fallback, ILogger<FfmpegVideoWriterFactory>? logger = null, string? ffmpegDirectory = null)
    {
        _fallback = fallback;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        // an "ffmpeg" folder next to the app (scripts/download-ffmpeg.ps1), unless a test passes another one
        var directory = ffmpegDirectory ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        FfmpegPath = Path.Combine(directory, "ffmpeg.exe");
        FfprobePath = Path.Combine(directory, "ffprobe.exe");

        _encoder = new Lazy<EncoderCandidate?>(ProbeEncoder, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string FfmpegPath { get; }

    // used to find the audio
    public string FfprobePath { get; }

    /// <summary>ffmpeg.exe is next to the app. Optional: without it OpenCV does the export.</summary>
    public bool IsInstalled => File.Exists(FfmpegPath);

    /// <summary>The H.264 encoder FFmpeg will use, or null (no FFmpeg, or no encoder works). Probes on first use.</summary>
    public string? SelectedEncoder => IsInstalled ? _encoder.Value?.Name : null;

    public IVideoWriter Create(string path, VideoMetadata source, ExportFormat format)
    {
        // only MP4, and only w/ a working encoder. everything else -> OpenCV
        if (format != ExportFormat.Mp4 || SelectedEncoder is null || _encoder.Value is not { } encoder)
        {
            return _fallback.Create(path, source, format);
        }

        var (audio, audioStatus) = ProbeAudio(source.SourcePath);
        var arguments = BuildArguments(path, source, encoder, audio);

        // we write frames to its stdin and read stderr (progress + errors). no shell, no console window
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        // ArgumentList quotes each one, so paths w/ spaces are safe
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        // exe blocked, deleted, antivirus... -> OpenCV instead
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose(); // it never started, just release the handle
            _logger.LogWarning(ex, "Could not start FFmpeg; exporting with OpenCV instead.");
            return _fallback.Create(path, source, format);
        }

        // the writer owns the process from here
        var codec = audio is null ? encoder.Name : $"{encoder.Name} + aac";
        _logger.LogInformation("Export encoder: FFmpeg {Codec} {Width}x{Height} @ {Fps:F3} fps → {Path}.", codec, source.Width, source.Height, source.FramesPerSecond, path);
        return new FfmpegVideoWriter(process, codec, audioStatus, source.Width, source.Height, _logger);
    }

    // input 0 = our raw frames on stdin, input 1 (only w/ audio) = the source, for its audio. out: H.264 (+ AAC) in MP4
    private static List<string> BuildArguments(string output, VideoMetadata source, EncoderCandidate encoder, AudioAlignment? audio)
    {
        // 0.1 bit per px per frame, clamped to 2..60 Mbit/s. picture quality at this rate isn't measured yet
        var bitrate = Math.Clamp((long)(source.Width * (long)source.Height * source.FramesPerSecond * 0.1), 2_000_000, 60_000_000);
        var args = new List<string>
        {
            // -nostdin: stdin carries frames, not keys. -loglevel error + -nostats: only real errors on stderr.
            // -progress pipe:2: key=value progress lines on stderr instead
            "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:2",

            // raw frames have no header, so describe them: bgr24, size, fps, read from stdin (pipe 0)
            "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{source.Width}x{source.Height}",
            "-framerate", FrameRate(source.FramesPerSecond), "-i", "pipe:0",
        };

        if (audio is not null)
        {
            args.AddRange(["-i", source.SourcePath]);
        }

        // video = our frames only
        args.AddRange(["-map", "0:v:0"]);

        // ffmpeg converts BGR to the encoder's pixel format
        args.AddRange(["-c:v", encoder.Name, .. encoder.Arguments, "-pix_fmt", encoder.PixelFormat]);

        // average bitrate, bursts up to 1.5x, held to that over a 2x-bitrate buffer (about 2 s)
        args.AddRange(["-b:v", Invariant(bitrate), "-maxrate", Invariant(bitrate * 3 / 2), "-bufsize", Invariant(bitrate * 2)]);

        if (audio is not null)
        {
            // stream N of the source, aligned (see ProbeAudio), AAC 192k.
            // apad + -shortest = audio never shorter than the video, cut at its last frame.
            // good find - w/o them a short test export waited for ffmpeg to read the source's whole audio track
            // (Performance.md, optimization 7)
            args.AddRange(["-map", $"1:{audio.StreamIndex}", "-af", audio.Filter + ",apad", "-c:a", "aac", "-b:a", "192k", "-shortest"]);
        }

        // +faststart: the moov index goes to the front, so players can start before the whole file is there. -y: overwrite
        args.AddRange(["-movflags", "+faststart", "-y", output]);
        return args;
    }

    // NTSC rates as exact rationals (30000/1001, 60000/1001...), the rest as decimals. 29.97 is really 29.97002997...,
    // and a rounded decimal slowly drifts audio vs video on a long recording.
    // the test: fps * 1.001 lands on a whole number that isn't fps itself (so 30 stays "30")
    internal static string FrameRate(double fps)
    {
        var ntsc = Math.Round(fps * 1.001);
        return Math.Abs((ntsc / 1.001) - fps) < 1e-3 && Math.Abs(ntsc - fps) > 1e-3
            ? Invariant((long)ntsc * 1000) + "/1001"
            : fps.ToString("0.######", CultureInfo.InvariantCulture);
    }

    // the first candidate that really encodes on this PC, or null. runs once (the Lazy)
    private EncoderCandidate? ProbeEncoder()
    {
        if (!IsInstalled)
        {
            return null;
        }

        foreach (var candidate in Candidates)
        {
            // encode 10 frames of ffmpeg's own 640x360 test pattern into nowhere (-f null). "is it listed?" isn't
            // enough: h264_nvenc is always listed, and it failed this probe here until a GPU driver update.
            // 20 s timeout, so a hung driver can't freeze the export
            var (exitCode, error) = Run(FfmpegPath, ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
                "-frames:v", "10", "-c:v", candidate.Name, .. candidate.Arguments, "-pix_fmt", candidate.PixelFormat, "-f", "null", "-"], TimeSpan.FromSeconds(20));
            if (exitCode == 0)
            {
                _logger.LogInformation("FFmpeg H.264 encoder selected: {Encoder}.", candidate.Name);
                return candidate;
            }

            _logger.LogInformation("FFmpeg encoder {Encoder} unavailable: {Reason}", candidate.Name, FirstLine(error));
        }

        _logger.LogWarning("FFmpeg is installed but no H.264 encoder works; exporting with OpenCV.");
        return null;
    }

    // the first audio stream + the filter that lines it up w/ the first video frame.
    // null = no audio in the export, and the status says why (the source has none, or it couldn't be read)
    private (AudioAlignment? Alignment, ExportAudio Status) ProbeAudio(string source)
    {
        if (!File.Exists(FfprobePath))
        {
            return (null, ExportAudio.Unreadable);
        }

        // every stream's index, type and start time, as JSON on stdout
        var (exitCode, output) = Run(FfprobePath, ["-v", "error", "-show_entries", "stream=index,codec_type,start_time", "-of", "json", source], TimeSpan.FromSeconds(30), captureStdout: true);
        if (exitCode != 0)
        {
            _logger.LogWarning("ffprobe could not read {Source}; exporting without audio.", source);
            return (null, ExportAudio.Unreadable);
        }

        try
        {
            using var json = JsonDocument.Parse(output);

            // start of the first video stream; index + start of the first audio stream
            double? videoStart = null;
            (int Index, double Start)? audio = null;
            foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
            {
                var type = stream.GetProperty("codec_type").GetString();
                var start = stream.TryGetProperty("start_time", out var s) && double.TryParse(s.GetString(), CultureInfo.InvariantCulture, out var v) ? v : 0;
                if (type == "video" && videoStart is null)
                {
                    videoStart = start;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = (stream.GetProperty("index").GetInt32(), start);
                }
            }

            if (audio is not { } a)
            {
                return (null, ExportAudio.SourceHasNone);
            }

            // exported frame 0 = the source's first video frame. audio that starts later gets that much silence in
            // front (adelay, on every channel), audio that starts earlier gets cut (atrim).
            // asetpts restarts the audio clock at 0, before and after the cut
            var offset = a.Start - (videoStart ?? a.Start);
            var filter = offset >= 0
                ? $"asetpts=PTS-STARTPTS,adelay={Invariant((long)Math.Round(offset * 1000))}:all=1"
                : $"asetpts=PTS-STARTPTS,atrim=start={(-offset).ToString("0.######", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS";
            return (new AudioAlignment(a.Index, filter), ExportAudio.Included);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Unexpected ffprobe output for {Source}; exporting without audio.", source);
            return (null, ExportAudio.Unreadable);
        }
    }

    // runs a short helper (the encoder probe, ffprobe) to the end -> exit code + stdout (captureStdout) or stderr.
    // TODO: Start isn't caught here. an exe that won't start makes the encoder probe throw, the Lazy keeps that
    // exception, and every Create throws instead of falling back to OpenCV (ffprobe: that one export). ok for now
    private static (int ExitCode, string Output) Run(string exe, IEnumerable<string> arguments, TimeSpan timeout, bool captureStdout = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = captureStdout,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        // read both pipes while waiting. one after the other deadlocks once the child fills the other pipe's buffer
        var stdout = captureStdout ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            // hung (e.g. a stuck GPU driver): kill it and its children
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        // it exited, so its pipes close and the reads finish. GetResult waits for that last bit
        return (process.ExitCode, captureStdout ? stdout.GetAwaiter().GetResult() : stderr.GetAwaiter().GetResult());
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "(no output)";

    // invariant culture, so the PC's region settings can't change what ffmpeg reads
    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record EncoderCandidate(string Name, string[] Arguments, string PixelFormat);

    private sealed record AudioAlignment(int StreamIndex, string Filter);
}

// one ffmpeg.exe process. VideoExportService: Write per redacted frame, then Complete. Dispose w/o Complete = abort.
// if ffmpeg dies, the next Write gets a broken pipe, and the error carries ffmpeg's last stderr lines
internal sealed class FfmpegVideoWriter : IVideoWriter
{
    // stderr lines kept for error messages
    private const int ErrorLines = 20;

    private readonly Process _process;

    // raw bytes: BaseStream skips StandardInput's text encoder
    private readonly Stream _input;
    private readonly int _width;
    private readonly int _height;
    private readonly ILogger _logger;

    // filled by OnError on a pool thread, read by ErrorText on the export thread -> lock (_errors)
    private readonly Queue<string> _errors = new();

    // frames we pushed into the pipe
    private long _written;

    // frames ffmpeg reports done (-progress), -1 = no report yet. written by the stderr callback
    private long _encoded = -1;
    private bool _completed;
    private bool _disposed;

    public FfmpegVideoWriter(Process process, string codec, ExportAudio audio, int width, int height, ILogger logger)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _width = width;
        _height = height;
        _logger = logger;
        Codec = codec;
        Audio = audio;

        // stderr must be drained: if nobody reads it the pipe fills, ffmpeg blocks writing its progress, and then it
        // stops reading our frames
        process.ErrorDataReceived += OnError;
        process.BeginErrorReadLine();
    }

    public string Codec { get; }

    public ExportAudio Audio { get; }

    // after Complete, ffmpeg's own count (what really went into the file), before that what we sent.
    // trick: no lock for _encoded, Complete's WaitForExit() already waited for the last stderr callback
    public long FramesWritten => _completed && _encoded >= 0 ? _encoded : _written;

    public void Write(OwnedVideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // ffmpeg was told one size; a frame of another size would scramble every frame after it
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ExportException(ExportErrorKind.IncompleteOutput, $"Frame {frame.FrameIndex} is {frame.Width}x{frame.Height}, expected {_width}x{_height}.");
        }

        try
        {
            // a span over the native px, no copy
            var pixels = frame.GetPixels();

            // ffmpeg wants packed rows (width * 3 bytes). our Stride can be bigger, rows may be padded
            var row = _width * 3;

            if (frame.Stride == row)
            {
                // no padding: one write
                _input.Write(pixels);
            }
            else
            {
                // padded: row by row, w/o the padding
                for (var y = 0; y < _height; y++)
                {
                    _input.Write(pixels.Slice(y * frame.Stride, row));
                }
            }
        }
        catch (IOException ex)
        {
            // broken pipe: ffmpeg exited (bad args, encoder failure, disk full). its stderr says why
            throw new ExportException(ExportErrorKind.IncompleteOutput, $"FFmpeg stopped accepting frames at frame {frame.FrameIndex}: {ErrorText()}", ex);
        }

        _written++;
    }

    // end of video: close stdin, wait for ffmpeg to finish the file, turn a failure into an exception
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _input.Dispose(); // EOF: ffmpeg flushes the encoder and writes the MP4 index (+faststart moves it to the front)
        }
        catch (IOException)
        {
            // already gone, the exit code below says why
        }

        // flushing + faststart can take a while on long files
        if (!_process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            throw new ExportException(ExportErrorKind.IncompleteOutput, "FFmpeg did not finish writing the file within 5 minutes.");
        }

        _process.WaitForExit(); // drains the async stderr reader (progress lines)
        if (_process.ExitCode != 0)
        {
            throw new ExportException(ExportErrorKind.IncompleteOutput, $"FFmpeg failed (exit code {_process.ExitCode}): {ErrorText()}");
        }

        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_completed)
        {
            // cancel or failure: stop ffmpeg now, so the caller can delete the partial file
            try
            {
                if (!_process.HasExited)
                {
                    // kill it (and its children), then wait a bit so Windows lets go of the output file before the
                    // caller deletes it
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogWarning(ex, "Could not stop FFmpeg.");
            }
        }

        // after a kill a broken pipe here is expected, ignore it
        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
        }

        _process.Dispose();
    }

    // stderr callback, one line each, on a pool thread: progress updates the count, real messages are kept
    private void OnError(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not { Length: > 0 } line)
        {
            return;
        }

        // -progress writes key=value lines; frame=N is ffmpeg's frame count so far
        if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
        {
            Interlocked.Exchange(ref _encoded, frames);
            return;
        }

        if (line.Contains('=', StringComparison.Ordinal) && !line.Contains(' ', StringComparison.Ordinal))
        {
            return; // other progress keys (fps=, bitrate=, progress=end ...)
        }

        // a real message: keep only the newest ErrorLines
        lock (_errors)
        {
            _errors.Enqueue(line);
            while (_errors.Count > ErrorLines)
            {
                _errors.Dequeue();
            }
        }
    }

    // the kept stderr lines in one line, for exception messages
    private string ErrorText()
    {
        lock (_errors)
        {
            return _errors.Count == 0 ? "(no error output)" : new StringBuilder().AppendJoin(" | ", _errors).ToString();
        }
    }
}
