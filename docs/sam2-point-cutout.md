# SAM 2 paint selection (Custom OF)

Floowan can extract subjects from Master Duel card art with Meta
[Segment Anything 2](https://github.com/facebookresearch/sam2) by **painting a
masking region**, then running SAM 2 on prompts derived from that paint (bounding
box + positive points). New masks **union** onto an existing Card Art subject
instead of replacing it. This sits beside rembg `isnet-anime` and does not
replace the default Auto-create / Auto detect path.

## Architecture

| Piece | Choice |
|-------|--------|
| Runtime | **ONNX Runtime** in-process (`Microsoft.ML.OnnxRuntime`), same stack as rembg |
| Model | **SAM 2 Hiera-Tiny** encoder + decoder (samexporter / AnyLabeling layout) |
| Sidecar | Not required (no Python/PyTorch at runtime) |
| Storage | `%LOCALAPPDATA%\Floowan\models\sam2\` (not in git) |

Core API:

- `Sam2PointCutoutService.PrepareSubjectWithPaintedRegionAsync(path, paintMask)` →
  `(Image<Rgba32> source, Image<L8> mask)`
- `BuildPromptsFromPaintMask` → box corners (labels 2/3) + centroid/grid positives
- `UnionMasks(existing, addition)` → per-pixel max alpha for **Add more selection**

Point-only `PrepareSubjectWithPointAsync` remains for tests / advanced callers.

## Model download

**Automatic (app):** first use of **Add more selection…** downloads
`sam2_hiera_tiny.zip` (~148 MB) from
[vietanhdev/segment-anything-2-onnx-models](https://huggingface.co/vietanhdev/segment-anything-2-onnx-models),
verifies SHA-256, and extracts:

- `sam2_hiera_tiny.encoder.onnx`
- `sam2_hiera_tiny.decoder.onnx`

**Manual / CI:**

```powershell
pwsh -File scripts/download-sam2-models.ps1
# optional:
pwsh -File scripts/download-sam2-models.ps1 -ModelDirectory "$env:LOCALAPPDATA\Floowan\models\sam2"
```

Do **not** commit the zip or ONNX weights.

## Desktop UX

In **Custom overframe art**:

1. Choose a subject as usual (**Auto detect** rembg or **Pick manually…**), or skip
   and start from Advanced only.
2. Open **Advanced** → **Add more selection…** (hidden if `FLOOWAN_SAM2=0`).
3. Floowan extracts live card art, warms SAM 2 models off the UI thread, and opens
   the editor with two modes:
   - **Paint** — brush size, clear paint, left-drag amber strokes / right-drag erase.
     A circle cursor tracks brush diameter. Apply runs SAM 2 on the painted region.
   - **Click object** — click a point; SAM 2 runs async and shows a **cyan** preview
     mask in-editor. Click again replaces the preview. Apply uses that pending mask.
4. If a Card Art subject already exists, it is shown as a **green** highlight under
   pending overlays.
5. Apply installs or **unions** the SAM mask onto Card Art (same as before).

## Feature flag

| Env | Effect |
|-----|--------|
| unset / anything else | Advanced SAM entry shown |
| `FLOOWAN_SAM2=0` | Advanced SAM expander hidden |

## Larger backbones

Tiny is the desktop default. To try Small / Base+ / Large, export or download the
matching pair from the same Hugging Face repo into the models folder and point
`Sam2PointCutoutService` at those filenames (or extend the service). Expect
much larger downloads and slower CPU encode.

## License

See [THIRD_PARTY_NOTICES.txt](../THIRD_PARTY_NOTICES.txt) (Meta SAM 2 Apache-2.0;
ONNX redistributions per Hugging Face model card).
