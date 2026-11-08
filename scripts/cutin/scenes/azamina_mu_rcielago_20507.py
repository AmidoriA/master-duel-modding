"""Azamina Mu Rcielago (20507) - the user's custom over-frame art (gate 20507 -> 20507).

Art: a blue-skinned horned demon woman with long swirling pink-red hair, huge dark-red bat wings, dark
armour, two dark spears, blue flames around her, crimson background. ("Murcielago" = bat.)
Separate layers: her (without wings and flames), the wing pair (flaps), the blue flames (additive).
"""
import math
import os
import random

CARD_ID = 20507
NAME = "Azamina Mu Rcielago"
TEMPLATE = 19172
SCALE = 0.9
DUR = 1.9                 # the game needs cut-ins strictly under 2 s: calm, slow motion instead of a longer clip

ART_CROP = (0, 0, 704, 760)

PROMPTS = {
    "hero": "Keep only the blue-skinned horned demon woman exactly as she is: her long swirling pink and red hair, her red eyes, her dark armor, "
            "and the two dark spears she holds. Same pose, position, size, colors and details. Remove her large dark red bat wings, remove all of "
            "the blue flames, and remove the card frame, the border, the title bar, the text box and the crimson background. Replace the entire "
            "background with a flat solid pure green color (#00FF00). No shadow.",
    "wings": "Keep only the pair of large dark red bat wings behind the demon woman. Show both bat wings complete and fully spread open to the left "
             "and to the right, symmetric, joined at the center, with nothing in front of them: remove the woman, her hair, her spears, the blue "
             "flames, the card frame and the background. Same anime painting style. Replace the entire background with a flat solid pure green "
             "color (#00FF00).",
    "flames": "Keep only the glowing blue and cyan flames exactly where they are. Remove the woman, her hair, wings and spears, the card frame and "
              "the crimson background. Make the entire background pure black (#000000).",
    "bg": "Remove the demon woman, her hair, her wings, her spears and the blue flames completely, and remove the card frame, the border, the "
          "title bar and the text box. Keep only the dark crimson and red background with soft swirling pink mist, filling the whole image "
          "naturally, same painting style.",
}


