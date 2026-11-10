"""16:9 start image for a video cut-in, from a card's scene or straight from its art.

Scene (a card that already has scripts/cutin/scenes/<scene>.py and a built work folder):
    python scripts/cutin/scene_still.py scripts/cutin/scenes/<scene>.py --work <work> --out <file.png>
        [--t 1.0] [--background <image>] [--dim 0.55] [--size 1920x1080]
  Renders the cut-in at --t with the in-game framing (no ComfyUI run). The cut-in band does not cover the
  whole screen, so a background image fills the rest: --background, else <work>/layers_src/bgup.png when it
  exists (centre 16:9 crop, slight blur, dimmed by --dim), else plain dark grey.

Art (any card; needs ComfyUI with Qwen-Image-Edit 2509):
    python scripts/cutin/scene_still.py --art <work>/art.png --out <file.png> --describe "<the character>"
        [--scenery "<background>"] [--crop 0,0,704,760] [--seed 7]
  Puts the art (cropped above the text box) in the middle of a grey 16:9 canvas and asks Qwen to remove the
  card frame, keep the character exactly as drawn and extend the background to the edges (--scenery, else
  the art's own scenery). Check the face and props against the art: Qwen sometimes adds details (an extra
  glowing eye on Diabellstar's helmet); say "do not add ..." in --describe and try other seeds.
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


def art_still(a, W, H):
    x0, y0, x1, y1 = (int(v) for v in a.crop.split(","))
    art = Image.open(a.art).convert("RGB").crop((x0, y0, x1, y1))
    art = art.resize((round(art.width * H / art.height), H), Image.LANCZOS)
    canvas = Image.new("RGB", (W, H), (128, 128, 128))
    canvas.paste(art, ((W - art.width) // 2, 0))
    stem = os.path.splitext(a.out)[0]
    canvas.save(stem + "_src.png")
    scenery = (f"Extend the background across the whole image to the left and right edges as {a.scenery}, filling the flat grey "
               "areas, and replace the old background around the character with the same scenery and matching lighting."
               if a.scenery else "Extend the background naturally across the whole image to the left and right edges, filling the "
               "flat grey areas with the same scenery and lighting.")
    prompt = (f"{a.describe} Remove the trading card frame completely: the border, the inner frame lines, the panels and the text box. "
              "Turn this into one wide landscape anime illustration. Keep the character exactly as drawn in the middle of the image: the "
              f"same pose, face, expression, hair, outfit, colors, size and art style. {scenery} Complete any part of the character that "
              "the frame cut off. No text, no border, no frame.")
    kit.qwen_edit(stem + "_src.png", prompt, stem + "_qwen.png", seed=a.seed)
    Image.open(stem + "_qwen.png").convert("RGB").resize((W, H), Image.LANCZOS).save(a.out)
    print(f"wrote {a.out} ({W}x{H}, Qwen seed {a.seed})")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scene", nargs="?")
    ap.add_argument("--work", help="scene mode: the scene's work folder (layers_src/ from an earlier build)")
    ap.add_argument("--art", help="art mode: the card art (monster_cutin.py extract-art)")
    ap.add_argument("--describe", default="", help="art mode: the character, so Qwen keeps the right details")
    ap.add_argument("--scenery", default="", help='art mode: new background, e.g. "a snowy winter forest"')
    ap.add_argument("--crop", default="0,0,704,760", help="art mode: x0,y0,x1,y1 of the picture above the text box")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--out", required=True)
    ap.add_argument("--t", type=float, default=1.0, help="moment of the animation to render (s)")
    ap.add_argument("--background")
    ap.add_argument("--dim", type=float, default=0.55)
    ap.add_argument("--size", default="1920x1080")
    a = ap.parse_args()
    W, H = (int(v) for v in a.size.lower().split("x"))
    if a.art:
        return art_still(a, W, H)
    if not (a.scene and a.work):
        ap.error("give a scene and --work, or --art")
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
