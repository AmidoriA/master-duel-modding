"""Rebuild src/Floowan.Core/Resources/sam3/prompt_features.bin.

Floowan's SAM 3 cutout only ever asks for a fixed list of words, so it ships their SAM 3 text
features instead of the 700 MB text encoder and a CLIP tokenizer. Run this when the prompt list or
the SAM 3 export changes.

Needs: onnxruntime, numpy, transformers (CLIPTokenizer), and `text_fp16.onnx` (+ `.data`) from
https://huggingface.co/wW-Ww/sam3-ONNX in --model-dir.

    python scripts/sam3/make_prompt_features.py --model-dir <dir with text_fp16.onnx>

File format (little-endian): "S3PF", int32 version (1), int32 count, int32 seq_len, int32 dim, then per
prompt: int32 name byte length, UTF-8 name, int64[seq_len] attention mask, float32[seq_len * dim].
"""
import argparse
import os
import struct

import numpy as np
import onnxruntime as ort
from transformers import CLIPTokenizer

# Order matters only for readability; the C# side looks prompts up by name.
PROMPTS = ["character", "monster", "person", "creature",
           "weapon", "sword", "axe", "staff", "wings", "hat", "cape", "tail"]
SEQ_LEN = 32
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.join(HERE, "..", "..", "src", "Floowan.Core", "Resources", "sam3", "prompt_features.bin")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model-dir", required=True)
    ap.add_argument("--out", default=DEFAULT_OUT)
    args = ap.parse_args()

    tok = CLIPTokenizer.from_pretrained("openai/clip-vit-base-patch32")
    sess = ort.InferenceSession(os.path.join(args.model_dir, "text_fp16.onnx"), providers=["CPUExecutionProvider"])
    rows = []
    for name in PROMPTS:
        ids = tok(name)["input_ids"][:SEQ_LEN]
        mask = [1] * len(ids) + [0] * (SEQ_LEN - len(ids))
        ids = ids + [tok.eos_token_id] * (SEQ_LEN - len(ids))  # pad with the end token
        i = np.array([ids], np.int64)
        m = np.array([mask], np.int64)
        feats = sess.run(None, {"input_ids": i, "attention_mask": m})[0][0].astype(np.float32)
        rows.append((name, m[0], feats))
    dim = rows[0][2].shape[1]
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(b"S3PF")
        f.write(struct.pack("<iiii", 1, len(rows), SEQ_LEN, dim))
        for name, mask, feats in rows:
            raw = name.encode("utf-8")
            f.write(struct.pack("<i", len(raw)))
            f.write(raw)
            f.write(mask.astype("<i8").tobytes())
            f.write(feats.astype("<f4").tobytes())
    print(f"wrote {len(rows)} prompts ({SEQ_LEN}x{dim}) to {os.path.abspath(args.out)}")


if __name__ == "__main__":
    main()
