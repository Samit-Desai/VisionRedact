# Test videos used in the blog series

Downloaded 2026-10-10 into `media/pexels/` (git-ignored, never committed). Verified with ffprobe after download.

| Local file | Pexels id · title · author | Source file | Bytes | ffprobe |
|---|---|---|---|---|
| `crowd-crossing_4k60.mp4` | 34762082 · Vibrant City Scene with Crowded Urban Streets · Talha Uguz | `14737112_3840_2160_60fps.mp4` | 131,851,577 | H.264 High, 3840×2160, 60/1, 14.87 s |
| `crowd-crossing_1080p60.mp4` | same clip, Pexels 1080p rendition | `14737106_1920_1080_60fps.mp4` | 47,710,899 | H.264 High, 1920×1080, 60/1, 14.87 s |
| `street-flags_4k30.mp4` | 33778196 · Bustling Street Scene with Flags and People · Towfiqu barbhuiya | `14337937_3840_2160_30fps.mp4` | 56,674,964 | H.264 High, 3840×2160, 30000/1001, 14.01 s |
| `street-flags_1080p30.mp4` | same clip, Pexels 1080p rendition | `14337935_1920_1080_30fps.mp4` | 19,508,117 | H.264 High, 1920×1080, 30000/1001, 14.01 s |
| `group-closeup_4k30.mp4` | 6136964 · Group of People Posing and Smiling · William Fortunato | `6136964-uhd_3840_2160_30fps.mp4` | 25,886,528 | H.264 High, 3840×2160, 30000/1001, 11.95 s, 358 frames |
| `../tears_of_steel_1080p.mov` | Tears of Steel · (CC) Blender Foundation · mango.blender.org | `tears_of_steel_1080p.mov` | — | 1920×800, 24 fps, 12:14 |

## Licences

- **Pexels License** (all `media/pexels/` clips): free to use, including commercially, and editing is allowed. Attribution is not required but appreciated.
  - Identifiable people must not be shown in a bad or offensive light.
  - Don't imply that the people or the author endorse the product.
  - Don't sell or redistribute the clips unaltered.
  - Blurring faces in a redaction demo is fine.
  - Credit line used in posts and videos: "Footage: Talha Uguz, Towfiqu barbhuiya, William Fortunato / Pexels".
- **Tears of Steel**: CC BY 3.0. Credit "(CC) Blender Foundation | mango.blender.org". Blender and Mango logos and trademarks are excluded.

## Why these clips

The comparisons use pairs with the same content, so only one variable changes at a time:

| Pair | What changes |
|---|---|
| street-flags 1080p30 → 4K30 | resolution only, at 30 fps |
| crowd-crossing 1080p60 → 4K60 | resolution only, at 60 fps |
| street-flags vs crowd-crossing | frame rate and crowd density |
| group-closeup 4K30 | three large frontal faces, to show blur quality |
| Tears of Steel | long cinematic footage, people leaving and coming back (people grouping) |
