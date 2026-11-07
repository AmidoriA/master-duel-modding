"""Dark Magician the Magician of Black Magic (20185) - the user's custom over-frame art (gate 20185 -> 20185).

Art: Dark Magician in black-and-gold armour, tall pointed hat, blue skin, long blond hair, swinging a black
staff with a cyan orb, bursting out of the card frame over a radiant green/gold rune circle.
"""
import math
import os
import random

CARD_ID = 20185
NAME = "Dark Magician the Magician of Black Magic"
TEMPLATE = 19172
SCALE = 0.9               # the user prefers heroes a little under full screen height

ART_CROP = (0, 0, 704, 760)

PROMPTS = {
    "hero": "Keep only the dark magician exactly as he is: black and gold armor, tall black and gold pointed hat, blue skin, long blond hair, "
            "and the black staff with the glowing cyan orb that he holds. Same pose, position, size, colors and details. Remove everything else: "
            "remove the orange card frame, the border, the title bar, the text box and the glowing green and yellow magic background. "
            "Replace the entire background with a flat solid pure green color (#00FF00). No shadow.",
    "bg": "Remove the dark magician and his staff completely, and remove the orange card frame, the border, the title bar and the text box. "
          "Keep only the radiant green and golden magic background with the glowing rune circle, filling the whole image naturally, "
          "in the same painting style.",
}


