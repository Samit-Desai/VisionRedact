# Error Handling

## Principles

1. **Translate at the boundary.** Infrastructure converts low-level exceptions into Application exceptions with a
   classification (`MediaException.Kind`, `DetectionException.Kind`, `ExportException.Kind`, `ProjectException.Kind`).
   ViewModels never catch `CvException`, `OnnxRuntimeException` or `DllNotFoundException`.
2. **Two audiences.** Users get a sentence they can act on; the log gets the full exception, thread id and context.
3. **Background failures become state, not crashes.** A worker exception moves its owner to a terminal state
   (`PlaybackState.Faulted`, `ProcessingJobState.Failed`) and is reported through a normal event.
4. **Report the root cause.** When one failure causes others (a consumer fails, the producer is cancelled), the user
   and the log see the first real error, not the secondary cancellation.
5. **Keep the user's work.** A failed open leaves the current video loaded (`VideoWorkspace` opens the new decoder
   before closing the old one); cancelled analysis and tracking keep their partial results; a failed export or save
   never replaces an existing file with a partial one.
6. **Global handlers are a last resort**, not a strategy — and they must not make things worse (no dialog storms).

## Flow

```text
OpenCV / ONNX Runtime / OS / .NET exception
      │  Infrastructure (EmguVideoDecoderFactory, EmguVideoDecoder, NativeLoadFailure,
      │                  OnnxDetectorFactory, OnnxObjectDetector, JsonProjectRepository)
      ▼
MediaException / DetectionException / ProjectException (Kind, user message, inner)
      │  Application (VideoWorkspace, PlaybackSession, ProcessingJob, VideoExportService → ExportException)
      ▼
ViewModels: status text, banner, or IDialogService error dialog (message + expandable details); log Warning/Error
```

## Catalogue

### Media and playback

| Condition | Detected | Kind / state | User message (abridged) |
|---|---|---|---|
| File missing / moved | `FileInfo.Exists`, `FileNotFoundException` | `FileNotFound` | "The file … does not exist." |
| No permission / locked | `UnauthorizedAccessException`, `IOException` on a read handle | `AccessDenied` | "Access to … was denied." |
| Corrupt, not a video, unsupported codec | `VideoCapture.IsOpened == false` (FFmpeg, then any backend) | `UnsupportedFormat` | "could not be opened as a video…" |
| Zero size / invalid fps / no frame count | metadata validation | `InvalidMetadata` | "reports an unusable frame rate" |
| Missing / incompatible `cvextern.dll` | `NativeLoadFailure` classification | `NativeRuntimeUnavailable` + startup banner | "requires a 64-bit process" / "could not be found" |
| Decode error mid-stream | `CvException` in `ReadNext` (decode task) | `DecodeFailed` → channel completed with the error → session `Faulted` | "Playback stopped because the video could not be decoded" |
| Stream shorter than reported | `Read` returns false | `Ended` (not an error) | — |
| Preview redaction throws on a frame | caught in the playback redaction task | logged (first 3), playback continues | — |
| Handler bug in a `StateChanged` subscriber | caught in `SetState` / `TryTransition` | logged; worker continues | — |

### AI analysis

| Condition | Detected | Kind / state | User sees |
|---|---|---|---|
| Model file missing | `File.Exists` in `OnnxDetectorFactory` | `DetectionException(ModelNotFound)` | message pointing to `scripts/download-model.ps1`; a later attempt retries |
| Corrupt model / wrong contract | `OnnxRuntimeException` on load; input/output contract validation | `InvalidModel` | "'…' does not match the YOLO model contract: …" |
| `onnxruntime.dll` missing / incompatible | `DllNotFound` / `BadImageFormat` / `EntryPointNotFound` / `TypeInitialization` | `RuntimeUnavailable` | message; playback, redaction and export keep working |
| DirectML unavailable or fails for this model | `OnnxRuntimeException` etc. at session creation | **not an error**: CPU session, logged as a warning | provider "CPU (DirectML unavailable)" / "CPU (GPU failed: …)" |
| Inference error on a frame | `OnnxRuntimeException` in `Run` | `InferenceFailed` → job **Failed**, frames released | "Failed: *model* failed on frame N." |
| Decoder error during analysis | exception in the producer | job **Failed** with that error | "Failed: …" |
| Invalid boxes from a model (NaN, inverted, outside the frame) | `NormalizedRect` clamping, min-size filter | clamped or dropped | — |
| Cancel | job token | **Cancelled**, results so far kept | "Cancelled. … Results so far are kept." |

