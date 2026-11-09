"""Render one moment of a scene_kit cut-in as a 16:9 still: the start image for a video cut-in.

    python scripts/cutin/scene_still.py scripts/cutin/scenes/<scene>.py --work <work> --out <file.png>
        [--t 1.0] [--background <image>] [--dim 0.55] [--size 1920x1080]

Uses the scene's layers in <work> (no ComfyUI run) and the in-game framing. The cut-in band does not cover
the whole screen, so a background image fills the rest: --background, else <work>/layers_src/bgup.png when it
exists (centre 16:9 crop, slight blur, dimmed by --dim), else plain dark grey.
"""
import argparse
import os
import sys

from PIL import Image, ImageEnhance, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scene_kit as kit  # noqa: E402


def cover(im, W, H):
    s = max(W / im.width, H / im.height)
    im = im.resize((max(W, round(im.width * s)), max(H, round(im.height * s))), Image.LANCZOS)
    x, y = (im.width - W) // 2, (im.height - H) // 2
    return im.crop((x, y, x + W, y + H))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scene")
    ap.add_argument("--work", required=True, help="the scene's work folder (layers_src/ from an earlier build)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--t", type=float, default=1.0, help="moment of the animation to render (s)")
    ap.add_argument("--background")
    ap.add_argument("--dim", type=float, default=0.55)
    ap.add_argument("--size", default="1920x1080")
    a = ap.parse_args()
    W, H = (int(v) for v in a.size.lower().split("x"))
    bg_src = a.background or os.path.join(a.work, "layers_src", "bgup.png")
    bg_path = None
    if os.path.exists(bg_src):
        bg = cover(Image.open(bg_src).convert("RGB"), W, H).filter(ImageFilter.GaussianBlur(2))
        bg_path = os.path.splitext(a.out)[0] + "_bg.png"
        ImageEnhance.Brightness(bg).enhance(a.dim).save(bg_path)
    scene = kit.load_scene(a.scene)
    images, meta = scene.make_layers(a.work, kit)
    rig = scene.rig(images, meta, kit)
    root = next(b for b in rig.bones if b["name"] == "root")
    root["scaleX"] = root.get("scaleX", 1) * kit.ROOT_SCALE
    root["scaleY"] = root.get("scaleY", 1) * kit.ROOT_SCALE
    grab = {}
    kit.save_previews = lambda frames, d, fps, stem: grab.update(frames=frames)
    rig.duration = a.t * 1.000001          # render exactly two moments: 0 and t
    kit.render_preview(rig, images, os.path.dirname(os.path.abspath(a.out)), W=W, H=H, fps=1 / max(a.t, 1e-3), background=bg_path)
    grab["frames"][-1].save(a.out)
    print(f"wrote {a.out} ({W}x{H}, t={a.t} s)")


if __name__ == "__main__":
    main()
