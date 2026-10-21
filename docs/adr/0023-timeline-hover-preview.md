# ADR-0023: Timeline Hover Preview — Instant Storyboard Tiles, Then the Exact Frame

## Context
Reviewers want to see the picture under the mouse while hovering the timeline. A seek jumps to a keyframe and
decodes forward: measured 150–170 ms median at 1080p and about 0.5 s at 4K on files with a keyframe every 250 frames
(25 ms on Tears of Steel, keyframe every 18 frames), while a sweeping pointer asks about 30 times a second. Decoding every reported position
would build a queue and show stale pictures. Using the playback decoder would move playback.

## Decision
Two layers. **Instant:** while the pointer moves, show the nearest pre-built storyboard tile (no decoding).
**Exact:** once the pointer rests for 100 ms, decode that very frame and replace the tile.

- **`StoryboardJob`** (own decoder, background): tiles at least 2 s apart, at most 240 (long videos get wider
  spacing; 240 × 192×108 ≈ 15 MB), built **coarse-to-fine** (first, middle, quarters, eighths…) so every part of the
  timeline has a nearby tile within the first second. `Storyboard` is immutable (sorted tiles, `Nearest` in
  O(log n)); a new snapshot is published per tile, and a builder for a previous video can no longer publish.
- **`VideoFrameReader`** reads single frames on a private decoder.
  - The decoder is opened lazily and reads are serialized with `SemaphoreSlim(1,1)`.
  - `VideoWorkspace` owns it and disposes it with the video.
  - It is reused for face thumbnails.
- **`FramePreviewService`**: `NearestTile(frame)` for the instant layer; each `Request` cancels the previous one and
  waits a settle delay (100 ms) on
  `TimeProvider` before decoding. Sweeping across the timeline therefore decodes nothing, and only the newest request
  can raise `Ready`.
- **`IFrameImaging`** (port) / **`EmguFrameImaging`**: crops and shrinks the frame to an immutable managed
  `ImageBuffer` (192×108) that is safe to pass between threads.
- **WPF**
  - `VideoTimelineControl` stays lookless. It gains `HoverFrameCommand`, raised only when the frame changes and with
    null on leave, plus `HoverPreview` and `HoverText`, and an optional `PART_HoverPreview` moved by a
    `TranslateTransform` (no layout pass).
  - `PlayerViewModel` writes previews into one reused `WriteableBitmap` and ignores previews for frames the pointer
    has already left.

## Alternatives
- Exact frame only (the first version): simple, but while the mouse moved the card kept the old picture — the
  reviewer saw "it only updates when the mouse stops". Replaced by the two-layer design.
- Storyboard only: instant but never exact.
- A WPF `Popup` for the card: it can overflow the window, but it is a separate window and moving it costs more.

## Consequences
- Tested with `FakeTimeProvider`: a sweep decodes only the frame the pointer rests on, and a cancelled hover raises
  nothing.
- Verified in the app: a picture is on the card 20 ms after the last mouse move (a tile); the exact frame follows.
- Cost: one background pass per opened video (a few seconds) and ~15 MB of tiles.
