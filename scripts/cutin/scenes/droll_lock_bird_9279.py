"""Droll & Lock Bird (9279) - the user's custom over-frame art (Floowan OF), not Konami's illustration.

Art: white-haired girl with animal ears, black bat wings, black/purple outfit, a purple spiky
X-eyed creature on her raised white sleeve, a chain with a cross-in-wheel "lock" charm, red sky with
bats. The OF canvas is 704x1024 (RGB = full card incl. frame; alpha is a mask, not transparency).

Cut-in idea: the girl bursts out of a crimson band (like she breaks the card frame); a purple lock
seal clamps shut behind her at the "lock" moment while two chain streaks cross the screen.
The over-frame gate maps 9279 -> 9279, so the cut-in lives at P9279.
"""
import math
import os
import random

CARD_ID = 9279
NAME = "Droll & Lock Bird"
TEMPLATE = 19172
SCALE = 0.9               # whole-scene size in game (user: "slightly smaller", 2026-11-08)

ART_CROP = (0, 0, 704, 770)    # card canvas down to the top of the text box

PROMPTS = {
    "girl": "Keep only the white-haired anime girl exactly as she is: her animal ears, red eyes, black bat wings, black and purple outfit and frilly skirt, "
            "the purple spiky ball creature with X eyes on her raised white sleeve, the black glove, and the hanging chain with the cross-in-wheel charm. "
            "Same pose, position, size, colors and details. Remove everything else: remove the orange card frame, the border, the title bar, "
            "the text box and the red sky with bats. Replace the entire background with a flat solid pure green color (#00FF00). No shadow on the background.",
    "sky": "Remove the girl, her wings, the purple spiky creature, the chain and charm completely, and remove the orange card frame, the border, the title bar "
           "and the text box. Show only the red and crimson sky background with small dark bat silhouettes and soft pale clouds, filling the whole image "
           "naturally, in the same anime painting style.",
    # run on a crop around the creature (CREATURE_CROP); the cut-out girl layer loses these wings
    "wings": "Keep only the pair of feathered bird wings that are behind the purple spiky creature: white feathers at the top and long dark gray and black "
             "flight feathers below, in the same anime painting style. Show both wings complete and fully spread open to the left and to the right, "
             "symmetric, joined at the center, with nothing in front of them. Remove the creature, the girl, her ears and hair, the card frame and the "
             "red sky. Replace the entire background with a flat solid pure green color (#00FF00).",
}
CREATURE_CROP = (0.25, 0.0, 1.0, 0.5)    # fractions of up.png