def comfy(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    Image.open(os.path.join(work, "art.png")).convert("RGB").crop(ART_CROP).save(L("crop.png"))
    kit.upscale(L("crop.png"), L("up.png"))
    kit.qwen_edit(L("up.png"), PROMPTS["hero"], L("hero.png"), seed=7)
    kit.qwen_edit(L("up.png"), PROMPTS["bg"], L("bg.png"), seed=7)
    kit.upscale(L("hero.png"), L("heroup.png"))
    kit.upscale(L("bg.png"), L("bgup.png"))


# anchors as fractions of the uncropped hero image (grid overlay)
ORB = (0.035, 0.60)       # cyan orb at the end of the staff
FACE = (0.60, 0.35)
CHEST = (0.55, 0.50)


def make_layers(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    # fade the bottom: legs go under the name bar and Qwen left a floor shadow there
    hero, meta = kit.key_flat_background(L("heroup.png"), 2048, t0=0.12, t1=0.32, fade_bottom=(0.80, 0.95), despill="green")
    meta.pop("_full")
    meta["pad"] = PAD = 120                       # room for the rim glow where the staff touches the image edge
    hero = kit.pad(hero, PAD)
    circle = kit.circle_mask(Image.open(L("bgup.png")).convert("RGBA").resize((1536, 1536), Image.LANCZOS), 0.80, 0.99)
    meta["rng"] = random.Random(CARD_ID)
    return {"hero": hero, "rim": kit.silhouette_glow(hero), "circle": circle, "band": kit.fx_band(),
            "glow": kit.fx_radial(), "ring": kit.fx_magic_circle(), "streak": kit.fx_streak(), "sparkle": kit.fx_sparkle()}, meta


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, ease_inout, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.ease_inout, kit.back_out
    rng, DUR = meta["rng"], 2.0
    hero = images["hero"]
    x0, y0, x1, y1 = meta["crop"]
    PAD = meta["pad"]
    H = 4300.0                                  # figure height (without the padding)
    K = H / (hero.height - 2 * PAD)
    W = hero.width * K
    HA = hero.height * K                        # attachment height including the padding
    HX, HY = 250.0, 120.0      # hero sits right of centre so the staff and its burst have room on the left

    def anchor(f):
        px, py = f[0] * meta["full_w"], f[1] * meta["full_h"]
        return HX + (px - (x0 + x1) / 2) * K, HY - (py - (y0 + y1) / 2) * K
    orb, chest = anchor(ORB), anchor(CHEST)
    BURST = 1.0

    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "band", "parent": "root", "y": 120},
             {"name": "circle", "parent": "root", "x": round(chest[0], 1), "y": round(chest[1], 1)},
             {"name": "hero", "parent": "root", "x": HX, "y": HY},
             {"name": "orb", "parent": "hero", "x": round(orb[0] - HX, 1), "y": round(orb[1] - HY, 1)},
             {"name": "shock", "parent": "hero", "x": round(orb[0] - HX, 1), "y": round(orb[1] - HY, 1)},
             {"name": "flash", "parent": "hero", "x": round(orb[0] - HX, 1), "y": round(orb[1] - HY, 1)}]
    rays = []
    for i in range(7):
        ang = 180 + (i - 3) * 24 + rng.uniform(-6, 6)                 # fan out to the left from the orb
        rays.append({"ang": ang, "len": rng.uniform(0.8, 1.2)})
        bones.append({"name": f"ray{i}", "parent": "orb", "rotation": round(ang, 1)})
    sparks = []
    for i in range(14):                                                 # charge: energy drawn into the orb
        ang, r = rng.uniform(0, 2 * math.pi), rng.uniform(1100, 2000)
        sparks.append({"t0": 0.5 + i * 0.03 + rng.uniform(0, 0.05), "dx": r * math.cos(ang), "dy": r * math.sin(ang), "size": rng.uniform(0.6, 1.2)})
        bones.append({"name": f"spark{i}", "parent": "orb"})
    golds = []
    for i in range(10):                                                 # gold motes around him after the burst
        ang, r = rng.uniform(0, 2 * math.pi), rng.uniform(1300, 2300)
        golds.append({"t0": BURST + 0.05 + i * 0.06, "size": rng.uniform(0.5, 1.0), "spin": rng.choice([-1, 1]) * 90})
        bones.append({"name": f"gold{i}", "parent": "root", "x": round(chest[0] + r * math.cos(ang), 1), "y": round(chest[1] + 0.7 * r * math.sin(ang), 1)})

    slots = [{"name": "band", "bone": "band", "attachment": "band", "color": "1c0c2c00"},
             {"name": "circle", "bone": "circle", "attachment": "circle", "color": "b89cff00", "blend": "additive"},
             {"name": "rim", "bone": "hero", "attachment": "rim", "color": "9b5cff00", "blend": "additive"},
             {"name": "hero", "bone": "hero", "attachment": "hero", "color": "ffffff00"}]
    slots += [{"name": f"ray{i}", "bone": f"ray{i}", "attachment": "streak", "color": "b07cff00", "blend": "additive"} for i in range(7)]
    slots += [{"name": "orbglow", "bone": "orb", "attachment": "glow", "color": "8a40ff00", "blend": "additive"},
              {"name": "orbcore", "bone": "orb", "attachment": "glow", "color": "12001e00"},
              {"name": "shock", "bone": "shock", "attachment": "ring", "color": "c08cff00", "blend": "additive"}]
    slots += [{"name": f"spark{i}", "bone": f"spark{i}", "attachment": "sparkle", "color": "d6b8ff00", "blend": "additive"} for i in range(14)]
    slots += [{"name": f"gold{i}", "bone": f"gold{i}", "attachment": "sparkle", "color": "ffd88000", "blend": "additive"} for i in range(10)]
    slots.append({"name": "flash", "bone": "flash", "attachment": "glow", "color": "e8d0ff00", "blend": "additive"})

    att = {"band": {"band": {"width": 9400, "height": 2700}},
           "circle": {"circle": {"width": 4600, "height": 4600}},
           "rim": {"rim": {"width": round(W * 1.03, 1), "height": round(HA * 1.03, 1)}},
           "hero": {"hero": {"width": round(W, 1), "height": round(HA, 1)}},
           "orbglow": {"glow": {"width": 1500, "height": 1500}},
           "orbcore": {"glow": {"width": 520, "height": 520}},
           "shock": {"ring": {"width": 1800, "height": 1800}},
           "flash": {"glow": {"width": 7000, "height": 7000}}}
    for i in range(7):
        att[f"ray{i}"] = {f"streak": {"x": 1600, "width": 3200, "height": 260}}
    for i in range(14):
        att[f"spark{i}"] = {"sparkle": {"width": 300, "height": 300}}
    for i in range(10):
        att[f"gold{i}"] = {"sparkle": {"width": 340, "height": 340}}

    out_t0 = 1.62
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)

    def band_m(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.3, ease_in)
        return {"scale": (1.0, sy)}

    def circle_m(t):
        s = seg(t, 0.05, 0.5, 0.35, 1.0, ease_out) + (0.06 * math.exp(-((t - BURST) / 0.1) ** 2))
        return {"rotate": -38 * t, "scale": (s, s)}

    def hero_m(t):
        k = seg(t, 0.05, 0.45, 0, 1)
        kk = back_out(k, 1.05) if 0 < k < 1 else k
        x = lerp(-800, 0, kk) + seg(t, 0.45, DUR, 0, -60)
        y = lerp(-220, 0, kk)
        recoil = 70 * math.exp(-max(0.0, t - BURST) * 9) if t >= BURST else 0.0
        s = lerp(1.12, 1.0, kk) * seg(t, 0.45, DUR, 1.0, 1.035)
        return {"translate": (x + recoil, y), "rotate": lerp(-4, 0, kk), "scale": (s, s)}

    def orb_m(t):
        s = seg(t, 0.5, BURST, 0.2, 1.25, ease_in) if t < BURST else 1.0 + 0.12 * math.sin((t - BURST) * 10)
        return {"scale": (s, s)}

    def ray_m(p):
        def f(t):
            u = seg(t, BURST, BURST + 0.18, 0.05, p["len"], ease_out)
            return {"scale": (u, seg(t, BURST, BURST + 0.5, 1.0, 0.4))}
        return f

    def spark_in(p):
        def f(t):
            u = seg(t, p["t0"], p["t0"] + 0.35, 0, 1, ease_in)
            return {"translate": (p["dx"] * (1 - u), p["dy"] * (1 - u)), "scale": ((1 - u) * p["size"] + 0.001,) * 2}
        return f

    def pop(p, life=0.45):
        def f(t):
            u = (t - p["t0"]) / life
            s = 0 if u <= 0 or u >= 1 else math.sin(math.pi * u) * p["size"]
            return {"scale": (max(s, 0.001),) * 2, "rotate": p.get("spin", 0) * max(0, min(1, u))}
        return f

    motions = {"band": band_m, "circle": circle_m, "hero": hero_m, "orb": orb_m,
               "shock": lambda t: {"scale": (seg(t, BURST, BURST + 0.35, 0.2, 3.2, ease_out),) * 2},
               "flash": lambda t: {"scale": (seg(t, BURST, BURST + 0.3, 0.2, 1.0, ease_out),) * 2}}
    alpha = {"band": lambda t: seg(t, 0, 0.18, 0, 0.82) * fade_out(t),
             "circle": lambda t: seg(t, 0.05, 0.4, 0, 0.5) * (0.9 + 0.1 * math.sin(t * 6)) * fade_out(t),
             "rim": lambda t: (seg(t, 0.05, 0.3, 0, 0.85) if t < 0.3 else seg(t, 0.3, 0.8, 0.85, 0.4)) * fade_out(t),
             "hero": lambda t: seg(t, 0.05, 0.2, 0, 1) * fade_out(t),
             "orbglow": lambda t: seg(t, 0.5, BURST, 0, 1) * fade_out(t),
             "orbcore": lambda t: seg(t, 0.6, BURST, 0, 0.85) * seg(t, BURST, BURST + 0.15, 1, 0.5) * fade_out(t),
             "shock": lambda t: seg(t, BURST, BURST + 0.04, 0, 1) * seg(t, BURST + 0.04, BURST + 0.38, 1, 0),
             "flash": lambda t: seg(t, BURST, BURST + 0.04, 0, 0.55) * seg(t, BURST + 0.04, BURST + 0.4, 1, 0, ease_out)}
    for i, p in enumerate(rays):
        motions[f"ray{i}"] = ray_m(p)
        alpha[f"ray{i}"] = lambda t: seg(t, BURST, BURST + 0.05, 0, 0.9) * seg(t, BURST + 0.2, BURST + 0.5, 1, 0)
    for i, p in enumerate(sparks):
        motions[f"spark{i}"] = spark_in(p)
        alpha[f"spark{i}"] = (lambda p: lambda t: 1.0 if p["t0"] < t < p["t0"] + 0.35 else 0.0)(p)
    for i, p in enumerate(golds):
        motions[f"gold{i}"] = pop(p)
        alpha[f"gold{i}"] = (lambda p: lambda t: (1.0 if p["t0"] < t < p["t0"] + 0.45 else 0.0) * fade_out(t))(p)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
