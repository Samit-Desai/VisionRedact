# VisionRedact, explained from zero

A plain-English walk through **what** this app does, **why** it is built this way, and **every trick** used to make it
fast and safe. No video or AI background needed. Every piece of jargon is explained the first time it appears (and
again in the [glossary](#glossary) at the end).

> How to read this: each chapter starts with the **problem** in everyday terms, then the **idea**, then a small
> **picture**, then **where it lives in the code**.

---

## Contents

1. [What a video really is](#1-what-a-video-really-is)
2. [The app in one picture](#2-the-app-in-one-picture)
3. [Threads: why the screen must never freeze](#3-threads-why-the-screen-must-never-freeze)
4. [Playing a video smoothly](#4-playing-a-video-smoothly)
5. [Memory: the 6 MB picture the garbage collector can't see](#5-memory-the-6-mb-picture-the-garbage-collector-cant-see)
6. [How the AI finds faces](#6-how-the-ai-finds-faces)
7. [Scanning a whole video in the background](#7-scanning-a-whole-video-in-the-background)
8. [Using the graphics card (GPU)](#8-using-the-graphics-card-gpu)
9. [Humans have the last word: review](#9-humans-have-the-last-word-review)
10. [Tracking: "is this the same face as a moment ago?"](#10-tracking-is-this-the-same-face-as-a-moment-ago)
11. [Hiding things: blur, pixelate, black box](#11-hiding-things-blur-pixelate-black-box)
12. [Exporting the finished video](#12-exporting-the-finished-video)
13. [Saving your work (projects)](#13-saving-your-work-projects)
14. [When things go wrong](#14-when-things-go-wrong)
15. [How we know it's fast: measuring](#15-how-we-know-its-fast-measuring)
16. [How the code is organized](#16-how-the-code-is-organized)
17. [Glossary](#glossary)

---

## 1. What a video really is

A video is a **flip-book**: a long list of still pictures called **frames**, shown quickly one after another.

```text
 frame 0     frame 1     frame 2     frame 3   ...
 ┌──────┐    ┌──────┐    ┌──────┐    ┌──────┐
 │ 🙂   │    │  🙂  │    │   🙂 │    │    🙂│      shown 30 or 60 times per second
 └──────┘    └──────┘    └──────┘    └──────┘
```

Words you will meet:

| Word | Plain meaning | Your cricket video |
|---|---|---|
| **Resolution** | how many dots (pixels) per picture | 1920 × 1080 ("Full HD") |
| **FPS** (frames per second) | how many pictures per second | 59.94 |
| **Frame count** | how many pictures in total | 82,351 (≈ 23 minutes) |
| **Pixel** | one coloured dot, stored as 3 numbers: Blue, Green, Red (0–255 each) | 2 million per frame |

**One uncompressed frame is big**: 1920 × 1080 dots × 3 colours = about **6 MB**. A 23-minute video would be
82,351 × 6 MB ≈ **500 GB** if stored like that. Yet your file is 4.3 GB. How?

### Compression (the codec)

A **codec** (here: H.264) stores the video cleverly. Most frames are saved as *"same as the previous picture, except
this small part moved"*. Only every so often is a full picture stored — a **keyframe**.

```text
 K  d  d  d  d  d  d  d  K  d  d  d  ...     K = keyframe (full picture)
                                              d = "difference from the previous one"
 └──── group of pictures (GOP) ────┘
```

Two consequences shape the whole app:

1. **Decoding** (unpacking a frame back into 6 MB of pixels) takes real work — a few milliseconds per frame.
2. **Jumping** (seeking) to frame 1,000 is slow: you must go back to the keyframe before it and unpack every
   difference up to 1,000. That is why the app is careful about *when* it seeks.

The file itself is a **container** (`.mp4`, `.mts`, `.avi`): a box holding the compressed video (and audio) plus
information like the FPS. Your `.MTS` files are **AVCHD**, the format many camcorders record.

We use **OpenCV** (a famous C++ image library) through **Emgu CV** (its C# wrapper) to open files and decode frames.

---

## 2. The app in one picture

What the user does: open a video → watch it → let AI find faces → check the AI's work → add boxes it missed →
export a copy with those areas blurred. **The original file is never changed.**

```text
                              YOUR VIDEO FILE (read only, never modified)
                                        │
           ┌────────────────────────────┼─────────────────────────────┐
           ▼                            ▼                             ▼
   ① WATCHING (playback)      ② AI SCANNING (analysis)        ③ EXPORT
   "smooth, instant"           "as fast as possible"           "every frame, perfect"
   may skip late pictures      may look at every Nth picture   never skips anything
           │                            │                             │
           ▼                            ▼                             ▼
      the screen               list of detected faces        new redacted video file
                                        │
                                        ▼
                         ④ HUMAN REVIEW (approve / reject / draw)
```

**Key design choice: three separate "readers" of the file.** Each job opens the video on its own.
Why? They want opposite things:

- Watching wants to be **quick** — if it falls behind, it's fine to skip a picture.
- AI scanning wants **throughput** — chew through the whole file.
- Export wants **correctness** — every single picture, in order.

If they shared one reader, every time you dragged the timeline the AI would lose its place, and export would risk
getting a "skipped" picture. Separate readers cost some extra CPU but remove a whole class of bugs.

---

## 3. Threads: why the screen must never freeze

### The restaurant analogy

Think of the app as a restaurant.

- The **UI thread** is the **one waiter** who talks to customers: draws the screen, reacts to clicks and keys.
  There is exactly one, and only he may touch the tables (WPF controls).
- **Background threads** are **cooks in the kitchen**: they do the heavy work (decoding video, running AI).

If the waiter goes into the kitchen to cook a steak (decode a video frame, 5–20 ms, or run AI, 25 ms…), **nobody is
serving customers** — the window freezes, buttons don't respond, Windows shows "Not responding".

```text
 BAD                                         GOOD
 ───                                         ────
 waiter: take order → COOK → COOK → COOK     waiter: take order → pass ticket → serve → serve → serve
 customers: 😠 waiting...                     cooks:  COOK → COOK → COOK   (in the kitchen)
```

**Rule #1 of the app: the UI thread never decodes, never runs AI, never waits for the kitchen.**

### How work goes back and forth

- **Waiter → kitchen**: the UI only writes a note ("go to frame 900") into a small mailbox and rings a bell.
  That takes microseconds. The UI never waits for the answer.
- **Kitchen → waiter**: a finished frame can't be put on the table by a cook (only the waiter may touch the UI). The cook
  puts it on the **pass** (a hand-off spot), and asks the waiter "when you have a moment, serve this"
  — in WPF that's `Dispatcher.BeginInvoke`. `BeginInvoke` means "do it later, I won't wait"; its dangerous twin
  `Invoke` means "do it now and I'll wait", which can **deadlock** (waiter waits for cook, cook waits for waiter,
  both stand still forever). We only ever use the "later" version.

```text
 UI thread (waiter)                     background (cook)
 ──────────────────                     ─────────────────
 click Seek → write "900" in mailbox ─► reads mailbox, decodes frame 900
                                        puts frame on the pass
 serves it (draws it)  ◄──BeginInvoke── "waiter, when free, show this"
```

### "async/await" in one paragraph

`await` means *"I'm waiting for something; meanwhile give this thread back so it can do other things"*. It does **not**
create a new thread by itself. A `Task` is a "promise of a result later". `Task.Run` is how you explicitly hand
blocking work to a cook (the thread pool). We use `Task.Run` only at the few places where slow native work starts
(opening a file, loading the AI model), not everywhere.

Things we never do, and why:

| Don't | Because |
|---|---|
| `.Result` / `.Wait()` | makes a thread sit and wait → freezes the UI or deadlocks |
| `Thread.Sleep` | wastes a thread doing nothing |
| `async void` (except event handlers) | its errors can't be caught → crashes |
| decode on a timer on the UI thread | freezes the UI for every frame |

**Where in code:** `PlaybackSession` (the kitchen for playback), `IUiThread` (the only way to ask the waiter),
`WriteableBitmapFramePresenter` (the pass for frames), `AsyncCommand` (safe button handlers),
`ShellWindow.OnClosing` (closes without freezing: cancels the close, cleans up, closes again).

---

## 4. Playing a video smoothly

### Problem

Showing 60 pictures a second means one every **16.7 ms**. Decoding sometimes takes 3 ms, sometimes 20 ms (keyframes).
The screen must stay smooth and respond instantly to pause and seek.

### Trick 1 — a small buffer ("decode ahead")

Two cooks: one **decodes** pictures and puts them on a short shelf that holds **3 pictures**; the other **presents**
them at the right time. If one decode is slow, the shelf still has pictures ready.

```text
  decoder cook ──► [ 🖼 🖼 🖼 ] shelf (max 3) ──► presenter cook ──► screen
                    full? decoder waits           takes one when it's due
```

Why only 3? Each picture is 6 MB, and after a seek everything on the shelf is useless. Small shelf = little memory,
little waste.

### Trick 2 — a real clock, not "wait 33 ms"

The naive way: show a frame, sleep 33 ms, repeat. But decoding + drawing also take time, and Windows timers are
imprecise (±15 ms), so the video slowly drifts and plays too slow.

Instead we note the **start time** and compute: *"frame N is due at start + N ÷ FPS"*. If we are late for one frame,
we are not late for all the following ones — no drift.

```text
 naive:   show ─ sleep 33 ─ show ─ sleep 33 ─ ...   errors add up → video slows down
 clock:   frame N due at  start + N/fps               each frame judged against the clock → no drift
```

### Trick 3 — throw away late pictures (preview only!)

If a picture's moment has already passed, showing it would only make us more late. So playback **drops** it and moves
on (at most 4 in a row, so something still shows on a slow PC). The diagnostics panel counts these drops.
**Export never does this** — export must contain every frame.

### Trick 4 — only the latest seek matters ("mailbox, not queue")

When you drag the timeline, you send hundreds of "go to frame X" requests. Doing them all would make the video lag
behind your mouse. So there is a **mailbox with one slot**: each new request overwrites the old one. The cook only
ever decodes the **latest** target.

```text
 you drag:   go 100 → go 140 → go 180 → go 220        (hundreds per second)
 mailbox:    [220]                                    (only the newest survives)
 cook:       decodes 220 only
```

### Trick 5 — "generations" to clean the shelf after a seek

After a seek, pictures already on the shelf are from the old position. Each seek gets a new **generation number**;
anything on the shelf with an older number is thrown away (and its memory freed) instead of shown.

### Trick 6 — one picture frame on screen, repainted

We don't create a new image object per frame (that would allocate 6 MB and make WPF redo its layout every time).
There is **one** `WriteableBitmap`; each frame we copy the new pixels into it (`WritePixels`). If the waiter is busy
and a newer frame arrives before the previous was drawn, the older one is dropped — the waiter never has a backlog.
And at most **one** "please draw" note is ever waiting for him.

### Trick 7 — boxes are drawn *on top*, not into the picture

Face boxes and redaction rectangles are separate transparent layers above the video (like a sheet of clear plastic),
so hiding/showing them never needs re-decoding.

```text
 VideoSurface
  ├─ video picture      (WriteableBitmap)
  ├─ AI boxes layer     (orange: detected faces)
  ├─ redaction layer    (red: your drawn regions)
  └─ mouse layer        (draw / move / resize)
```

### The timeline control

`VideoTimelineControl` is a custom WPF control. It draws thousands of detection marks cheaply by grouping them per
**screen pixel column** (50,000 detections still become at most ~1,000 little rectangles), and the moving playhead is
a separate small element, so moving it every frame doesn't redraw all the marks.

**Measured on your 4.3 GB file:** 59.9 decoded / 61.9 shown per second, 3.8 ms per decode, 0 drops, only 4 pictures in
memory at once.

---

## 5. Memory: the 6 MB picture the garbage collector can't see

### Problem

C# has a **garbage collector (GC)**: a cleaner who throws away objects nobody uses anymore. But a video frame lives
mostly in **native memory** (memory managed by OpenCV's C++ code, outside .NET). The C# object the GC sees is a tiny
~100-byte "handle" pointing to 6 MB of native memory.

```text
 what the GC sees:   [handle 100 bytes]  ──►  6,000,000 bytes of pixels (native, invisible to GC)
```

The cleaner judges "is it time to clean?" by how much *he* can see. He sees almost nothing, so he doesn't hurry —
meanwhile gigabytes pile up. **So we never rely on the GC for frames.**

### The idea: every frame has exactly one owner (a hot potato)

Whoever holds a frame either **passes it on** or **disposes it** (frees its memory) — immediately, in a `finally`
block so it also happens if an error occurs.

```text
 decoder creates it ──► shelf owns it ──► presenter owns it ──► drawn ──► Dispose() → memory freed
                                     └─ thrown away (late / stale)   ──► Dispose() → memory freed
```

The class is called `OwnedVideoFrame`. It also keeps a **live counter**: how many frames exist right now. If that
number keeps growing, someone forgot to dispose — an easy leak alarm. It's shown in the diagnostics panel; it never
went above **10** in any test, whether the video was 15 seconds or 23 minutes.

A classic trap we avoid: OpenCV's `QueryFrame()` gives you a picture that it **reuses** for the next frame. If you put
it on the shelf, it silently changes under you. We always decode into a **fresh** picture.

---

## 6. How the AI finds faces

### What a "model" is

A **model** is a file full of numbers (learned from thousands of labelled photos) plus a recipe for computing with
them. You give it a picture as numbers; it gives back guesses: *"there's a face here, I'm 93 % sure"*.

We use the **ONNX** format (a standard file format for AI models) and **ONNX Runtime** (Microsoft's engine that runs
such files). Two models are included:

| Model | Picture size it wants | Speed (CPU) | Good at |
|---|---|---|---|
| **UltraFace** | 320 × 240 | ~5 ms | fast, but misses small/distant faces |
| **YOLO11n face** | 640 × 640 | ~23 ms | finds tiny faces (≈30 px tall on your cricket video) |

Any other YOLO model file dropped into the `models` folder (people, cars, licence plates) is picked up automatically
— the app reads the class names stored inside the file.

### Step by step inside one detection

```text
 1920×1080 frame
   │ ① shrink to fit 640×640 WITHOUT squashing ("letterbox": grey bars fill the rest)
   │ ② reorder colours: OpenCV stores Blue-Green-Red, the model wants Red-Green-Blue
   │ ③ turn 0–255 into small decimals the model expects (e.g. ÷255)
   │ ④ rearrange from "pixel by pixel" to "all reds, all greens, all blues" (HWC → CHW)
   ▼
 ONNX Runtime runs the model (the "inference")
   ▼
 thousands of raw guesses ("anchors"), each: box + score
   │ ⑤ keep only confident ones (score ≥ threshold)
   │ ⑥ NMS: many overlapping guesses for the same face → keep the best one
   │ ⑦ undo the letterbox: model coordinates → position in the real frame
   ▼
 a handful of face boxes
```

- **Letterbox (①)**: squashing a wide 16:9 picture into a square would make faces thin and the AI worse. So we
  shrink it proportionally and pad with bars — like a widescreen movie on an old TV.
- **NMS, "non-maximum suppression" (⑥)**: the model says "face here" 20 times for one face, slightly shifted. NMS
  keeps the highest-scoring box and deletes the others that overlap it a lot.

### Coordinates: store positions as fractions, not pixels

The same box must be drawn on a 1920-wide video, a 640-wide AI input, and a resizable window on a 4K monitor. So a
box is stored as **fractions of the frame**: "starts 25 % from the left, 10 % wide" (`NormalizedRect`). Each place
converts fractions into its own units at the last moment. One small class (`LetterboxTransform`) does the "fit with
bars" maths for both the AI input *and* the screen display.

Safety details: boxes that stick out of the picture, have negative sizes or are "not a number" are clamped or thrown
away, so they can never crash the pixel code. When turning fractions into pixels for blurring, we round **outward**:
covering one extra pixel is harmless; missing one could leak part of a face.

### Tricks that make it fast

- **Load the model once.** Loading takes hundreds of milliseconds; we keep one loaded copy (`DetectorHost`) forever.
- **Reuse the big buffers.** The 640×640 input needs a 4.9 MB number array. Creating a new one every frame forced
  the GC to do a big "stop everything" clean every ~3 frames (99 of them per 300 frames). Reusing one: **zero**.
- **Read the answer in place.** The model's output is read directly where ONNX Runtime left it, not copied.

---

## 7. Scanning a whole video in the background

### Problem

The decoder can unpack ~600 frames per second (when skipping) but the AI handles ~46 (CPU). If the decoder just kept
pushing frames into a list for the AI, the list would grow by ~550 frames × 6 MB = **3 GB every second** until the
PC runs out of memory.

### The idea: a conveyor belt that holds only 4 items (backpressure)

```text
 decoder ──► [ 🖼 🖼 🖼 🖼 ] belt: max 4 ──► AI
             belt full? the decoder simply WAITS until the AI takes one.
```

This is a **bounded channel** (`System.Threading.Channels`). When full, the fast side pauses — that pausing is called
**backpressure**. Memory stays at 4 frames no matter how long the video is. The diagnostics panel shows the belt:
"4 / 4" = the AI is the slow part (normal on CPU).

### Sampling: you don't need every frame

At 60 frames per second, a face barely moves between frames. "5 per second" means the AI looks at every ~12th frame.
For the 11 frames in between, the box **slides** from where the face was on one analysed frame to where it is on the
next (**interpolation** — like a flip-book artist drawing the in-between pictures). If the face isn't found again on the
next analysed frame, the box simply stays where it was (**hold**), so nothing that was blurred stops being blurred.

```text
analysed:     f0 ●──────────────────────● f12
shown now:    f0 ●──●──●──●──●──●──●──●──● f12     box glides with the face
first version: ●●●●●●●●●●●● then jump ► ●          box froze, lagged, then snapped back
```

The first version only held boxes. You spotted boxes lagging behind moving players and snapping back; measured on your
footage, 17.9 % of faces had a box visibly off. With interpolation and a smarter tracker (chapter 10) it's 4.3 %.

| | Every frame | 5 per second |
|---|---|---|
| Time for your 23-min video (CPU, YOLO) | ~30 min | ~5 min |
| Risk | none | a face visible for less than ⅕ s could be missed |

### Trick — skip without unpacking colours

For the 11 frames the AI doesn't look at, we still must step past them (compression depends on previous frames), but
we skip the expensive "convert to a 6 MB colour picture" part (`Skip()` instead of `ReadNext()`). Measured: **2.3×
faster** scanning.

### Jobs: start, watch, cancel safely

Long tasks (analysis, export, tracking) are **jobs** with a clear life story:

```text
 Created ─► Running ─► Completed          (finished)
               │  └──► Failed             (error — the real cause is reported)
               ▼
           Cancelling ─► Cancelled        (you pressed Cancel)
```

Cancel is **cooperative**: we can't yank the AI mid-calculation, so we set a flag it checks between steps (ONNX
Runtime's "terminate" switch), and between frames. Everything already found is kept. If either side (decoder or AI)
fails, it stops the other one and the **original** error is reported, not a confusing "cancelled".

### The detection list with 50,000 rows

- **Batching**: the AI finds results dozens of times per second. Updating the screen each time would swamp the
  waiter. Instead results go into a queue and the list is updated **4 times a second** in one go.
- **Virtualization**: the list only creates on-screen rows for the ~15 you can see, and **recycles** them while you
  scroll. 50,000 real rows would take hundreds of MB and seconds to lay out.

---

## 8. Using the graphics card (GPU)

A **GPU** is a chip with thousands of small calculators — perfect for AI maths. Your laptop has two: an **NVIDIA RTX
3500** (powerful, 11.7 GB of its own memory) and an **Intel Arc** (built into the CPU).

We use **DirectML**: Microsoft's way to run AI on *any* DirectX 12 graphics card (NVIDIA, AMD, Intel) without
installing NVIDIA's CUDA toolkit. The "Run on" box in the Detections panel lists CPU and each GPU; the default is the
GPU with the most memory.

How we find the GPUs: a small piece of low-level code asks Windows' graphics system (**DXGI**) for the list of graphics
adapters — in exactly the order DirectML numbers them (another API could list them in a different order and pick the
wrong card). If anything fails on the GPU, the app quietly falls back to the CPU and says so.

| YOLO face model | Time per frame | Whole pipeline |
|---|---|---|
| CPU | 23.4 ms | 39 frames/s |
| Intel Arc | 14.2 ms | — |
| **RTX 3500** | **4.7 ms (5× faster)** | **152 frames/s** |

Interesting effect: with the GPU the conveyor belt is often **empty** (0/4) — the AI now waits for the decoder. The
bottleneck moved. The next improvement would be letting the GPU decode video too (hardware decoding).

We also checked the GPU gives the **same answers**: fed an official test input, both GPUs matched the reference results
to 0.0000003.

---

## 9. Humans have the last word: review

AI makes two kinds of mistakes:

- **False positive**: it boxes something that isn't a face (a glove, a logo).
- **False negative**: it misses a real face (turned away, too small, hidden by a cap).

So AI results are only **proposals**:

```text
 AI finds a face ─► Pending ──(reviewer)──► Approved
                        │                └► Rejected  (false positive: not blurred)
                        └─ Pending faces ARE blurred by default ("fail-safe")
 AI missed a face ─► you draw a box yourself (manual region)
```

Why blur "pending" ones? If a reviewer forgets to review, it's much better to blur one extra patch than to publish a
face. Rejecting is an explicit decision.

Everything you approve, reject or draw is what the export uses — the preview shows exactly what the export will do
(same "planner" code for both).

---

## 10. Tracking: "is this the same face as a moment ago?"

There are two different kinds of tracking in the app. They solve different problems.

### A. Identity tracking (for the AI's detections) — "IoU tracker"

**Problem:** the AI finds "a face" on frame 100, "a face" on frame 112, "a face" on frame 124… The reviewer sees
hundreds of separate rows for what is really *one person*. Rejecting a false positive (say, a logo on a shirt) would
mean clicking hundreds of rows.

**Idea:** if a box on the new frame **overlaps a lot** with a box on the previous analysed frame, it's probably the
same object → give it the same **track number**.

"Overlaps a lot" is measured with **IoU (Intersection over Union)**:

```text
  ┌──────┐
  │  A ┌─┼────┐        IoU = shared area ÷ total area covered by both
  │    │█│    │        1.0 = identical boxes, 0 = no overlap
  └────┼─┘  B │        we say "same object" if IoU ≥ 0.3
       └──────┘
```

Now the list shows "Face #12 · Track 3", and the buttons **Approve track / Reject track** change every detection of
that person in one click. A track survives a few missed frames (the face was hidden briefly) and then closes.

**The problem with plain overlap, and the fix.** On your far-away cricket footage a face is only ~30 pixels wide, and
between two analysed frames (⅕ s) a walking player moves it half its own width or more. Then the old box and the new box
barely overlap, the IoU test fails, and the tracker thinks it's a *new* person: 262 tracks in one minute. Two classic
tricks fix it:

1. **Predict first.** Remember how fast each track was moving. Before comparing, move the old box to where the face
   *should* be now, and compare against that. (Like a goalkeeper watching where the ball is going, not where it was.)
2. **"Close enough" rule.** If the boxes still don't overlap, but the new box is close (within about 1.5 face widths of
   the prediction) and about the same size, treat it as the same face. Overlap matches always win over "close enough"
   matches.

```text
old box ● ──moving right──► predicted ◌        new box ▢ right next to ◌  →  same track
```

Result on one minute of your footage: 80 tracks instead of 262, and because tracks now continue, the boxes between
samples can slide with the face (chapter 7) — visible drift fell from 17.9 % to 4.3 % of faces.

### B. Visual tracking (for your hand-drawn boxes) — "follow this object"

**Problem:** you draw a box over a face the AI missed. The person walks; a fixed box no longer covers them.

**Idea:** a **visual tracker** looks at the *pixels* inside your box and finds where that patch moved on each next
frame. The resulting path is stored with your box, so preview and export follow the person automatically.

We use OpenCV's **KCF** tracker (fast, reliable; 21 ms per frame on your video, followed the umpire for 120/120
frames). OpenCV's **CSRT** tracker is usually more accurate, but in the Emgu CV version we use it **failed on every
test** (on synthetic and on your real footage), so it's kept only as an option to re-check after an upgrade. This is
documented rather than hidden. If the tracker loses the object, the box's time range is cut at that point.

```text
 A. IoU tracker:     detections ─► "same object?" by overlap ─► track numbers  (identity, no pixels)
 B. Visual tracker:  your box   ─► "where did this patch go?" by pixels ─► path (follows motion)
```

---

## 11. Hiding things: blur, pixelate, black box

Three **effects**, each a small class with the same shape (the **Strategy pattern**: interchangeable tools behind one
interface):

| Effect | What it looks like | Note |
|---|---|---|
| Gaussian blur | smooth smear | strength scales with box size, so small and large faces are equally unrecognisable |
| Pixelate | big blocks (mosaic) | average colour per block |
| Solid black | black rectangle | the only effect that removes **all** information |

Boxes are grown by a **padding** (default 20 %) because AI boxes hug the face tightly — hair, ears and motion blur stick
out.

### Speed trick: blur a small copy

A strong blur of a big area is expensive: every output dot averages hundreds of neighbours. Our first version took
**62 ms** per big region, making export crawl at 13 frames per second.

Trick: shrink the region to a tiny copy (48 px), blur the tiny copy, stretch it back. Blurring already throws away all
detail, so the result looks the same — but it's **41× faster (1.5 ms)**, and export went to **75 frames per second**.

```text
 slow:  big region ──── blur (huge kernel) ────► result            62 ms
 fast:  big region ─► shrink ─► blur small ─► stretch back ─► result  1.5 ms
```

---

## 12. Exporting the finished video

```text
 press Export
   │ ① take a SNAPSHOT of all decisions (you can keep reviewing; the export won't change mid-way)
   │ ② open the video with its own reader, go frame by frame from the start — never jump, never skip
   │ ③ for each frame: which boxes apply? → blur them → write the frame
   │ ④ write into a TEMPORARY file
   │ ⑤ check: frames written == frames read
   ▼ ⑥ rename the temporary file to the name you chose (only a complete file ever has that name)
```

- **Never modifies the source**: the source is only opened for reading, and choosing the same name as the source is
  refused. A test checks the source's fingerprint (SHA-256 hash) before and after.
- **Cancel or error**: the temporary file is deleted — no half-finished files.
- **Two ways to write the file.** Compressing the frames again ("encoding") is the slowest step, so who does it matters:
  - **With FFmpeg installed** (an optional free tool, fetched by `scripts/download-ffmpeg.ps1`): the app starts
    `ffmpeg.exe` as a separate program and hands it the finished pictures one by one through a pipe. FFmpeg compresses
    them to H.264 with the first encoder that works on this machine (it tries the NVIDIA chip, then Windows' own Media
    Foundation encoder, then Intel and AMD chips; on the development laptop that is Media Foundation, which runs on the
    processor), and it **keeps the sound**: the audio track is
    copied from the original, lined up with the first picture, and cut to end with the video. On the 1080p
    camcorder file this is about twice as fast (147–167 frames per second instead of 72–80) and the file is about a
    third of the size. Running FFmpeg as a separate program means a crash inside it cannot take the app down.
  - **Without FFmpeg**, or for AVI: the app uses OpenCV's own writer, as before — video only, **no sound**, H.264 in
    MP4 when Cisco's extra DLL is present (otherwise the older MPEG-4 codec), Motion-JPEG in AVI.
- **Honest limits**: without FFmpeg there is no sound in the export; subtitles are never carried over; on this laptop
  the NVIDIA encoder is not used yet because its driver is one version too old (updating the driver switches it on).

---

## 13. Saving your work (projects)

A `.vrproject` file stores your review — **not the video** (the video stays where it is; copying 4 GB into a project
would be absurd):

- where the video is + a fingerprint (size, resolution, frame count, fps) to recognise it later
- every detection and your decision on it, your manual boxes (and tracked paths), effect settings

Safety tricks:
- **Safe save**: write to a temporary file, then swap it in, so a crash mid-save never corrupts your previous project.
- **Moved video**: if the video isn't where it was, the app asks you to locate it and checks the fingerprint.
- **Version number** in the file: a file from a newer app version is refused with a clear message.

---

## 14. When things go wrong

| Situation | What the user sees | What's behind it |
|---|---|---|
| file missing / no permission | clear message | checked before handing the path to OpenCV |
| corrupt or unsupported video | "could not be opened as a video" | OpenCV reports "not opened"; translated |
| OpenCV DLL missing / wrong version | red banner at startup, app still runs | tested at startup, off the UI thread |
| AI model missing / broken | message + how to download it | model contents checked before first use |
| GPU can't run the model | runs on CPU, says so | automatic fallback |
| decode error while playing | red banner over the video, controls disabled | playback stops safely instead of crashing |
| unexpected bug | **one** error window (never a pile of them), details in the log | global handler, rate-limited |

Technical details (stack traces, native messages, thread numbers) go to the log file in
`%LOCALAPPDATA%\VisionRedact\logs`; users get sentences, not stack traces.

---

## 15. How we know it's fast: measuring

Rule: **no speed claim without a measurement.** Two tools:

1. The **Diagnostics tab** in the app: frames per second shown/decoded, decode time, shelf and belt fill levels,
   dropped frames, live frames, memory, GC counts, AI speed and device — measured live, twice a second.
2. The **benchmark tool** (`tools/VisionRedact.Benchmarks`), which prints the numbers used in
   [Performance.md](Performance.md).

Every optimization is written up as: **problem → measurement before → evidence → change → measurement after →
cost**. The big ones:

| # | Trick | Before | After |
|---|---|---|---|
| 1 | skip unsampled frames without colour conversion | 272 frames/s | 615 frames/s |
| 2 | reuse AI input buffers | 99 full GCs / 300 frames | 0 |
| 3 | blur a small copy | 62 ms / region, export 13 fps | 1.5 ms, export 75 fps |
| 4 | run the AI on the GPU | YOLO 23.4 ms | 4.7 ms |
| 5 | export through FFmpeg and the encoding hardware | 72–80 frames/s, no sound | 147–167 frames/s, with sound |

A **memory soak test** plays and analyses the video over and over and checks memory stays flat (it did: 184–238 MB,
ending at 195 MB).

---

## 16. How the code is organized

Think of it as layers of an onion. Inner layers know nothing about outer ones.

```text
 ┌─────────────────────────────────────────────────────────────┐
 │ Presentation.Wpf   screens, buttons, drawing (WPF + Prism)  │
 │  ┌───────────────────────────────────────────────────────┐  │
 │  │ Application   the "how": playback, analysis, export,  │  │
 │  │               jobs — and the INTERFACES it needs      │  │
 │  │  ┌───────────────────────────────────────────────┐    │  │
 │  │  │ Domain   the "what": frames/time maths, boxes,│    │  │
 │  │  │          detections, regions (pure C#)        │    │  │
 │  │  └───────────────────────────────────────────────┘    │  │
 │  └───────────────────────────────────────────────────────┘  │
 └─────────────────────────────────────────────────────────────┘
   plugged in from outside: Infrastructure.Video (OpenCV, FFmpeg), Infrastructure.AI (ONNX, GPU),
                            Infrastructure.Persistence (project files)
```

Why? The inner layers can be **tested without a screen, a GPU or OpenCV** (using fake decoders and fake detectors),
and a technology can be swapped by replacing one outer piece — the FFmpeg export writer was added as one new outer
class; the only inner change was a "finish the file" step (`Complete`) on the writer interface. 234 automated tests cover
timing, memory ownership, threading, AI maths, export correctness and more.

**Prism** is a WPF helper library: it builds the objects and connects them (**dependency injection**), splits the
window into swappable areas (**regions**), and passes rare app-wide announcements like "a video was opened"
(**EventAggregator** — never used for per-frame data, which would flood it).

---

## Glossary

| Term | Plain meaning |
|---|---|
| **Frame** | one still picture of the video |
| **FPS** | frames per second |
| **Resolution** | picture size in pixels (1920×1080) |
| **Codec** (H.264, MJPEG) | the method used to compress/decompress video |
| **Container** (.mp4, .mts, .avi) | the file "box" holding video, audio and info |
| **Keyframe / GOP** | a fully stored picture / the group of difference-frames after it |
| **Decode** | unpack a compressed frame into pixels |
| **Seek** | jump to another position in the video |
| **AVCHD / MTS** | camcorder video format (your files) |
| **OpenCV / Emgu CV** | C++ image/video library / its C# wrapper |
| **FFmpeg** | widely used free tool that reads and writes almost every video and audio format |
| **Encoder** | the part that compresses pictures into a codec; a hardware encoder does it on a dedicated chip |
| **Native memory** | memory used by C/C++ code, invisible to .NET's garbage collector |
| **Garbage collector (GC)** | .NET's automatic memory cleaner |
| **Dispose** | free an object's resources *now*, instead of waiting for the GC |
| **UI thread** | the single thread allowed to touch the window |
| **Dispatcher / BeginInvoke** | the UI thread's to-do list / "add this to it, don't wait" |
| **Deadlock** | two threads each waiting for the other, forever |
| **Task / async / await** | a promise of a result / code that can wait without blocking a thread |
| **Producer / consumer** | one part makes items, another uses them, with a buffer between |
| **Channel (bounded)** | a thread-safe conveyor belt with a maximum size |
| **Backpressure** | a full belt makes the fast side wait |
| **Latency / throughput** | how long one thing takes / how many things per second |
| **Drop (a frame)** | skip showing a late frame (preview only) |
| **Model / ONNX / ONNX Runtime** | trained AI file / its standard format / the engine running it |
| **Inference** | running the model on one input |
| **Tensor** | the grid of numbers fed into / out of a model |
| **Letterbox** | resize without distortion, filling the rest with bars |
| **Confidence** | the model's certainty (0–100 %) |
| **NMS** | remove duplicate overlapping boxes, keep the best |
| **IoU** | overlap of two boxes: shared area ÷ combined area |
| **Normalized coordinates** | positions as fractions (0–1) of the picture |
| **Sampling / hold** | analyse only some frames / reuse their result for the frames in between |
| **False positive / negative** | AI found something that isn't there / missed something that is |
| **Track** | the same object followed across frames |
| **KCF / CSRT** | two OpenCV visual trackers (KCF used; CSRT broken in this Emgu version) |
| **GPU / DirectML / DXGI** | graphics chip / Microsoft's GPU AI layer / Windows' graphics-adapter list |
| **Execution provider** | where ONNX Runtime runs the model (CPU, DirectML…) |
| **Virtualization (UI)** | only create list rows that are visible, reuse them while scrolling |
| **Strategy pattern** | interchangeable implementations behind one interface (blur/pixelate/black) |
| **Dependency injection (DI)** | objects get what they need handed to them, instead of creating it |
| **Prism / region / EventAggregator** | WPF helper library / swappable screen area / app-wide announcements |
| **Snapshot** | a frozen copy of data at one moment |
| **Atomic save / rename** | the new file appears all at once, never half-written |
| **Soak test** | run for a long time to see if memory grows |
| **Benchmark** | a repeatable speed measurement |