def comfy(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    Image.open(os.path.join(work, "art.png")).convert("RGB").crop(ART_CROP).save(L("crop.png"))
    kit.upscale(L("crop.png"), L("up.png"))
    kit.qwen_edit(L("up.png"), PROMPTS["girl"], L("girl.png"), seed=7)
    kit.qwen_edit(L("up.png"), PROMPTS["sky"], L("sky.png"), seed=7)
    kit.upscale(L("girl.png"), L("girlup.png"))
    kit.upscale(L("sky.png"), L("skyup.png"))
    up = Image.open(L("up.png"))
    w, h = up.size
    up.crop((int(w * CREATURE_CROP[0]), int(h * CREATURE_CROP[1]), int(w * CREATURE_CROP[2]), int(h * CREATURE_CROP[3]))).save(L("creature_crop.png"))
    kit.qwen_edit(L("creature_crop.png"), PROMPTS["wings"], L("wings.png"), seed=21)
    kit.upscale(L("wings.png"), L("wingsup.png"))


# Anchor points as fractions of the (uncropped) girl image, read off a grid overlay.
CHARM = (0.69, 0.76)       # cross-in-wheel lock charm on the chain
CREATURE = (0.555, 0.235)  # purple spiky X-eyed ball on her sleeve
WING_JOINT = (0.59, 0.325) # where the creature's wings meet, behind its body (from the original art)
CHEST = (0.38, 0.60)
WING_SPAN = 3200.0         # skeleton units, tip to tip


def _split_wings(kit, path):
    """Key the wing pair and split it at the centre joint. Returns (left, right, meta) where each
    wing's root (the joint) sits at its inner edge, at meta['joint_y'] px from the top."""
    import numpy as np
    _, m = kit.key_flat_background(path, 2048, despill="green")
    full = m["_full"]
    A = np.asarray(full)[..., 3]
    cx = full.width // 2
    col = np.nonzero(A[:, cx - 8:cx + 8].max(axis=1) > 128)[0]
    jy = float(col.mean()) if len(col) else full.height / 2
    out = {}
    for side, box in (("left", (0, 0, cx, full.height)), ("right", (cx, 0, full.width, full.height))):
        half = full.crop(box)
        ys, xs = np.nonzero(np.asarray(half)[..., 3] > 6)
        y0, y1 = ys.min(), ys.max() + 1
        x0, x1 = (xs.min(), half.width) if side == "left" else (0, xs.max() + 1)
        out[side] = half.crop((x0, y0, x1, y1))
        out[side + "_joint_y"] = jy - y0
    out["span_px"] = out["left"].width + out["right"].width
    return out


def _silhouette_glow(layer, kit, size=640, radius=22, gain=1.5):
    import numpy as np
    from PIL import Image, ImageFilter
    a = np.asarray(layer.resize((size, round(size * layer.height / layer.width)), Image.LANCZOS))[..., 3]
    g = np.asarray(Image.fromarray(a).filter(ImageFilter.GaussianBlur(radius))).astype(np.float32) / 255
    al = np.clip(g * gain, 0, 1)
    return kit.img(np.dstack([np.ones(al.shape + (3,)), al]))


def _x_mark(kit, size=256):
    import numpy as np
    from PIL import Image, ImageDraw, ImageFilter
    im = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(im)
    m, w = size * 0.2, int(size * 0.16)
    d.line((m, m, size - m, size - m), fill=255, width=w)
    d.line((size - m, m, m, size - m), fill=255, width=w)
    glow = im.filter(ImageFilter.GaussianBlur(10))
    a = np.clip(np.asarray(im).astype(np.float32) / 255 + np.asarray(glow).astype(np.float32) / 255 * 0.9, 0, 1)
    return kit.img(np.dstack([np.ones((size, size, 3)), a]))


def make_layers(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    rng = random.Random(CARD_ID)
    girl, meta = kit.key_flat_background(L("girlup.png"), 2048, t0=0.12, t1=0.32, fade_bottom=(0.86, 0.995), despill="green")
    meta.pop("_full")
    sky = kit.wide_band(L("skyup.png"), stars=0, rng=rng)
    meta["rng"] = rng
    wings = _split_wings(kit, L("wingsup.png"))
    meta["wings"] = {k: v for k, v in wings.items() if not k in ("left", "right")}
    images = {"sky": sky, "girl": girl, "rim": _silhouette_glow(girl, kit), "seal": kit.fx_magic_circle(),
              "glow": kit.fx_radial(), "sparkle": kit.fx_sparkle(), "streak": kit.fx_streak(), "xmark": _x_mark(kit),
              "wing_l": wings["left"], "wing_r": wings["right"]}
    return images, meta


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, ease_inout, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.ease_inout, kit.back_out
    rng, DUR = meta["rng"], 2.0
    girl = images["girl"]
    x0, y0, x1, y1 = meta["crop"]
    GH = 4300.0                            # girl height in skeleton units: taller than the band, she breaks out of it
    K = GH / girl.height
    GW = girl.width * K
    GX, GY = -350.0, 330.0         # lifted so the charm clears the in-game card name bar (bottom ~23%)

    def anchor(f):   # fraction of the full girl image -> skeleton units
        px, py = f[0] * meta["full_w"], f[1] * meta["full_h"]
        return GX + (px - (x0 + x1) / 2) * K, GY - (py - (y0 + y1) / 2) * K
    charm, creature, chest, joint = anchor(CHARM), anchor(CREATURE), anchor(CHEST), anchor(WING_JOINT)
    wm = meta["wings"]
    WK = WING_SPAN / wm["span_px"]            # wing px -> units
    wl, wr = images["wing_l"], images["wing_r"]

    LOCK = 0.95       # the "lock" moment
    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "band", "parent": "root"},
             {"name": "seal", "parent": "root", "x": round(chest[0], 1), "y": round(chest[1], 1)},
             {"name": "girl", "parent": "root", "x": GX, "y": GY},
             {"name": "chainA", "parent": "root", "x": 0, "y": 150, "rotation": 17},
             {"name": "chainB", "parent": "root", "x": 0, "y": 150, "rotation": -17},
             {"name": "charm", "parent": "girl", "x": round(charm[0] - GX, 1), "y": round(charm[1] - GY, 1)},
             {"name": "creature", "parent": "girl", "x": round(creature[0] - GX, 1), "y": round(creature[1] - GY, 1)},
             # the lock bird's wings: bones at the joint behind its body, rotating around their roots
             {"name": "wing_l", "parent": "girl", "x": round(joint[0] - GX - 30, 1), "y": round(joint[1] - GY, 1)},
             {"name": "wing_r", "parent": "girl", "x": round(joint[0] - GX + 30, 1), "y": round(joint[1] - GY, 1)}]
    sparks, xmarks = [], []
    for i in range(14):
        ang, r = rng.uniform(0, 2 * math.pi), rng.uniform(1300, 2300)
        sparks.append({"t0": 0.35 + i * 0.085 + rng.uniform(-0.03, 0.03), "size": rng.uniform(0.6, 1.3),
                       "spin": rng.choice([-1, 1]) * rng.uniform(60, 150)})
        bones.append({"name": f"spark{i}", "parent": "root", "x": round(GX + r * math.cos(ang), 1), "y": round(GY + 300 + 0.75 * r * math.sin(ang), 1)})
    for i in range(6):   # "X" marks pop when the lock clamps (the creature's X eyes / denied search)
        ang = math.pi * (0.15 + i / 6 * 1.7) + rng.uniform(-0.15, 0.15)
        r = rng.uniform(2300, 3100)
        xmarks.append({"t0": LOCK + 0.03 + i * 0.05, "size": rng.uniform(0.8, 1.2), "rot": rng.uniform(-20, 20)})
        bones.append({"name": f"x{i}", "parent": "root", "x": round(chest[0] + r * math.cos(ang), 1), "y": round(chest[1] + 0.55 * r * math.sin(ang), 1)})

    slots = [{"name": "sky", "bone": "band", "attachment": "sky", "color": "b98a9600"},
             {"name": "seal", "bone": "seal", "attachment": "seal", "color": "b47cff00", "blend": "additive"},
             {"name": "rim", "bone": "girl", "attachment": "rim", "color": "a66bff00", "blend": "additive"},
             {"name": "wing_l", "bone": "wing_l", "attachment": "wing_l", "color": "ffffff00"},
             {"name": "wing_r", "bone": "wing_r", "attachment": "wing_r", "color": "ffffff00"},
             {"name": "girl", "bone": "girl", "attachment": "girl", "color": "ffffff00"},
             {"name": "chainA", "bone": "chainA", "attachment": "streak", "color": "cfa8ff00", "blend": "additive"},
             {"name": "chainB", "bone": "chainB", "attachment": "streak", "color": "cfa8ff00", "blend": "additive"},
             {"name": "charmglow", "bone": "charm", "attachment": "glow", "color": "ff5aa800", "blend": "additive"},
             {"name": "creatureglow", "bone": "creature", "attachment": "glow", "color": "b07cff00", "blend": "additive"}]
    slots += [{"name": f"spark{i}", "bone": f"spark{i}", "attachment": "sparkle", "color": "f2dcff00", "blend": "additive"} for i in range(14)]
    slots += [{"name": f"x{i}", "bone": f"x{i}", "attachment": "xmark", "color": "c38cff00", "blend": "additive"} for i in range(6)]
    slots.append({"name": "flash", "bone": "charm", "attachment": "glow", "color": "f0d8ff00", "blend": "additive"})

    att = {"sky": {"sky": {"width": 9200, "height": 2600}},
           "seal": {"seal": {"width": 3500, "height": 3500}},
           "rim": {"rim": {"width": round(GW * 1.03, 1), "height": round(GH * 1.03, 1)}},
           "girl": {"girl": {"width": round(GW, 1), "height": GH}},
           # attachment offset puts each wing's root (inner edge, joint height) on its bone
           "wing_l": {"wing_l": {"x": round(-wl.width * WK / 2, 1), "y": round((wl.height / 2 - wm["left_joint_y"]) * WK, 1),
                                 "width": round(wl.width * WK, 1), "height": round(wl.height * WK, 1)}},
           "wing_r": {"wing_r": {"x": round(wr.width * WK / 2, 1), "y": round((wr.height / 2 - wm["right_joint_y"]) * WK, 1),
                                 "width": round(wr.width * WK, 1), "height": round(wr.height * WK, 1)}},
           "chainA": {"streak": {"width": 9600, "height": 240}},
           "chainB": {"streak": {"width": 9600, "height": 240}},
           "charmglow": {"glow": {"width": 1000, "height": 1000}},
           "creatureglow": {"glow": {"width": 1100, "height": 1100}},
           "flash": {"glow": {"width": 6500, "height": 6500}}}
    for i in range(14):
        att[f"spark{i}"] = {"sparkle": {"width": 400, "height": 400}}
    for i in range(6):
        att[f"x{i}"] = {"xmark": {"width": 420, "height": 420}}

    out_t0 = 1.62
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)

    def band(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.25, ease_in)
        return {"scale": (1.0, sy), "translate": (seg(t, 0, DUR, 150, -150), 0)}

    def seal_m(t):
        s = seg(t, 0.15, 0.6, 1.55, 1.25, ease_out) if t < LOCK else seg(t, LOCK, LOCK + 0.14, 1.25, 0.98, ease_out) + 0.02 * math.sin((t - LOCK) * 10)
        return {"rotate": -40 * t + (seg(t, LOCK, LOCK + 0.14, 0, -45, ease_out)), "scale": (s, s)}

    def girl_m(t):
        k = seg(t, 0.05, 0.5, 0, 1)
        kk = back_out(k, 1.05) if 0 < k < 1 else k
        s = lerp(1.12, 1.0, kk) * seg(t, 0.5, DUR, 1.0, 1.03)
        y = lerp(-650, 0, kk) + seg(t, 0.5, DUR, 0, 70)
        r = lerp(-5, 0, kk)
        hit = 0.012 * math.exp(-max(0.0, t - LOCK) * 14) if t >= LOCK else 0.0   # small kick at the lock
        return {"translate": (seg(t, 0.5, DUR, 0, -60), y), "rotate": r, "scale": (s + hit, s + hit)}

    def chain(dirn, t0):
        def f(t):
            u = seg(t, t0, t0 + 0.26, -1, 1, ease_inout)
            a = math.radians(17 * dirn)
            return {"translate": (u * 5200 * math.cos(a), u * 5200 * math.sin(a))}
        return f

    FLAP_HZ = 1.8          # slow, gliding beat (user asked for slower than 3.4)

    def flap(side):
        """Wing beat: rotation around the root (up for the right wing = positive angle), with the
        wing foreshortened (narrower) on the upstroke. A big beat at the lock moment."""
        sgn = 1 if side == "r" else -1

        def f(t):
            ph = 2 * math.pi * FLAP_HZ * t
            amp = 28 + 14 * math.exp(-((t - LOCK) / 0.22) ** 2)        # stronger beat around the lock
            ang = 6 + amp * math.sin(ph)
            open_ = seg(t, 0.05, 0.4, 0.35, 1.0, ease_out)              # wings unfold as she arrives
            sx = (0.86 + 0.14 * math.cos(ph)) * open_
            return {"rotate": sgn * ang * open_, "scale": (sx, 1.0)}
        return f

    def pulse(t0, base=1.0, amp=0.18, speed=9):
        return lambda t: {"scale": ((base + amp * math.sin((t - t0) * speed)) if t > t0 else 0.3,) * 2}

    def flash_m(t):
        s = seg(t, LOCK, LOCK + 0.3, 0.15, 1.0, ease_out)
        return {"scale": (s, s)}

    def pop(p, life=0.4):
        def f(t):
            u = (t - p["t0"]) / life
            s = 0 if u <= 0 or u >= 1 else math.sin(math.pi * u) * p["size"]
            return {"scale": (max(s, 0.001), max(s, 0.001)), "rotate": p.get("spin", 0) * max(0, min(1, u)) + p.get("rot", 0)}
        return f

    alpha = {"sky": lambda t: seg(t, 0, 0.18, 0, 0.95) * fade_out(t),
             "seal": lambda t: (seg(t, 0.15, 0.5, 0, 0.35) + seg(t, LOCK, LOCK + 0.05, 0, 0.45) * seg(t, LOCK + 0.05, LOCK + 0.5, 1, 0)) * fade_out(t),
             "rim": lambda t: (seg(t, 0.05, 0.3, 0, 0.9) if t < 0.3 else seg(t, 0.3, 0.8, 0.9, 0.35) * (0.9 + 0.1 * math.sin(t * 8))) * fade_out(t),
             "girl": lambda t: seg(t, 0.05, 0.22, 0, 1) * fade_out(t),
             "wing_l": lambda t: seg(t, 0.08, 0.25, 0, 1) * fade_out(t),
             "wing_r": lambda t: seg(t, 0.08, 0.25, 0, 1) * fade_out(t),
             "chainA": lambda t: seg(t, LOCK - 0.1, LOCK - 0.04, 0, 0.9) * seg(t, LOCK + 0.1, LOCK + 0.2, 1, 0),
             "chainB": lambda t: seg(t, LOCK - 0.04, LOCK + 0.02, 0, 0.9) * seg(t, LOCK + 0.16, LOCK + 0.26, 1, 0),
             "charmglow": lambda t: seg(t, LOCK, LOCK + 0.08, 0, 0.9) * fade_out(t),
             "creatureglow": lambda t: seg(t, 0.55, 0.75, 0, 0.55) * (0.8 + 0.2 * math.sin(t * 6)) * fade_out(t),
             "flash": lambda t: seg(t, LOCK, LOCK + 0.04, 0, 0.6) * seg(t, LOCK + 0.04, LOCK + 0.4, 1, 0, ease_out)}
    motions = {"band": band, "seal": seal_m, "girl": girl_m, "chainA": chain(1, LOCK - 0.12), "chainB": chain(-1, LOCK - 0.06),
               "charm": pulse(LOCK, 1.0, 0.2), "creature": pulse(0.55, 1.0, 0.12, 6), "flash": None,
               "wing_l": flap("l"), "wing_r": flap("r")}
    motions["flash"] = flash_m
    # the flash shares the charm bone; give it its own bone so the pulse does not scale it
    bones.append({"name": "flashb", "parent": "girl", "x": round(charm[0] - GX, 1), "y": round(charm[1] - GY, 1)})
    slots[-1]["bone"] = "flashb"
    motions["flashb"] = motions.pop("flash")
    for i, p in enumerate(sparks):
        alpha[f"spark{i}"] = (lambda p: (lambda t: 1.0 if p["t0"] < t < p["t0"] + 0.4 else 0.0))(p)
        motions[f"spark{i}"] = pop(p)
    for i, p in enumerate(xmarks):
        alpha[f"x{i}"] = (lambda p: (lambda t: 0.95 * (1.0 if p["t0"] < t < p["t0"] + 0.5 else 0.0) * fade_out(t)))(p)
        motions[f"x{i}"] = pop(p, 0.5)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
