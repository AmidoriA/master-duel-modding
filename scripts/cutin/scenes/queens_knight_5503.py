"""Queen's Knight (5503) - the user's custom over-frame art (gate 5503 -> 5503).

Art: an anime girl with long white hair, a red bunny-eared skull mask, red eyes, a black and red gothic
outfit with a red coat, a small dark bat wing, and a large crimson sword; blue thorny forest behind.
"""
import math
import os
import random

CARD_ID = 5503
NAME = "Queen's Knight"
TEMPLATE = 19172
SCALE = 0.9
DUR = 2.0                 # the user wants every cut-in exactly 2.0 s
ART_CROP = (0, 0, 704, 760)

PROMPTS = {
    "hero": "Keep only the anime girl exactly as she is: her long white hair, the red bunny-eared skull mask on her head, her red eyes, her "
            "black and red gothic outfit with the red coat, the small dark bat wing behind her, and the large crimson sword at the right. Same "
            "pose, position, size, colors and details. Remove the card frame, the border, the title bar, the text box and the blue forest "
            "background. Replace the entire background with a flat solid pure green color (#00FF00). No shadow.",
    "body": "Keep only the anime girl exactly as she is: her long white hair, the red bunny-eared skull mask on her head, her red eyes, her "
            "black and red gothic outfit with the red coat, and the large crimson sword at the right. Same pose, position, size, colors and "
            "details. Remove both dark bat wings behind her, and remove the card frame, the border, the title bar, the text box and the blue "
            "forest background. Replace the entire background with a flat solid pure green color (#00FF00). No shadow.",
    # run on the green hero image (it already shows both wings); on the card art Qwen kept the frame and drew a whole bat
    "wings": "Remove the girl completely: her hair, her bunny mask, her face, her body, her coat, her outfit and the red sword. Keep only "
             "the two dark purple bat wings exactly where they are, complete, spread to the left and to the right, with the hidden parts "
             "behind her filled in naturally. No bat body, no head. Keep the flat solid pure green background (#00FF00) everywhere else.",
    "bg": "Remove the girl, her hair, her mask, her coat, her wing and her sword completely, and remove the card frame, the border, the title "
          "bar and the text box. Keep only the blue thorny forest with dark branches and glowing cyan light, filling the whole image "
          "naturally, same painting style.",
}


