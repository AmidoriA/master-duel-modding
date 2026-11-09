"""Build a Monster Cut-in content folder from a video (flipbook) for `monster_cutin.py apply`.

    python scripts/cutin/video_cutin.py <video> --card <id> --out <work>/out [--name NAME] [--seconds 2.0]
        [--fps 30] [--px 1080] [--mode full|character] [--start S] [--end S] [--overscan 1.03]

Writes skeleton.json, hd.atlas/hd.png, sd.atlas/sd.png, meta.json (template P19172) and preview.mp4/.gif/
preview_sheet.png at the in-game framing. The whole clip (or --start/--end) is spread over --seconds.

Method (TLVv's cut-in guide, "Flipbook"): one slot whose region attachment is swapped frame_0..frame_N-1 at
even steps; all frames on one sheet (<= 16384 px a side; 2 px bled gutters so bilinear filtering never samples
a neighbouring frame). Size on screen = attachment size in skeleton units (full mode fills the game camera,
plus --overscan). Length = the template's Timeline clip, which monster_cutin sets from the last key time, so
the final key sits at --seconds. 60 frames of 1920x1080 make an 11544x10840 sheet: about 90 MB of BC7 in the
HD bundle. --mode character (rembg isnet-anime cut-out + pop-in zoom) is untested in game.
"""
import argparse
import json
import math
import os
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scene_kit as kit  # noqa: E402

Image.MAX_IMAGE_PIXELS = None    # the frame sheet is ~125 MP on purpose

TEMPLATE = 19172
MAX_SIDE = 16384
GUTTER = 2           # px on each side of a frame (bled edge colour)


def read_frames(path, n_want, start, end):
    import cv2
    cap = cv2.VideoCapture(path)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    first = int(round(start * fps))
    last = total - 1 if end is None else min(total - 1, int(round(end * fps)) - 1)
    span = last - first + 1
    n = min(n_want, span)
    picks = [first + int(i * span / n) for i in range(n)]
    want, out, idx = set(picks), {}, 0
    cap.set(cv2.CAP_PROP_POS_FRAMES, first)
    idx = first
    while idx <= last and len(out) < len(want):
        ok, fr = cap.read()
        if not ok:
            break
        if idx in want:
            out[idx] = Image.fromarray(cv2.cvtColor(fr, cv2.COLOR_BGR2RGB))
        idx += 1
    cap.release()
    frames = [out[i] for i in picks if i in out]
    return frames, fps, total, span / fps


def cutout(frames):
    """character mode: rembg isnet-anime per frame + green despill (guide B2 'Konami look')."""
    from rembg import new_session, remove
    sess = new_session("isnet-anime")
    res = []
    for k, f in enumerate(frames):
        a = np.asarray(remove(f, session=sess)).astype(np.int16)
        r, g, b = a[..., 0], a[..., 1], a[..., 2]
        green = (g > r + 45) & (g > b + 45)
        a[..., 3][green] = 0
        a[..., 1] = np.minimum(g, np.maximum(r, b))
        res.append(Image.fromarray(a.clip(0, 255).astype(np.uint8), "RGBA"))
        print(f"  cutout {k + 1}/{len(frames)}", end="\r", flush=True)
    print()
    return res


def grid_for(n, cw, ch):
    best = None
    for cols in range(1, n + 1):
        rows = math.ceil(n / cols)
        W, H = cols * cw, rows * ch
        if W > MAX_SIDE or H > MAX_SIDE:
            continue
        key = (W * H, abs(W - H))
        if best is None or key < best[0]:
            best = (key, cols, rows)
    return None if best is None else best[1:]


def bleed(im, g):
    """pad an RGBA frame by g px, repeating its edge pixels (no dark/bright fringe under bilinear)."""
    a = np.asarray(im)
    return Image.fromarray(np.pad(a, ((g, g), (g, g), (0, 0)), mode="edge"), "RGBA")


