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

## SkyTNT anime-segmentation (`isnetis`)

- Project: https://github.com/SkyTNT/anime-segmentation
- Model hub: https://huggingface.co/skytnt/anime-seg (`isnetis.onnx`)
- License: Apache-2.0 (upstream anime-segmentation repository)
- Floowan ports SkyTNT’s documented ONNX inference path (letterbox + `/255`
  normalize, crop pad, resize mask) to .NET via Microsoft.ML.OnnxRuntime.
  The ONNX weights are downloaded from Hugging Face on first use and are not
  stored in this repository. A previously downloaded rembg `isnet-anime.onnx`
  with the same MD5 is reused as `isnetis.onnx` when present.

## Microsoft.ML.OnnxRuntime

- https://github.com/microsoft/onnxruntime
- License: MIT
- Runs the SkyTNT `isnetis` anime subject-segmentation model locally.