def comfy(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    Image.open(os.path.join(work, "art.png")).convert("RGB").crop(ART_CROP).save(L("crop.png"))
    kit.upscale(L("crop.png"), L("up.png"))
    for tag in ("hero", "bg", "body"):
        kit.qwen_edit(L("up.png"), PROMPTS[tag], L(f"{tag}.png"), seed=7)
        kit.upscale(L(f"{tag}.png"), L(f"{tag}up.png"))
    kit.qwen_edit(L("hero.png"), PROMPTS["wings"], L("wings.png"), seed=7)
    kit.upscale(L("wings.png"), L("wingsup.png"))


# anchors (fractions of the uncropped body image, from a grid overlay)
EYE = (0.43, 0.33)          # her red eye
GUARD = (0.77, 0.88)        # sword guard
TIP = (0.99, 0.32)          # sword tip (the art crops it at the right edge)
WING_JOINT = (0.45, 0.76)   # at her hips, by the belt (the user wants hip wings)
N_MOTES, N_PETALS = 6, 5


def make_layers(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    body, meta = kit.key_flat_background(L("bodyup.png"), 2048, t0=0.12, t1=0.32, fade_right=(0.95, 1.0), fade_bottom=(0.9, 1.0),
                                         despill="green")
    meta.pop("_full")
    meta["pad"] = PAD = 120
    body = kit.pad(body, PAD)
    wings = kit.split_wing_pair(L("wingsup.png"))
    meta["wings"] = {k: v for k, v in wings.items() if k not in ("left", "right")}
    rng = random.Random(CARD_ID)
    meta["rng"] = rng
    band = kit.wide_band(L("bgup.png"), stars=0, rng=rng)
    return {"band": band, "body": body, "rim": kit.silhouette_glow(body, radius=24), "wing_l": wings["left"], "wing_r": wings["right"],
            "streak": kit.fx_streak(), "glow": kit.fx_radial(), "sparkle": kit.fx_sparkle()}, meta


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.back_out
    rng = meta["rng"]
    body = images["body"]
    x0, y0, x1, y1 = meta["crop"]
    PAD = meta["pad"]
    H = 4300.0
    K = H / (body.height - 2 * PAD)
    W, HA = body.width * K, body.height * K
    HX, HY = -150.0, 160.0
    CLIMAX = 0.85

    def anchor(f):
        px, py = f[0] * meta["full_w"], f[1] * meta["full_h"]
        return HX + (px - (x0 + x1) / 2) * K, HY - (py - (y0 + y1) / 2) * K
    eye, guard, tip, joint = anchor(EYE), anchor(GUARD), anchor(TIP), anchor(WING_JOINT)
    blade_len = math.hypot(tip[0] - guard[0], tip[1] - guard[1])
    blade_rot = math.degrees(math.atan2(tip[1] - guard[1], tip[0] - guard[0]))
    wm = dict(meta["wings"])
    wm["left"], wm["right"] = images["wing_l"], images["wing_r"]
    att_l, att_r = kit.wing_attachments(wm, 0.95 * W)
    rel = lambda p: (round(p[0] - HX, 1), round(p[1] - HY, 1))

    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "band", "parent": "root", "y": 120},
             {"name": "body", "parent": "root", "x": HX, "y": HY},
             {"name": "wing_l", "parent": "body", "x": rel(joint)[0] - 60, "y": rel(joint)[1]},
             {"name": "wing_r", "parent": "body", "x": rel(joint)[0] + 60, "y": rel(joint)[1]},
             {"name": "eye", "parent": "body", "x": rel(eye)[0], "y": rel(eye)[1]},
             {"name": "blade", "parent": "body", "x": rel(guard)[0], "y": rel(guard)[1], "rotation": round(blade_rot, 2)},
             {"name": "glint", "parent": "blade"},
             {"name": "tip", "parent": "blade", "x": round(blade_len * 0.97, 1)}]
    motes, petals = [], []
    for i in range(N_MOTES):                    # cyan motes from the forest, rising slowly (behind her)
        motes.append({"t0": 0.15 + i * 0.2 + rng.uniform(0, 0.08), "rise": rng.uniform(350, 600), "size": rng.uniform(0.5, 0.9)})
        bones.append({"name": f"mote{i}", "parent": "root", "x": round(rng.choice([-1, 1]) * rng.uniform(1300, 3300), 1),
                      "y": round(rng.uniform(-1500, 300), 1)})
    for i in range(N_PETALS):                   # crimson sparks drifting down (behind her)
        petals.append({"t0": 0.3 + i * 0.22 + rng.uniform(0, 0.08), "fall": rng.uniform(400, 700), "size": rng.uniform(0.45, 0.75)})
        bones.append({"name": f"petal{i}", "parent": "root", "x": round(rng.choice([-1, 1]) * rng.uniform(1200, 3200), 1),
                      "y": round(rng.uniform(200, 1500), 1)})

    slots = [{"name": "band", "bone": "band", "attachment": "band", "color": "6c84aa00"}]
    slots += [{"name": f"mote{i}", "bone": f"mote{i}", "attachment": "sparkle", "color": "7ce0ff00", "blend": "additive"} for i in range(N_MOTES)]
    slots += [{"name": f"petal{i}", "bone": f"petal{i}", "attachment": "sparkle", "color": "ff3c5a00", "blend": "additive"} for i in range(N_PETALS)]
    slots += [{"name": "wing_l", "bone": "wing_l", "attachment": "wing_l", "color": "ffffff00"},
              {"name": "wing_r", "bone": "wing_r", "attachment": "wing_r", "color": "ffffff00"},
              {"name": "rim", "bone": "body", "attachment": "rim", "color": "ff4a6a00", "blend": "additive"},
              {"name": "body", "bone": "body", "attachment": "body", "color": "ffffff00"},
              {"name": "glint", "bone": "glint", "attachment": "streak", "color": "ffe8ee00", "blend": "additive"},
              {"name": "tipstar", "bone": "tip", "attachment": "sparkle", "color": "fff0f400", "blend": "additive"},
              {"name": "eyestar", "bone": "eye", "attachment": "sparkle", "color": "ff587000", "blend": "additive"}]

    att = {"band": {"band": {"width": 9400, "height": 2700}},
           "wing_l": {"wing_l": att_l}, "wing_r": {"wing_r": att_r},
           "rim": {"rim": {"width": round(W * 1.03, 1), "height": round(HA * 1.03, 1)}},
           "body": {"body": {"width": round(W, 1), "height": round(HA, 1)}},
           "glint": {"streak": {"width": 900, "height": 110}},
           "tipstar": {"sparkle": {"width": 700, "height": 700}},
           "eyestar": {"sparkle": {"width": 380, "height": 380}}}
    for i in range(N_MOTES):
        att[f"mote{i}"] = {"sparkle": {"width": 260, "height": 260}}
    for i in range(N_PETALS):
        att[f"petal{i}"] = {"sparkle": {"width": 240, "height": 240}}

    out_t0 = 1.62
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)
    FLAP_HZ = 0.9

    def band_m(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.3, ease_in)
        return {"scale": (1.0, sy)}

    def body_m(t):
        k = seg(t, 0.05, 0.6, 0, 1)
        kk = back_out(k, 1.03) if 0 < k < 1 else k
        x = lerp(-500, 0, kk) + seg(t, 0.6, DUR, 0, 40)                  # glides in from the left, then drifts
        y = lerp(-250, 0, kk) + 20 * math.sin(t * 2.5)
        s = lerp(1.05, 1.0, kk) * seg(t, 0.6, DUR, 1.0, 1.02)
        return {"translate": (x, y), "scale": (s, s)}

    def flap(side):
        sgn = 1 if side == "r" else -1

        def f(t):
            ph = 2 * math.pi * FLAP_HZ * t
            open_ = seg(t, 0.05, 0.5, 0.3, 1.0, ease_out)
            return {"rotate": sgn * (30 + 12 * math.sin(ph)) * open_,          # swept up from the hips
                    "scale": ((0.9 + 0.1 * math.cos(ph)) * open_, 1.0)}
        return f

    def drift(p, dy, life):
        def f(t):
            u = (t - p["t0"]) / life
            s = 0 if u <= 0 or u >= 1 else math.sin(math.pi * u) * p["size"]
            return {"translate": (0, dy * max(0, min(1, u))), "scale": (max(s, 0.001),) * 2}
        return f

    G0, G1 = CLIMAX, CLIMAX + 0.4                # the light runs up the blade, then the tip shines
    motions = {"band": band_m, "body": body_m, "wing_l": flap("l"), "wing_r": flap("r"),
               "glint": lambda t: {"translate": (seg(t, G0, G1, 0.1, 0.95, kit.ease_inout) * blade_len, 0)},
               "tip": lambda t: {"scale": (seg(t, G1 - 0.05, G1 + 0.15, 0.2, 1.0, ease_out),) * 2, "rotate": seg(t, G1, DUR, 0, 60)},
               "eye": lambda t: {"scale": (seg(t, 0.5, 0.65, 0.2, 1.0, ease_out),) * 2}}
    alpha = {"band": lambda t: seg(t, 0, 0.18, 0, 0.92) * fade_out(t),
             "wing_l": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "wing_r": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "rim": lambda t: (seg(t, 0.05, 0.3, 0, 0.7) if t < 0.3 else seg(t, 0.3, 0.5, 0.7, 0.35)) * fade_out(t),
             "body": lambda t: seg(t, 0.05, 0.22, 0, 1) * fade_out(t),
             "glint": lambda t: seg(t, G0, G0 + 0.06, 0, 0.85) * seg(t, G1 - 0.1, G1, 1, 0),
             "tipstar": lambda t: seg(t, G1 - 0.05, G1 + 0.05, 0, 1) * seg(t, G1 + 0.1, G1 + 0.6, 1, 0),
             "eyestar": lambda t: seg(t, 0.5, 0.56, 0, 0.9) * seg(t, 0.6, 0.85, 1, 0)}
    for i, p in enumerate(motes):
        motions[f"mote{i}"] = drift(p, p["rise"], 0.9)
        alpha[f"mote{i}"] = (lambda p: lambda t: (0.9 if p["t0"] < t < p["t0"] + 0.9 else 0.0) * fade_out(t))(p)
    for i, p in enumerate(petals):
        motions[f"petal{i}"] = drift(p, -p["fall"], 0.9)
        alpha[f"petal{i}"] = (lambda p: lambda t: (0.8 if p["t0"] < t < p["t0"] + 0.9 else 0.0) * fade_out(t))(p)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