def pack(frames, out_dir, page_name):
    fw, fh = frames[0].size
    cw, ch = fw + 2 * GUTTER, fh + 2 * GUTTER
    g = grid_for(len(frames), cw, ch)
    if g is None:
        raise SystemExit("frames do not fit a 16384 sheet; lower --px or --fps")
    cols, rows = g
    W, H = cols * cw, rows * ch
    print(f"  sheet {cols}x{rows} = {W}x{H} for {len(frames)} frames of {fw}x{fh}")
    pages = {}
    for tag, scale, label in (("hd", 1.0, "1"), ("sd", 0.5, "0.5")):
        PW, PH = round(W * scale), round(H * scale)
        canvas = Image.new("RGBA", (PW, PH), (0, 0, 0, 0))
        lines = [page_name, f"size:{PW},{PH}", "filter:Linear,Linear", "pma:true", f"scale:{label}"]
        for i, f in enumerate(frames):
            c, r = i % cols, i // cols
            x0, y0 = round(c * cw * scale), round(r * ch * scale)
            gs = max(1, round(GUTTER * scale))
            w, h = round(fw * scale), round(fh * scale)
            src = f if scale == 1.0 else f.resize((w, h), Image.LANCZOS)
            canvas.paste(bleed(kit.premultiply(src), gs), (x0 + round(GUTTER * scale) - gs, y0 + round(GUTTER * scale) - gs))
            lines += [f"frame_{i}", f"bounds:{x0 + round(GUTTER * scale)},{y0 + round(GUTTER * scale)},{w},{h}"]
        canvas.save(os.path.join(out_dir, f"{tag}.png"), compress_level=1)
        open(os.path.join(out_dir, f"{tag}.atlas"), "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
        pages[tag] = (PW, PH)
    return pages


def skeleton(n, aspect, seconds, mode, overscan, fade_in, fade_out, card):
    H = kit.GAME_VIEW_H * overscan if mode == "full" else kit.GAME_VIEW_H * 0.95
    W = H * aspect
    step = seconds / n
    # n frames at even steps; the last key at `seconds` holds the last frame so the clip length is exact
    att_keys = [{"time": round(i * step, 4), "name": f"frame_{i}"} for i in range(n)]
    att_keys.append({"time": round(seconds, 4), "name": f"frame_{n - 1}"})
    T = [0.0, fade_in, max(fade_in, seconds - fade_out), seconds]
    A = [0.0 if fade_in > 0 else 1.0, 1.0, 1.0, 0.0 if fade_out > 0 else 1.0]
    rgba = [{"time": round(t, 4), "color": "ffffff%02x" % round(a * 255)} for t, a in zip(T, A)]
    anim = {"slots": {"frame": {"attachment": att_keys, "rgba": rgba}}}
    if mode == "character":       # pop-in zoom measured from Disaster Leo (guide B2) + slow upward drift
        ks = [(0.0, 0.70), (0.229 * seconds, 0.95), (seconds, 1.10)]
        anim["bones"] = {"root": {"scale": [{"time": round(t, 4), "x": s, "y": s, **({"curve": [t + 0.08 * seconds, 0.88, t + 0.1 * seconds, 0.95, t + 0.08 * seconds, 0.88, t + 0.1 * seconds, 0.95]} if i == 0 else {})}
                                            for i, (t, s) in enumerate(ks)]},
                         "Body": {"translate": [{"time": 0, "x": 0, "y": 0}, {"time": round(seconds, 4), "x": 0, "y": round(0.03 * H, 2)}]}}
    skel = {"skeleton": {"hash": f"video{card}", "spine": "4.2.20", "x": -W / 2, "y": -H / 2, "width": round(W, 2), "height": round(H, 2),
                         "fps": 60, "images": "./images/", "audio": ""},
            "bones": [{"name": "root"}, {"name": "Body", "parent": "root"}],
            "slots": [{"name": "frame", "bone": "Body", "attachment": "frame_0"}],
            "skins": [{"name": "default", "attachments": {"frame": {f"frame_{i}": {"width": round(W, 2), "height": round(H, 2)} for i in range(n)}}}],
            "animations": {"animation": anim}}
    return skel, (W, H)


def preview(frames, size_units, seconds, fade_in, fade_out, out_dir, fps=30, PW=960, PH=540):
    """preview.mp4/.gif/preview_sheet.png at the in-game framing (same view as scene_kit's preview).
    Character mode is drawn without its zoom; check that one with spine_preview/serve.py."""
    ppu = PH / kit.GAME_VIEW_H
    dw, dh = max(1, round(size_units[0] * ppu)), max(1, round(size_units[1] * ppu))
    small = [f.resize((dw, dh), Image.LANCZOS) for f in frames]
    bg = Image.new("RGBA", (PW, PH), (40, 44, 52, 255))
    step, out = seconds / len(frames), []
    for fi in range(int(round(seconds * fps)) + 1):
        t = fi / fps
        al = 1.0
        if fade_in > 0 and t < fade_in:
            al = t / fade_in
        if fade_out > 0 and t > seconds - fade_out:
            al = min(al, max(0.0, (seconds - t) / fade_out))
        im = small[min(len(small) - 1, int(t / step + 1e-6))]
        layer = Image.new("RGBA", (PW, PH), (0, 0, 0, 0))
        layer.paste(im, ((PW - dw) // 2, (PH - dh) // 2))
        if al < 1.0:
            layer.putalpha(Image.eval(layer.getchannel("A"), lambda v: int(v * al)))
        out.append(Image.alpha_composite(bg, layer).convert("RGB"))
    kit.save_previews(out, out_dir, fps, "preview")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video")
    ap.add_argument("--card", type=int, required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--name", default="")
    ap.add_argument("--seconds", type=float, default=2.0, help="cut-in length (the clip is spread over it)")
    ap.add_argument("--fps", type=float, default=30, help="flipbook frame rate (frames = seconds * fps, capped by the sheet)")
    ap.add_argument("--px", type=int, default=1080, help="frame height in px")
    ap.add_argument("--mode", choices=["full", "character"], default="full")
    ap.add_argument("--start", type=float, default=0.0)
    ap.add_argument("--end", type=float, default=None)
    ap.add_argument("--overscan", type=float, default=1.03, help="full mode: >1 hides the frame edges at the screen border")
    ap.add_argument("--fade-in", type=float, default=0.08)
    ap.add_argument("--fade-out", type=float, default=0.25)
    a = ap.parse_args()
    t0 = time.time()
    os.makedirs(a.out, exist_ok=True)
    frames, vfps, total, clip_len = read_frames(a.video, max(1, round(a.seconds * a.fps)), a.start, a.end)
    print(f"video {vfps:.2f} fps, {total} frames; using {len(frames)} frames from {clip_len:.2f} s -> played over {a.seconds:.2f} s"
          f" ({len(frames) / a.seconds:.1f} fps on screen, speed x{clip_len / a.seconds:.2f})")
    w0, h0 = frames[0].size
    fh = min(a.px, h0)
    fw = round(w0 * fh / h0)
    frames = [f.convert("RGB").resize((fw, fh), Image.LANCZOS).convert("RGBA") if (fw, fh) != (w0, h0) else f.convert("RGBA") for f in frames]
    # fit the sheet: shrink frames until N fit in 16384 x 16384
    while grid_for(len(frames), frames[0].width + 2 * GUTTER, frames[0].height + 2 * GUTTER) is None:
        fh = int(fh * 0.95)
        fw = round(w0 * fh / h0)
        frames = [f.resize((fw, fh), Image.LANCZOS) for f in frames]
    if a.mode == "character":
        frames = cutout(frames)
    t1 = time.time()
    pages = pack(frames, a.out, f"P{TEMPLATE}.png")
    t2 = time.time()
    skel, (W, H) = skeleton(len(frames), fw / fh, a.seconds, a.mode, a.overscan, a.fade_in, a.fade_out, a.card)
    json.dump(skel, open(os.path.join(a.out, "skeleton.json"), "w", encoding="utf-8", newline="\n"), separators=(",", ":"))
    json.dump({"card_id": a.card, "name": a.name, "template": TEMPLATE, "duration": a.seconds, "source": "video",
               "video": os.path.basename(a.video), "frames": len(frames), "frame_px": [fw, fh], "pages": pages, "mode": a.mode,
               "built": time.strftime("%Y-%m-%dT%H:%M:%S")},
              open(os.path.join(a.out, "meta.json"), "w", encoding="utf-8"), indent=1)
    print(f"  skeleton attachment {W:.0f} x {H:.0f} units (game view height {kit.GAME_VIEW_H:.0f})")
    preview(frames, (W, H), a.seconds, a.fade_in, a.fade_out, a.out)
    print(f"timing: read+resize {t1 - t0:.1f}s, pack {t2 - t1:.1f}s, preview {time.time() - t2:.1f}s")
    print(f"built {a.name or a.card} -> {a.out}\n  preview: {os.path.join(a.out, 'preview.mp4')}")


if __name__ == "__main__":
    main()
