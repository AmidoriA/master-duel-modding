"""Azamina Moa Regina (20509) - the user's custom over-frame art (gate 20509 -> 20509). Replaces the
official cut-in (apply with --override-official).

Art: a silver-haired woman with red eyes and a golden thorn crown, dark armoured gauntlets and boots,
golden thorny vines wrapped around her, large dark purple feathered wings, purple mist background.
Separate layers: her (without wings), the wing pair (slow flap), the mist band.
"""
import math
import os
import random

CARD_ID = 20509
NAME = "Azamina Moa Regina"
TEMPLATE = 19172
SCALE = 0.9
DUR = 2.0                 # the user wants every cut-in exactly 2.0 s
ART_CROP = (0, 0, 704, 760)

PROMPTS = {
    "body": "Keep only the silver-haired woman exactly as she is: her golden thorn crown, her red eyes, her dark armoured gauntlets and boots, "
            "her white outfit and the golden thorny vines wrapped around her. Same pose, position, size, colors and details. Remove her large "
            "dark purple feathered wings, remove the dark purple smoke, and remove the card frame, the border, the title bar, the text box and "
            "the purple background. Replace the entire background with a flat solid pure magenta color (#FF00FF). No shadow.",
    "wings": "Keep only the pair of large dark purple feathered wings behind the woman. Show both feathered wings complete and fully spread open "
             "to the left and to the right, symmetric, joined at the center, with nothing in front of them: remove the woman, her hair, her "
             "crown, the golden vines, the purple smoke, the card frame and the background. Same anime painting style. Replace the entire "
             "background with a flat solid pure green color (#00FF00).",
    "bg": "Remove the woman, her wings, the golden vines and the dark smoke completely, and remove the card frame, the border, the title bar "
          "and the text box. Keep only the soft violet and purple misty background, filling the whole image naturally, same painting style.",
}


