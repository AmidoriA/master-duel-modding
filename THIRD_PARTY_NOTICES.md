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

## Floowandereeze and Modding (Qt)

- https://github.com/Nauder/floowandereeze-and-modding-qt
- License: GPL-3.0
- Reference for Master Duel path layout (`LocalData/<id>/0000/...`), card `database.db` schema,
  and the practical card-art replacement strategy (Texture2D → RGBA32, LZ4 pack, backups).
- This C# project reimplements those behaviors independently with AssetsTools.NET.
  It does not copy Floowandereeze Python source. If you distribute a combined work that
  incorporates Floowandereeze GPL code, comply with GPL-3.0 for that combined work.

## SixLabors.ImageSharp

- https://github.com/SixLabors/ImageSharp
- License: Apache-2.0
- Image load / resize / PNG export for preparation and previews.

## Microsoft.Data.Sqlite

- License: MIT
- Reads Floowandereeze-compatible `database.db`.

## rembg / U-2-Net (`u2netp`)

- https://github.com/danielgatis/rembg
- https://github.com/xuebinqin/U-2-Net
- Licenses: rembg MIT; U-2-Net Apache-2.0
- The automatic over-frame generator independently ports rembg's documented
  `u2netp` preprocessing/postprocessing to .NET. The ONNX model is downloaded
  from the official rembg release on first use and is not stored in this repository.

## Microsoft.ML.OnnxRuntime

- https://github.com/microsoft/onnxruntime
- License: MIT
- Runs the `u2netp` background-removal model locally.
