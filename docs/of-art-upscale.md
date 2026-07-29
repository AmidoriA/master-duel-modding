# Over-frame art upscale (Real-ESRGAN)

When composing **over-frame (OF)** art from Master Duel **512×512** (or
similar 512-class) illustrations, Floowan automatically upscales the clean
RGB + subject mask to **1024×1024** (Pendulum **512×683 → 1024×1366**) with a
local Real-ESRGAN anime model before Cover resize into the OF art hole. That
avoids soft Lanczos-only enlarge from 512 into the ~527×528 hole.

## Architecture

| Piece | Choice |
|-------|--------|
| Runtime | **ONNX Runtime** in-process (`Microsoft.ML.OnnxRuntime`), same stack as rembg / SAM 2 |
| Model | **RealESR-AnimeVideo-v3** ×4 ONNX (anime-tuned; BSD-3-Clause) |
| Scale path | Network ×4 → Lanczos down to OF ×2 target (512→1024) |
| Sidecar | Not required |
| Storage | `%LOCALAPPDATA%\Floowan\models\realesrgan\` (not in git) |

Core API: `ArtUpscaleService` — hooked from `AutoOverFrameArtService` (Auto-
create / rembg / alpha) and `Sam2PointCutoutService` after subject + mask are
ready. Live OF texture size stays **704×1024**; upscale is composition quality
only.

## Model download

**Automatic (app):** first OF create / rembg / SAM / manual alpha load that
needs upscale downloads `RealESR-AnimeVideo-v3_x4.onnx` (~2.4 MB) from
[tidus2102/Real-ESRGAN](https://huggingface.co/tidus2102/Real-ESRGAN),
verifies SHA-256, and caches it locally.

**Manual / CI:**

```powershell
pwsh -File scripts/download-realesrgan-model.ps1
# optional:
pwsh -File scripts/download-realesrgan-model.ps1 -ModelDirectory "$env:LOCALAPPDATA\Floowan\models\realesrgan"
```

Do **not** commit the ONNX weights.

## UX

Automatic when source art is 512-class — no extra toggle. Status text reports
`Upscaling illustration to 1024×1024 (Real-ESRGAN)…` on first composition after
rembg / SAM.

## Feature flag

| Env | Effect |
|-----|--------|
| unset / anything else | Upscale enabled |
| `FLOOWAN_OF_UPSCALE=0` | Skip Real-ESRGAN; compose with original 512 art (Lanczos Cover only) |

## Why animevideov3 ×4 then down

Yu-Gi-Oh card art is line/cel styled; the ×4 anime video v3 net is tiny (~2.4 MB)
and faster than full RealESRGAN_x2plus (~67 MB) while staying sharp. Running ×4
then Lanczos to ×2 preserves detail better than a single bilinear 512→1024.

## License

See [THIRD_PARTY_NOTICES.txt](../THIRD_PARTY_NOTICES.txt) (Real-ESRGAN BSD-3-Clause;
ONNX redistrib via Hugging Face model card).
