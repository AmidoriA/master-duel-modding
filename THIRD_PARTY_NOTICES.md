# Third-party notices

## AssetsTools.NET / UABEA

- https://github.com/nesrak1/AssetsTools.NET
- https://github.com/nesrak1/UABEA
- License: MIT
- Used for Unity AssetBundle / Texture2D reading and writing.
- `classdata.tpk` is redistributed from UABEA release files for Unity type trees.

## AssetStudio (reference only)

- https://github.com/Perfare/AssetStudio
- Used as a research reference for Unity asset layouts; not linked into this build.

## Floowandereeze and Modding (inspiration)

- Qt app: https://github.com/Nauder/floowandereeze-and-modding-qt
- Catalog ETL: https://github.com/Nauder/floowandereeze-and-modding-etl
- License: GPL-3.0
- Inspiration / reference for Master Duel path layout (`LocalData/<id>/0000/...`)
  and the practical card-art replacement strategy (Texture2D → RGBA32, LZ4 pack, backups).
- Floowan’s shipped `database.db` is **not** a verbatim copy of upstream ETL output.
  Schema and contents are maintained in this project (Tools → DB update from a local
  Master Duel install), including Floowan-specific columns such as `card_type` and
  `created_at`. Early catalog ideas were inspired by Floowandereeze; treat the DB as
  Floowan’s own artifact going forward.
- This C# project reimplements related behaviors independently with AssetsTools.NET.
  It does not copy Floowandereeze Python source. If you distribute a combined work that
  incorporates Floowandereeze GPL code, comply with GPL-3.0 for that combined work.

## SixLabors.ImageSharp

- https://github.com/SixLabors/ImageSharp
- License: Apache-2.0
- Image load / resize / PNG export for preparation and previews.

## Microsoft.Data.Sqlite

- License: MIT
- Reads Floowan’s master `database.db` (and attached `user.db`).

## rembg / IS-Net anime (`isnet-anime`)

- https://github.com/danielgatis/rembg
- https://github.com/SkyTNT/anime-segmentation
- Licenses: rembg MIT; anime-segmentation model per upstream project
- The automatic over-frame generator independently ports rembg's documented
  `isnet-anime` preprocessing/postprocessing to .NET. The ONNX model is downloaded
  from the official rembg release on first use and is not stored in this repository.

## Microsoft.ML.OnnxRuntime

- https://github.com/microsoft/onnxruntime
- License: MIT
- Runs the `isnet-anime` background-removal model and Meta SAM 2 point-cutout
  encoder/decoder ONNX sessions locally.

## Meta Segment Anything 2 (SAM 2)

- https://github.com/facebookresearch/sam2
- Paper: https://ai.meta.com/research/publications/sam-2-segment-anything-in-images-and-videos/
- License: Apache-2.0 (see upstream `LICENSE` / `LICENSE_cctorch` notes on the Meta repo)
- Floowan uses an ONNX export of **SAM 2 Hiera-Tiny** (encoder + decoder) for
  interactive point-prompt subject cutouts in Custom over-frame art. Runtime is
  ONNX Runtime only — PyTorch is not required.
- Pre-exported ONNX packages are downloaded on first use from
  https://huggingface.co/vietanhdev/segment-anything-2-onnx-models
  (`sam2_hiera_tiny.zip`, SHA-256 verified) into `%LOCALAPPDATA%\Floowan\models\sam2`.
  Weights are not stored in this repository. Manual fetch:
  `pwsh -File scripts/download-sam2-models.ps1`.
- Export tooling reference: https://github.com/vietanhdev/samexporter (Apache-2.0).
  Floowan reimplements the documented encoder/decoder preprocess and point-prompt
  decode path in C#; it does not vendor samexporter Python sources.