def comfy(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    Image.open(os.path.join(work, "art.png")).convert("RGB").crop(ART_CROP).save(L("crop.png"))
    kit.upscale(L("crop.png"), L("up.png"))
    for tag in ("body", "wings", "bg"):
        kit.qwen_edit(L("up.png"), PROMPTS[tag], L(f"{tag}.png"), seed=7)
        kit.upscale(L(f"{tag}.png"), L(f"{tag}up.png"))


# anchors (fractions of the uncropped body image, from a grid overlay)
CROWN = (0.40, 0.17)        # centre of the thorn crown (the halo sits behind it)
EYE = (0.45, 0.31)          # her red eyes
WING_JOINT = (0.46, 0.50)   # behind the middle of her back (the wings meet there)
N_FEATHERS = 7


def feather_sprite(w=96, h=320):
    """A dark violet feather (white shaft line) for drifting feathers."""
    from PIL import Image, ImageDraw, ImageFilter
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pts = [(w / 2 + (w / 2 - 4) * math.sin(math.pi * k / 40) ** 0.8 * (1 if s > 0 else -1), h * k / 40)
           for s in (1, -1) for k in (range(41) if s > 0 else range(40, -1, -1))]
    d.polygon(pts, fill=(58, 36, 86, 255))
    d.line([(w / 2, 6), (w / 2, h - 2)], fill=(150, 120, 190, 255), width=3)
    return im.filter(ImageFilter.GaussianBlur(1.2))


def make_layers(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    body, meta = kit.key_flat_background(L("bodyup.png"), 2048, t0=0.12, t1=0.32, fade_right=(0.95, 1.0), fade_bottom=(0.9, 1.0),
                                         despill="magenta")
    meta.pop("_full")
    meta["pad"] = PAD = 120
    body = kit.pad(body, PAD)
    wings = kit.split_wing_pair(L("wingsup.png"))
    meta["wings"] = {k: v for k, v in wings.items() if k not in ("left", "right")}
    rng = random.Random(CARD_ID)
    meta["rng"] = rng
    band = kit.wide_band(L("bgup.png"), stars=0, rng=rng)
    return {"band": band, "body": body, "rim": kit.silhouette_glow(body, radius=24), "wing_l": wings["left"], "wing_r": wings["right"],
            "halo": kit.fx_magic_circle(), "glow": kit.fx_radial(), "sparkle": kit.fx_sparkle(), "feather": feather_sprite()}, meta


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.back_out
    rng = meta["rng"]
    body = images["body"]
    x0, y0, x1, y1 = meta["crop"]
    PAD = meta["pad"]
    H = 4300.0
    K = H / (body.height - 2 * PAD)
    W, HA = body.width * K, body.height * K
    HX, HY = 100.0, 120.0
    CLIMAX = 0.9

    def anchor(f):
        px, py = f[0] * meta["full_w"], f[1] * meta["full_h"]
        return HX + (px - (x0 + x1) / 2) * K, HY - (py - (y0 + y1) / 2) * K
    crown, eye, joint = anchor(CROWN), anchor(EYE), anchor(WING_JOINT)
    wm = dict(meta["wings"])
    wm["left"], wm["right"] = images["wing_l"], images["wing_r"]
    att_l, att_r = kit.wing_attachments(wm, 1.0 * W)
    rel = lambda p: (round(p[0] - HX, 1), round(p[1] - HY, 1))

    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "band", "parent": "root", "y": 120},
             {"name": "body", "parent": "root", "x": HX, "y": HY},
             {"name": "wing_l", "parent": "body", "x": rel(joint)[0] - 30, "y": rel(joint)[1]},
             {"name": "wing_r", "parent": "body", "x": rel(joint)[0] + 30, "y": rel(joint)[1]},
             {"name": "halo", "parent": "body", "x": rel(crown)[0], "y": rel(crown)[1]},
             {"name": "eye", "parent": "body", "x": rel(eye)[0], "y": rel(eye)[1]}]
    feathers = []
    for i in range(N_FEATHERS):                  # dark feathers drifting down behind her
        feathers.append({"t0": 0.2 + i * 0.17 + rng.uniform(0, 0.08), "fall": rng.uniform(600, 900), "spin": rng.uniform(-90, 90),
                         "ph": rng.uniform(0, 6.28), "size": rng.uniform(0.7, 1.1)})
        bones.append({"name": f"feather{i}", "parent": "root", "x": round(rng.choice([-1, 1]) * rng.uniform(1500, 3400), 1),
                      "y": round(rng.uniform(300, 1600), 1), "rotation": round(rng.uniform(-60, 60), 1)})

    slots = [{"name": "band", "bone": "band", "attachment": "band", "color": "8a72a800"}]
    slots += [{"name": f"feather{i}", "bone": f"feather{i}", "attachment": "feather", "color": "ffffff00"} for i in range(N_FEATHERS)]
    slots += [{"name": "haloglow", "bone": "halo", "attachment": "glow", "color": "ffc86000", "blend": "additive"},
              {"name": "wing_l", "bone": "wing_l", "attachment": "wing_l", "color": "ffffff00"},
              {"name": "wing_r", "bone": "wing_r", "attachment": "wing_r", "color": "ffffff00"},
              {"name": "halo", "bone": "halo", "attachment": "halo", "color": "ffd06a00", "blend": "additive"},
              {"name": "rim", "bone": "body", "attachment": "rim", "color": "b070ff00", "blend": "additive"},
              {"name": "body", "bone": "body", "attachment": "body", "color": "ffffff00"},
              {"name": "eyestar", "bone": "eye", "attachment": "sparkle", "color": "ff4a5a00", "blend": "additive"}]

    att = {"band": {"band": {"width": 9400, "height": 2700}},
           "haloglow": {"glow": {"width": 3200, "height": 3200}},
           "wing_l": {"wing_l": att_l}, "wing_r": {"wing_r": att_r},
           "halo": {"halo": {"width": 1700, "height": 1700}},
           "rim": {"rim": {"width": round(W * 1.03, 1), "height": round(HA * 1.03, 1)}},
           "body": {"body": {"width": round(W, 1), "height": round(HA, 1)}},
           "eyestar": {"sparkle": {"width": 420, "height": 420}}}
    for i in range(N_FEATHERS):
        att[f"feather{i}"] = {"feather": {"width": 150, "height": 500}}

    out_t0 = 1.62
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)
    FLAP_HZ = 0.9

    def band_m(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.3, ease_in)
        return {"scale": (1.0, sy)}

    def body_m(t):
        k = seg(t, 0.05, 0.6, 0, 1)
        kk = back_out(k, 1.03) if 0 < k < 1 else k
        y = lerp(650, 0, kk) + 20 * math.sin(t * 2.5)                    # descends, then floats
        s = lerp(1.05, 1.0, kk) * seg(t, 0.6, DUR, 1.0, 1.02)
        return {"translate": (seg(t, 0.6, DUR, 0, -30), y), "scale": (s, s)}

    def flap(side):
        sgn = 1 if side == "r" else -1

        def f(t):
            ph = 2 * math.pi * FLAP_HZ * t
            amp = 9 + 5 * math.exp(-((t - CLIMAX) / 0.25) ** 2)           # a fuller beat at the climax
            open_ = seg(t, 0.05, 0.5, 0.35, 1.0, ease_out)
            return {"rotate": sgn * (2 + amp * math.sin(ph)) * open_, "scale": ((0.92 + 0.08 * math.cos(ph)) * open_, 1.0)}
        return f

    def fall(p):
        def f(t):
            u = (t - p["t0"]) / 1.1
            uu = max(0, min(1, u))
            return {"translate": (120 * math.sin(uu * 5 + p["ph"]), -p["fall"] * uu), "rotate": p["spin"] * uu,
                    "scale": (p["size"] if 0 < u < 1 else 0.001,) * 2}
        return f

    halo_pulse = lambda t: 1 + 0.06 * math.exp(-((t - CLIMAX) / 0.15) ** 2)
    motions = {"band": band_m, "body": body_m, "wing_l": flap("l"), "wing_r": flap("r"),
               "halo": lambda t: {"rotate": seg(t, 0.2, DUR, 0, -30), "scale": (seg(t, 0.25, 0.7, 0.75, 1.0, ease_out) * halo_pulse(t),) * 2},
               "eye": lambda t: {"scale": (seg(t, 0.55, 0.7, 0.2, 1.0, ease_out),) * 2}}
    alpha = {"band": lambda t: seg(t, 0, 0.18, 0, 0.92) * fade_out(t),
             "wing_l": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "wing_r": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "haloglow": lambda t: seg(t, 0.25, 0.6, 0, 0.35) * fade_out(t),
             "halo": lambda t: seg(t, 0.25, 0.6, 0, 0.6) * fade_out(t),
             "rim": lambda t: (seg(t, 0.05, 0.3, 0, 0.55) if t < 0.3 else seg(t, 0.3, 0.5, 0.55, 0.22)) * fade_out(t),
             "body": lambda t: seg(t, 0.05, 0.22, 0, 1) * fade_out(t),
             "eyestar": lambda t: seg(t, 0.55, 0.61, 0, 0.9) * seg(t, 0.65, 0.9, 1, 0)}
    for i, p in enumerate(feathers):
        motions[f"feather{i}"] = fall(p)
        alpha[f"feather{i}"] = (lambda p: lambda t: seg(t, p["t0"], p["t0"] + 0.15, 0, 0.95) * seg(t, p["t0"] + 0.85, p["t0"] + 1.1, 1, 0)
                                * fade_out(t))(p)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