Root cause, not symptom: in `AnalysisJob`, whichever task fails cancels the other through a linked token. `Task.WhenAll`
then throws the *first* exception in the aggregate, which can be the other side's `OperationCanceledException`. The job
catches that case and re-throws the first non-cancellation exception with `ExceptionDispatchInfo.Throw`, preserving its
stack trace (`Inference_failure_fails_the_job_with_the_root_cause_and_releases_frames`). This was a real bug: the job
failed with "The operation was canceled" instead of the inference error that caused it.

### Export

| Condition | Kind / state | User sees |
|---|---|---|
| output folder missing / invalid path | `ExportException(InvalidOutputPath)`, checked before any frame is decoded | dialog, nothing written |
| output == source (case-insensitive full path) | `SameAsSource` | dialog |
| no encoder opens (avc1 → mp4v → MJPG all fail) | `EncoderUnavailable` | "Try AVI (Motion-JPEG)" |
| access denied | `AccessDenied` | dialog |
| frames written ≠ frames read | `IncompleteOutput`, temp file deleted | dialog |
| writer or decoder throws mid-export | job **Failed**, temp file deleted, frame released | "Export failed" dialog with details |
| cancel | job **Cancelled**, temp file deleted | "Export cancelled. No partial file was left behind." |
| FFmpeg: `ffmpeg.exe` missing, fails to start, or no H.264 encoder passes the probe | not an error: warning logged (except when simply not installed), OpenCV writer used | export completes, video only; codec shown in the completion dialog |
| FFmpeg: `ffprobe.exe` missing or cannot read the source | not an error: warning logged | export completes without audio |
| FFmpeg exits while frames are being written (broken pipe) | `ExportException(IncompleteOutput)` with the tail of ffmpeg's stderr, temp file deleted | "Export failed" dialog with details |
| FFmpeg exits with a non-zero code, or does not finish within 5 min, in `Complete()` | `ExportException(IncompleteOutput)` with exit code and stderr tail, temp file deleted | "Export failed" dialog with details |
| cancel or failure while FFmpeg runs | writer disposed without `Complete()` → ffmpeg.exe killed, temp file deleted | as for cancel / failure above |

Emgu's `VideoWriter` constructor *throws* (instead of returning an unopened writer) when `openh264` is missing;
`EmguVideoWriterFactory.TryOpen` catches that and moves on to the next codec. Encoder failures surface in
`IVideoWriter.Complete()` (flush and close), not in `Dispose`, which only releases or aborts.

### Region tracking

| Condition | Result | User sees |
|---|---|---|
| tracker loses the target | job **Completed**, region trimmed at the last tracked frame | "Target lost after frame N: region trimmed there." |
| cancel | **Cancelled**, path tracked so far kept | "Tracking cancelled; the path tracked so far is kept." |
| region deleted or first frame not decodable | job **Failed** | "Tracking failed: …" |

### Projects

| Condition | Kind | User sees |
|---|---|---|
| file missing | `ProjectException(NotFound)` | dialog |
| not valid JSON / incomplete | `Corrupt` | dialog naming the file |
| saved by a newer version | `UnsupportedVersion` (`schemaVersion` > 1) | "saved by a newer version (schema N)" |
| access denied on save/load | `AccessDenied` | dialog; previous project file intact (temp + rename) |
| hand-edited / out-of-range boxes | clamped by `NormalizedRect` on load | — |
| source video moved | `ProjectOpenResult.SourceMissing` | "Video not found" → locate it → `RelinkAsync` |
| relinked video differs (size, resolution, frame count, fps) | `SourceChanged` | "Video differs from the project … review detections before exporting" |

### Jobs in general

`ProcessingJob.RunAsync` is the single place where job outcomes are decided:

- normal return → **Completed**;
- `OperationCanceledException` **and this job's token was cancelled** → **Cancelled**;
- any other exception — including an OCE from someone else's token — → **Failed**, logged once with the job name,
  stored in `Error`, and delivered through `StateChanged`.

`Completion` never faults, so nothing awaiting a job can throw an unobserved exception.

### Global handlers

