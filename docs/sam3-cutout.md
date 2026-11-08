# SAM 3 + ViTMatte subject cutout (Auto OF)

**Over-frame → Subject cutout** picks the model that finds the card's character or monster
for **Auto-create & apply**, **Preview**, and Custom OF **Auto detect**. Custom OF has the same
picker under **Auto detect**; both edit one shared setting, and changing it there re-runs an
existing Auto detect cutout:

| Choice | How it works | Download | Time per card |
|--------|--------------|----------|---------------|
| isnet-anime (default) | rembg salient-object matting | ~170 MB | under 1 s (CPU) |
| SAM 3 + ViTMatte | SAM 3 finds the subject from words, ViTMatte places the edge | ~1.1 GB | ~1.5 s on a DirectX 12 GPU, ~17 s on CPU |

The setting is stored in `user.db` (`app_config.cutout_model`). Older user.db files may still
have an unused `cutout_held_items` column; held items are now always included.

## Pipeline (`Sam3CutoutService`)

1. Stretch the art to 1008×1008, scale to −1..1, run `vision_fp16.onnx` once.
2. For each subject word (`character`, `monster`, `person`, `creature`) run `detector.onnx`;
   keep queries with `sigmoid(logit) × sigmoid(presence) > 0.35` and union their masks
   (288² logits, bilinearly upsampled to the art size).
3. **Held items** (always on; the app has no switch for it): the same for `weapon`, `sword`,
   `axe`, `staff`, `wings`, `hat`, `cape`, `tail` at 0.40, kept only when the item touches the
   subject (within 6 px) and adds new area. It can pick up background effects (for example
   swirls read as "wings"); `Sam3CutoutService.PredictMask(includeHeldItems: false)` still
   turns it off for code and tests.
4. Trimap: ±12 px around the SAM 3 outline is "unknown"; ViTMatte
   (`vitmatte-small-composition-1k`) decides it at full resolution.

The over-frame composer still hard-cuts the mask at 140
(`OverFrameAutoArtComposer.MaskKeepThreshold`), because the OF texture's alpha is a mask,
not transparency. ViTMatte's value is where that hard edge falls (hair strands, fin tips),
not soft edges.

The text side of SAM 3 is never run in the app: the words are fixed, so their text
features ship as `src/Floowan.Core/Resources/sam3/prompt_features.bin` (393 KB). Rebuild it
with `scripts/sam3/make_prompt_features.py` if the word list changes.

## Runtime

- ONNX Runtime **DirectML** package on Windows (any DirectX 12 GPU, no CUDA install). If the
  GPU session cannot be created or fails at run time, the service falls back to CPU.
  `FLOOWAN_SAM3_CPU=1` forces CPU.
- Other models (isnet-anime, SAM 2, Real-ESRGAN) still use the CPU provider.
- Linux test builds reference the plain `Microsoft.ML.OnnxRuntime` package.

## Models

Downloaded on first use into `%LOCALAPPDATA%\Floowan\models\sam3cutout\`, each SHA-256
verified (a `.sha256` marker beside each file skips re-hashing later):

| File | Source |
|------|--------|
| `vision_fp16.onnx` + `.data` | [wW-Ww/sam3-ONNX](https://huggingface.co/wW-Ww/sam3-ONNX) |
| `detector.onnx` + `.data` | [wW-Ww/sam3-ONNX](https://huggingface.co/wW-Ww/sam3-ONNX) |
| `vitmatte-small-composition-1k.onnx` | [Xenova/vitmatte-small-composition-1k](https://huggingface.co/Xenova/vitmatte-small-composition-1k) (`onnx/model.onnx`) |

Do not commit the weights.

## License

SAM 3 is under Meta's SAM License; the prompt-feature file is a derivative, so
`licenses/SAM_LICENSE.txt` ships with the app (see `THIRD_PARTY_NOTICES.txt`). ViTMatte is MIT.
