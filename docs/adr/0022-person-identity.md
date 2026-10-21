# ADR-0022: Person Identity — Group Face Tracks into People

## Context
A tracker follows a face only while it stays on screen. When the same person reappears later, they get a new track,
so a reviewer sees "T3, T41, T88" for one officer. They need to work with people: name them, fix the grouping, and
redact or keep a whole person at once, with undo.

Grouping by face is **biometric processing**, and a wrong merge must never cause a leak.

## Decision
- **A separate pass after the AI scan (`IdentificationJob`), not a stage inside it.**
  - `FaceShotSelector` picks the 3 best faces per track from stored detections.
  - Only those frames are decoded: short gaps are skipped, long gaps seek.
  - `ArcFaceEmbedder` finds 5 landmarks with YuNet, aligns the face to the 112×112 InsightFace template, and runs
    ArcFace MobileFaceNet (512-d) on the CPU.
  - The track averages are clustered.
  - The scan's hot path is unchanged, this works on reopened projects, and re-running it is the "regroup".
- **Constrained average-linkage clustering (`TrackClusterer`).**
  - Two tracks on the same frame are different people, unless their boxes overlap (duplicates).
  - Human decisions become must-link and cannot-link rules.
  - The threshold (0.35 cosine) lives in `IdentityOptions` so it can be calibrated.
- **An immutable `PeopleIndex` in the Domain.**
  - It holds people with a stable number, an optional name, and track assignments marked Suggested or Confirmed.
  - Every edit (rename, merge, move, confirm, apply groups) returns a new index. Automatic grouping never changes
    confirmed assignments, and merging never rewrites TrackIds, so interpolation is untouched.
- **`IPeopleStore` with snapshot undo.**
  - `Apply(index → index)` runs under a lock with the newest index, and undo restores the previous snapshot.
- **Decisions stay on detections.**
  - "Redact all" approves the detections of all the person's tracks.
  - "Keep visible" rejects only the **confirmed** tracks, after a confirmation that names the suggested tracks it
    leaves redacted. A wrong suggestion can never un-blur a stranger.
- **Privacy by design.**
  - The People tab has an opt-in consent box.
  - Embeddings exist only inside the job.
  - Projects (schema 2) store names and membership, never fingerprints or face images. Thumbnails are cropped from
    the video on demand.
- **Models.**
  - YuNet (OpenCV Zoo, MIT) and InsightFace `w600k_mbf` (non-commercial research licence, acceptable for this demo),
    downloaded by `scripts/download-face-models.ps1` and pinned by SHA-256.
  - A commercial product would use a permissively licensed recognizer such as OpenCV Zoo SFace, behind the same
    `IFaceEmbedder` port.

## Consequences
- Verified on Tears of Steel: 103 tracks with a usable face were grouped into 35 suggested people. Rename, merge,
  undo and the overlay labels (person name and the detector's confidence, e.g. "Martha 81%") work in the app.
- Cost: a second short pass, and a small allocation per edit (immutable index).
- Identity is per video. Recognizing people across the files of a case would need a case-level store.
