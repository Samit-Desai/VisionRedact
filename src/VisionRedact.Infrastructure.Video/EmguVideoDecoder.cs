using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Infrastructure.Video;

// one reader over OpenCV's VideoCapture (FFmpeg backend), made by EmguVideoDecoderFactory. playback, the scan, export,
// people grouping, region tracking and the previews each open their own. one thread at a time.
// TODO: a new native Mat per frame, no pool. only worth it if a profile shows that cost, none seen so far
// (Performance.md, known limits). ok for now
internal sealed class EmguVideoDecoder : IVideoDecoder
{
    private readonly VideoCapture _capture;

    // the index the next ReadNext/Skip returns. counted here, so Position is exact and cheap
    private long _position;
    private bool _disposed;

    // takes ownership of an opened capture
    public EmguVideoDecoder(VideoCapture capture, VideoMetadata metadata)
    {
        _capture = capture;
        Metadata = metadata;
        BackendName = capture.BackendName;
    }

    public VideoMetadata Metadata { get; }

    public string BackendName { get; }

    public long Position => _position;

    // PosFrames: OpenCV goes back to the keyframe before the target and decodes forward. exact on the MTS test file
    // (Performance.md, optimization 5), slow when keyframes are far apart, approximate for VFR (ADR-0018)
    public void Seek(long frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var target = Metadata.ClampFrame(frameIndex);
        if (!_capture.Set(CapProp.PosFrames, target))
        {
            throw new MediaException(MediaErrorKind.DecodeFailed, $"Seeking to frame {target} is not supported for this file.", Metadata.SourcePath);
        }

        _position = target;
    }

    /// <summary>The next frame, or null at the end. The caller owns (and disposes) it.</summary>
    public OwnedVideoFrame? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // a NEW Mat every frame, so the frame really owns its px. QueryFrame would be simpler, but its buffer is
        // overwritten on the next grab, which would spoil frames still queued downstream (ADR-0008).
        // non-null until the frame owns it, so finally frees it on any early exit
        Mat? mat = new Mat();
        try
        {
            // false/empty = end of stream OR a broken packet, OpenCV can't tell them apart. both end the read here;
            // export spots a short source w/ its SourceEndedEarly check
            if (!_capture.Read(mat) || mat.IsEmpty)
            {
                return null;
            }

            // every frame gets the same layout: 8-bit BGR, rows back to back
            mat = NormalizeToBgr24(mat);

            var index = _position++;
            var frame = new MatVideoFrame(mat, index, Metadata.TimeOfFrame(index));
            mat = null; // the frame owns it now
            return frame;
        }
        catch (CvException ex)
        {
            // callers never see OpenCV types
            throw new MediaException(MediaErrorKind.DecodeFailed, $"Frame {_position} could not be decoded.", Metadata.SourcePath, ex);
        }
        finally
        {
            mat?.Dispose();
        }
    }

    // Grab only: the packet is still decoded (later frames need it), just no color conversion and no copy.
    // good find - the scan used to ReadNext every frame and throw most of them away (Performance.md, optimization 1)
    public bool Skip()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_capture.Grab())
        {
            return false;
        }

        _position++;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // native demuxer/decoder + the file handle
        _capture.Dispose();
    }

    // 8-bit, 3-channel BGR, continuous. almost every file decodes like that already -> same Mat back.
    // when it has to convert, it disposes the input
    private static Mat NormalizeToBgr24(Mat mat)
    {
        if (mat.Depth == DepthType.Cv8U && mat.NumberOfChannels == 3 && mat.IsContinuous)
        {
            return mat;
        }

        var converted = new Mat();
        try
        {
            switch (mat.NumberOfChannels)
            {
                case 1:
                    CvInvoke.CvtColor(mat, converted, ColorConversion.Gray2Bgr);
                    break;
                case 4:
                    CvInvoke.CvtColor(mat, converted, ColorConversion.Bgra2Bgr);
                    break;
                default:
                    mat.CopyTo(converted); // makes the buffer continuous. NOTE: 2 channels land here too and stay 2
                    break;
            }

            // 8 bits. careful: ConvertTo w/ scale 1 saturates, so 16-bit input would clip, not scale down
            if (converted.Depth != DepthType.Cv8U)
            {
                using var source = converted;
                converted = new Mat();
                source.ConvertTo(converted, DepthType.Cv8U);
            }

            return converted;
        }
        catch
        {
            // free the half-built output too
            converted.Dispose();
            throw;
        }
        finally
        {
            // the input is done either way
            mat.Dispose();
        }
    }
}