def comfy(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    Image.open(os.path.join(work, "art.png")).convert("RGB").crop(ART_CROP).save(L("crop.png"))
    kit.upscale(L("crop.png"), L("up.png"))
    for tag in ("hero", "wings", "flames", "bg"):
        kit.qwen_edit(L("up.png"), PROMPTS[tag], L(f"{tag}.png"), seed=7)
        kit.upscale(L(f"{tag}.png"), L(f"{tag}up.png"))


# anchors (fractions of the uncropped hero image, from a grid overlay)
GEM = (0.535, 0.36)         # red gem on her chest
WING_JOINT = (0.52, 0.40)   # between her shoulder blades
WING_SPAN = 6400.0          # design units, tip to tip


def make_layers(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    hero, meta = kit.key_flat_background(L("heroup.png"), 2048, t0=0.12, t1=0.32, despill="green")
    meta.pop("_full")
    meta["pad"] = PAD = 120
    hero = kit.pad(hero, PAD)
    wings = kit.split_wing_pair(L("wingsup.png"))
    meta["wings"] = {k: v for k, v in wings.items() if k not in ("left", "right")}
    from PIL import Image
    fl = Image.open(L("flamesup.png")).convert("RGB")
    fl = fl.crop((0, int(fl.height * 0.45), fl.width, fl.height))           # the flame row is in the lower half
    fl.save(L("flames_row.png"))
    flames = kit.additive_layer(L("flames_row.png"), 1600, vignette=False)
    rng = random.Random(CARD_ID)
    meta["rng"] = rng
    band = kit.wide_band(L("bgup.png"), stars=0, rng=rng)
    return {"band": band, "hero": hero, "rim": kit.silhouette_glow(hero, radius=24), "wing_l": wings["left"], "wing_r": wings["right"],
            "flames": flames, "bat": kit.fx_bat(), "glow": kit.fx_radial(), "sparkle": kit.fx_sparkle()}, meta


N_BATS, N_EMBERS = 10, 8


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.back_out
    rng = meta["rng"]
    hero = images["hero"]
    x0, y0, x1, y1 = meta["crop"]
    PAD = meta["pad"]
    H = 4300.0
    K = H / (hero.height - 2 * PAD)
    W, HA = hero.width * K, hero.height * K
    HX, HY = 0.0, 180.0
    CLIMAX = 0.85

    def anchor(f):
        px, py = f[0] * meta["full_w"], f[1] * meta["full_h"]
        return HX + (px - (x0 + x1) / 2) * K, HY - (py - (y0 + y1) / 2) * K
    gem, joint = anchor(GEM), anchor(WING_JOINT)
    wm = dict(meta["wings"])
    wm["left"], wm["right"] = images["wing_l"], images["wing_r"]
    att_l, att_r = kit.wing_attachments(wm, WING_SPAN)

    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "band", "parent": "root", "y": 120},
             {"name": "flamesA", "parent": "root", "x": -2200, "y": -1950},
             {"name": "flamesB", "parent": "root", "x": 2200, "y": -1950, "scaleX": -1},
             {"name": "hero", "parent": "root", "x": HX, "y": HY},
             {"name": "wing_l", "parent": "hero", "x": round(joint[0] - HX - 40, 1), "y": round(joint[1] - HY, 1)},
             {"name": "wing_r", "parent": "hero", "x": round(joint[0] - HX + 40, 1), "y": round(joint[1] - HY, 1)},
             {"name": "gem", "parent": "hero", "x": round(gem[0] - HX, 1), "y": round(gem[1] - HY, 1)}]
    bats = []
    for i in range(N_BATS):                               # the swarm bursts out of her at the climax
        ang = rng.uniform(0, 2 * math.pi)
        dist = rng.uniform(2600, 4400)
        bats.append({"t0": CLIMAX + rng.uniform(0, 0.15), "dx": dist * math.cos(ang), "dy": dist * 0.6 * math.sin(ang),
                     "size": rng.uniform(0.6, 1.2), "flap": rng.uniform(9, 12), "ph": rng.uniform(0, 6.28)})
        bones.append({"name": f"bat{i}", "parent": "root", "x": round(gem[0], 1), "y": round(gem[1], 1),
                      "rotation": round(math.degrees(math.atan2(math.sin(ang) * 0.6, math.cos(ang))) * 0.15, 1)})
    embers = []
    for i in range(N_EMBERS):
        embers.append({"t0": 0.3 + i * 0.13 + rng.uniform(0, 0.05), "rise": rng.uniform(400, 700), "size": rng.uniform(0.5, 1.0)})
        bones.append({"name": f"ember{i}", "parent": "root", "x": round(rng.uniform(-3300, 3300), 1), "y": round(rng.uniform(-1700, -600), 1)})

    slots = [{"name": "band", "bone": "band", "attachment": "band", "color": "8c4a5800"},
             {"name": "flamesA", "bone": "flamesA", "attachment": "flames", "color": "ffffff00", "blend": "additive"},
             {"name": "flamesB", "bone": "flamesB", "attachment": "flames", "color": "ffffff00", "blend": "additive"},
             {"name": "wing_l", "bone": "wing_l", "attachment": "wing_l", "color": "ffffff00"},
             {"name": "wing_r", "bone": "wing_r", "attachment": "wing_r", "color": "ffffff00"}]
    # embers behind her: additive cyan passing over her body read as a colour flicker
    slots += [{"name": f"ember{i}", "bone": f"ember{i}", "attachment": "sparkle", "color": "6cd0ff00", "blend": "additive"} for i in range(N_EMBERS)]
    slots += [{"name": "rim", "bone": "hero", "attachment": "rim", "color": "35a8ff00", "blend": "additive"},
             {"name": "hero", "bone": "hero", "attachment": "hero", "color": "ffffff00"},
             {"name": "gemglow", "bone": "gem", "attachment": "glow", "color": "ff3a4a00", "blend": "additive"}]
    slots += [{"name": f"bat{i}", "bone": f"bat{i}", "attachment": "bat", "color": "240a1400"} for i in range(N_BATS)]
    slots.append({"name": "flash", "bone": "gem", "attachment": "glow", "color": "ff8aa000", "blend": "additive"})

    fl = images["flames"]
    att = {"band": {"band": {"width": 9400, "height": 2800}},
           "wing_l": {"wing_l": att_l}, "wing_r": {"wing_r": att_r},
           "rim": {"rim": {"width": round(W * 1.03, 1), "height": round(HA * 1.03, 1)}},
           "hero": {"hero": {"width": round(W, 1), "height": round(HA, 1)}},
           "gemglow": {"glow": {"width": 900, "height": 900}},
           "flamesA": {"flames": {"y": 500, "width": 4600, "height": round(4600 * fl.height / fl.width * 0.7, 1)}},
           "flamesB": {"flames": {"y": 500, "width": 4600, "height": round(4600 * fl.height / fl.width * 0.7, 1)}},
           "flash": {"glow": {"width": 6500, "height": 6500}}}
    for i in range(N_BATS):
        att[f"bat{i}"] = {"bat": {"width": 780, "height": 780}}
    for i in range(N_EMBERS):
        att[f"ember{i}"] = {"sparkle": {"width": 280, "height": 280}}

    out_t0 = 1.55
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)
    FLAP_HZ = 0.9                       # slow beats (the user liked ~0.9 beats/s)

    def band_m(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.3, ease_in)
        return {"scale": (1.0, sy)}

    def hero_m(t):
        k = seg(t, 0.05, 0.6, 0, 1)
        kk = back_out(k, 1.03) if 0 < k < 1 else k
        y = lerp(700, 0, kk) + 25 * math.sin(t * 2.5)                    # glides down, then hovers slowly
        s = lerp(1.06, 1.0, kk) * seg(t, 0.6, DUR, 1.0, 1.02)
        return {"translate": (seg(t, 0.6, DUR, 0, -30), y), "scale": (s, s)}

    def flap(side):
        sgn = 1 if side == "r" else -1

        def f(t):
            ph = 2 * math.pi * FLAP_HZ * t
            amp = 14 + 8 * math.exp(-((t - CLIMAX) / 0.25) ** 2)          # big beat at the climax
            open_ = seg(t, 0.05, 0.45, 0.4, 1.0, ease_out)
            return {"rotate": sgn * (4 + amp * math.sin(ph)) * open_, "scale": ((0.88 + 0.12 * math.cos(ph)) * open_, 1.0)}
        return f

    def flames_m(phase):
        def f(t):
            up = seg(t, 0.0, 0.4, -400, 0, ease_out) + 120 * math.exp(-((t - CLIMAX) / 0.22) ** 2)
            return {"translate": (25 * math.sin(t * 3.5 + phase), up),
                    "scale": (1.0, 1.0 + 0.05 * math.sin(t * 6.5 + phase) + 0.15 * math.exp(-((t - CLIMAX) / 0.2) ** 2))}
        return f

    def bat_m(p):
        def f(t):
            u = (t - p["t0"]) / 0.65
            if u <= 0:
                return {"translate": (0, 0), "scale": (0.001, 0.001)}
            u = min(u, 1)
            e = ease_out(u)
            s = p["size"] * (0.4 + 0.8 * e)
            return {"translate": (p["dx"] * e, p["dy"] * e + 200 * math.sin(u * 3)),
                    "scale": (s, s * (0.55 + 0.45 * abs(math.sin(t * p["flap"] + p["ph"]))))}
        return f

    motions = {"band": band_m, "hero": hero_m, "wing_l": flap("l"), "wing_r": flap("r"),
               "flamesA": flames_m(0.0), "flamesB": flames_m(2.1),
               "gem": lambda t: {"scale": ((1.0 + 0.04 * math.sin(t * 4)) * (1 + 0.4 * math.exp(-((t - CLIMAX) / 0.12) ** 2)),) * 2}}
    alpha = {"band": lambda t: seg(t, 0, 0.18, 0, 0.95) * fade_out(t),
             "wing_l": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "wing_r": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "rim": lambda t: (seg(t, 0.05, 0.3, 0, 0.8) if t < 0.3 else seg(t, 0.3, 0.5, 0.8, 0.4)) * fade_out(t),   # steady: no pulse tinting her edges
             "hero": lambda t: seg(t, 0.05, 0.22, 0, 1) * fade_out(t),
             "gemglow": lambda t: seg(t, 0.4, 0.6, 0, 0.55) * fade_out(t),
             "flamesA": lambda t: seg(t, 0.0, 0.3, 0, 0.7) * (0.94 + 0.06 * math.sin(t * 9)) * fade_out(t),
             "flamesB": lambda t: seg(t, 0.0, 0.3, 0, 0.7) * (0.94 + 0.06 * math.sin(t * 8 + 1)) * fade_out(t),
             "flash": lambda t: seg(t, CLIMAX, CLIMAX + 0.04, 0, 0.5) * seg(t, CLIMAX + 0.04, CLIMAX + 0.4, 1, 0, ease_out)}
    for i, p in enumerate(bats):
        motions[f"bat{i}"] = bat_m(p)
        alpha[f"bat{i}"] = (lambda p: lambda t: seg(t, p["t0"], p["t0"] + 0.05, 0, 1) * seg(t, p["t0"] + 0.4, p["t0"] + 0.65, 1, 0) * fade_out(t))(p)
    for i, p in enumerate(embers):
        def em(p):
            def f(t):
                u = (t - p["t0"]) / 0.8
                s = 0 if u <= 0 or u >= 1 else math.sin(math.pi * u) * p["size"]
                return {"translate": (0, p["rise"] * max(0, min(1, u))), "scale": (max(s, 0.001),) * 2}
            return f
        motions[f"ember{i}"] = em(p)
        alpha[f"ember{i}"] = (lambda p: lambda t: (1.0 if p["t0"] < t < p["t0"] + 0.8 else 0.0) * fade_out(t))(p)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