| Condition | Handler | Behaviour |
|---|---|---|
| Unexpected exception in an async command | `AsyncCommand` error callback | logged + dialog ("Unexpected error") |
| Anything else on the UI thread | `DispatcherUnhandledException` in `App` | see below |
| Unobserved task exception | `TaskScheduler.UnobservedTaskException` | logged, marked observed |
| Unhandled exception on a non-UI thread | `AppDomain.UnhandledException` | logged as critical (the process terminates) |

`DispatcherUnhandledException` is written to survive errors that repeat on every layout or render pass:

- **De-duplicated logging**: errors are keyed by type + message; the first 3 occurrences are logged in full, then
  one summary line every 500 ("repeated N times").
- **Rate-limited dialog**: never more than one dialog at a time (`_isShowingError`), and none within 10 s of the last
  one; suppressed occurrences are counted and mentioned in the next dialog. Without this, each modal `MessageBox`
  pumps messages, re-runs layout, re-throws, and stacks another dialog.
- `Handled = true` only once the shell is loaded; a failure before the shell exists ends the process instead of
  leaving a windowless zombie.
- On the first `ArgumentException`, `DispatcherAffinityProbe.LogForeignObjects` runs (below).

## Visual Studio XAML tools and a second Dispatcher

A debugging note, because it looks exactly like an application threading bug and is not one.

- **Symptom**: only under the Visual Studio debugger (F5), an endless stream of `ArgumentException: This API was
  accessed with arguments from the wrong context`. `Ctrl+F5` never showed it. Same on .NET 8 and .NET 10.
- **Diagnosis**: `DispatcherAffinityProbe` walks the visual tree and application resources and logs, once, every
  object whose Dispatcher differs from the window's, the Dispatcher identities on the UI thread, and debugging-tool
  assemblies loaded in the process. Result: all 185 visuals were on the window's Dispatcher — but
  `Dispatcher.CurrentDispatcher` on the same UI thread was a *second* Dispatcher (`#0037ADF8` vs `#017ADAB1`), with
  `Microsoft.VisualStudio.DesignTools.WpfTap` loaded. New WPF objects take `Dispatcher.CurrentDispatcher`, so every
  newly created element was "from the wrong context" for the tree it joined.
- **Fix**: *Tools > Options > Debugging > General* → untick **Enable UI Debugging Tools for XAML** (and XAML Hot
  Reload for WPF). When `Dispatcher.CurrentDispatcher != Dispatcher`, the error dialog now says this itself.
- **Lesson**: gather evidence (object ownership, dispatcher identity, loaded assemblies) before changing threading
  code; the probe turned a guess into a one-line configuration fix.

## Cancellation is not failure

`OperationCanceledException` caused by *our* token is filtered (`catch … when (token.IsCancellationRequested)`) and
never logged as an error. An OCE from someone else's token is a bug and becomes **Failed**
(`Cancellation_from_a_foreign_token_is_a_failure_not_a_cancellation`). Inside inference, a run terminated through
`RunOptions.Terminate` surfaces as an `OnnxRuntimeException`; the detector converts it to an OCE when the token was
cancelled.

## Testing

Unit: `Failed_open_keeps_the_current_video`, `Decoder_failure_faults_the_session_instead_of_crashing`,
`Cancellation_while_opening_disposes_the_decoder_that_was_opened`, `Exception_fails_the_job_without_escaping`,
`Cancellation_from_a_foreign_token_is_a_failure_not_a_cancellation`,
`Inference_failure_fails_the_job_with_the_root_cause_and_releases_frames`, `Decoder_failure_fails_the_job`,
`Load_failure_surfaces_and_a_later_call_retries`, `Writer_failure_leaves_no_partial_file`,
`Encoder_that_drops_frames_is_detected_as_incomplete_output`, `Source_file_can_never_be_the_output`,
`Missing_output_folder_is_rejected_up_front`, `Lost_target_trims_the_region_at_the_last_tracked_frame`,
`Preview_processor_runs_on_every_decoded_frame_and_failures_do_not_stop_playback`.
Integration: `Missing_file_is_reported_as_FileNotFound`, `Corrupt_file_is_reported_as_UnsupportedFormat_not_a_native_crash`,
`Missing_model_file_is_reported_as_ModelNotFound`, `Corrupt_model_file_is_reported_as_InvalidModel`,
`Model_with_the_wrong_format_fails_contract_validation`, `Corrupt_missing_and_future_files_are_reported_clearly`,
`Hand_edited_out_of_range_boxes_are_clamped_on_load`.
