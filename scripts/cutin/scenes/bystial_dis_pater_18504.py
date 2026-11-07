"""Bystial Dis Pater (18504) - the card's current in-game art (no Floowan over-frame), 512x512.

Art: a pale skeletal abyss dragon with many long clawed arms, pale wings and purple tentacles, wrapped in
purple lightning, in a crimson/pink sky with dark diamond-shaped rifts.
"""
import math
import os
import random

CARD_ID = 18504
NAME = "Bystial Dis Pater"
TEMPLATE = 19172
SCALE = 0.9

PROMPTS = {
    # the first wording made Qwen drop the purple tentacles; this one keeps them (seed 11)
    "beast": "Cut out the whole creature exactly as it is, including ALL of its dark purple tentacles with red eyes that curl around it (they are "
             "part of the creature and must stay), its pale skeletal dragon body, long neck and dragon head, its many long clawed pale arms and "
             "its pale wings. Same pose, position, size, colors and details. Remove only the background: the thin glowing purple lightning lines, "
             "the red and pink sky and the dark diamond shaped portals. Replace the background with a flat solid pure green color (#00FF00). No shadow.",
    "sky": "Remove the pale skeletal dragon creature completely, including its arms, wings and purple tentacles. Keep only the red and pink sky "
           "with the dark diamond shaped portals and a few thin purple lightning bolts, filling the whole image naturally, same painting style.",
}


def comfy(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    kit.upscale(os.path.join(work, "art.png"), L("up.png"))
    kit.qwen_edit(L("up.png"), PROMPTS["beast"], L("beast.png"), seed=11)
    kit.qwen_edit(L("up.png"), PROMPTS["sky"], L("sky.png"), seed=7)
    kit.upscale(L("beast.png"), L("beastup.png"))
    kit.upscale(L("sky.png"), L("skyup.png"))


HEAD = (0.43, 0.20)       # dragon head, fraction of the uncropped beast image
CORE = (0.50, 0.50)


def make_layers(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    beast, meta = kit.key_flat_background(L("beastup.png"), 2048, t0=0.12, t1=0.32, despill="green")
    meta.pop("_full")
    meta["pad"] = PAD = 120
    beast = kit.pad(beast, PAD)
    rng = random.Random(CARD_ID)
    meta["rng"] = rng
    sky = kit.wide_band(L("skyup.png"), stars=0, rng=rng)
    bolts = {f"bolt{i}": kit.fx_lightning(seed=CARD_ID + i) for i in range(3)}
    return {"sky": sky, "beast": beast, "aura": kit.silhouette_glow(beast, radius=26, gain=1.6),
            "glow": kit.fx_radial(), "ring": kit.fx_magic_circle(), "sparkle": kit.fx_sparkle(), **bolts}, meta


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.back_out
    rng, DUR = meta["rng"], 2.0
    beast = images["beast"]
    x0, y0, x1, y1 = meta["crop"]
    PAD = meta["pad"]
    H = 4300.0
    K = H / (beast.height - 2 * PAD)
    W, HA = beast.width * K, beast.height * K
    BX, BY = 0.0, 80.0
    ROAR = 0.75

    def anchor(f):
        px, py = f[0] * meta["full_w"], f[1] * meta["full_h"]
        return BX + (px - (x0 + x1) / 2) * K, BY - (py - (y0 + y1) / 2) * K
    head, core = anchor(HEAD), anchor(CORE)

    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "shake", "parent": "root"},                       # screen shake on the roar
             {"name": "band", "parent": "shake", "y": 100},
             {"name": "beast", "parent": "shake", "x": BX, "y": BY},
             {"name": "head", "parent": "beast", "x": round(head[0] - BX, 1), "y": round(head[1] - BY, 1)},
             {"name": "roarring", "parent": "beast", "x": round(head[0] - BX, 1), "y": round(head[1] - BY, 1)}]
    # lightning strikes across the band; each has its own flicker times
    strikes = [(-2900, [0.10, 0.13, ROAR, ROAR + 0.05, 1.30]), (-1300, [0.36, ROAR + 0.02, ROAR + 0.08]),
               (1500, [0.22, ROAR + 0.01, ROAR + 0.06, 1.45]), (3000, [0.48, ROAR + 0.03, 1.12, 1.16])]
    for i, (x, _) in enumerate(strikes):
        bones.append({"name": f"bolt{i}", "parent": "shake", "x": x, "y": 200, "rotation": rng.uniform(-8, 8)})
    embers = []
    for i in range(16):                                                # purple embers rising from the abyss
        embers.append({"t0": 0.3 + i * 0.08 + rng.uniform(0, 0.05), "rise": rng.uniform(500, 1000), "size": rng.uniform(0.5, 1.1)})
        bones.append({"name": f"ember{i}", "parent": "shake", "x": round(rng.uniform(-3300, 3300), 1), "y": round(rng.uniform(-1500, 300), 1)})

    slots = [{"name": "sky", "bone": "band", "attachment": "sky", "color": "8a6a7a00"}]
    slots += [{"name": f"bolt{i}", "bone": f"bolt{i}", "attachment": f"bolt{i % 3}", "color": "e0c4ff00", "blend": "additive"} for i in range(len(strikes))]
    slots += [{"name": "aura", "bone": "beast", "attachment": "aura", "color": "5a24b400", "blend": "additive"},
              {"name": "beast", "bone": "beast", "attachment": "beast", "color": "ffffff00"},
              {"name": "roarring", "bone": "roarring", "attachment": "ring", "color": "d070ff00", "blend": "additive"},
              {"name": "headglow", "bone": "head", "attachment": "glow", "color": "f0a8ff00", "blend": "additive"}]
    slots += [{"name": f"ember{i}", "bone": f"ember{i}", "attachment": "sparkle", "color": "c890ff00", "blend": "additive"} for i in range(16)]

    att = {"sky": {"sky": {"width": 9400, "height": 2700}},
           "aura": {"aura": {"width": round(W * 1.04, 1), "height": round(HA * 1.04, 1)}},
           "beast": {"beast": {"width": round(W, 1), "height": round(HA, 1)}},
           "roarring": {"ring": {"width": 1600, "height": 1600}},
           "headglow": {"glow": {"width": 2400, "height": 2400}}}
    for i in range(len(strikes)):
        att[f"bolt{i}"] = {f"bolt{i % 3}": {"width": 760, "height": 3000}}
    for i in range(16):
        att[f"ember{i}"] = {"sparkle": {"width": 300, "height": 300}}

    out_t0 = 1.62
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)

    def shake_m(t):
        if not (ROAR <= t <= ROAR + 0.32):
            return {"translate": (0.0, 0.0)}
        a = 70 * (1 - (t - ROAR) / 0.32)
        return {"translate": (a * math.sin(t * 157), a * math.sin(t * 211 + 1.3))}

    def band_m(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.3, ease_in)
        return {"scale": (1.0, sy)}

    def beast_m(t):
        k = seg(t, 0.05, 0.6, 0, 1)
        kk = back_out(k, 1.05) if 0 < k < 1 else k
        y = lerp(-1100, 0, kk) + seg(t, 0.6, DUR, 0, 70)
        s = lerp(1.15, 1.0, kk) * seg(t, 0.6, DUR, 1.0, 1.035)
        roar = 0.03 * math.exp(-((t - ROAR - 0.06) / 0.09) ** 2)          # rears up on the roar
        breathe = 0.012 * math.sin(t * 5)
        return {"translate": (0, y), "scale": (s + roar, s + roar + breathe)}

    def flicker(times):
        def f(t):
            v = 0.0
            for t0 in times:
                if t0 <= t < t0 + 0.09:
                    v = max(v, 1.0 - (t - t0) / 0.09 * 0.6)
            return v
        return f

    motions = {"shake": shake_m, "band": band_m, "beast": beast_m,
               "roarring": lambda t: {"scale": (seg(t, ROAR, ROAR + 0.4, 0.2, 3.4, ease_out),) * 2},
               "head": lambda t: {"scale": (seg(t, ROAR, ROAR + 0.2, 0.3, 1.0, ease_out),) * 2}}
    alpha = {"sky": lambda t: seg(t, 0, 0.18, 0, 0.95) * fade_out(t),
             "aura": lambda t: (seg(t, 0.05, 0.4, 0, 0.7) if t < 0.4 else 0.32 + 0.08 * math.sin(t * 9)
                                + 0.35 * math.exp(-((t - ROAR) / 0.12) ** 2)) * fade_out(t),
             "beast": lambda t: seg(t, 0.05, 0.25, 0, 1) * fade_out(t),
             "roarring": lambda t: seg(t, ROAR, ROAR + 0.04, 0, 0.9) * seg(t, ROAR + 0.04, ROAR + 0.42, 1, 0),
             "headglow": lambda t: seg(t, ROAR, ROAR + 0.05, 0, 0.8) * seg(t, ROAR + 0.05, ROAR + 0.45, 1, 0.15) * fade_out(t)}
    for i, (_, times) in enumerate(strikes):
        alpha[f"bolt{i}"] = (lambda f: lambda t: f(t) * fade_out(t))(flicker(times))
        motions[f"bolt{i}"] = (lambda tt: lambda t: {"scale": (1.0, 1.0 + 0.1 * (1 if any(a <= t < a + 0.09 for a in tt) else 0))})(times)
    for i, p in enumerate(embers):
        def em(p):
            def f(t):
                u = (t - p["t0"]) / 0.7
                s = 0 if u <= 0 or u >= 1 else math.sin(math.pi * u) * p["size"]
                return {"translate": (0, p["rise"] * max(0, min(1, u))), "scale": (max(s, 0.001),) * 2}
            return f
        motions[f"ember{i}"] = em(p)
        alpha[f"ember{i}"] = (lambda p: lambda t: (1.0 if p["t0"] < t < p["t0"] + 0.7 else 0.0) * fade_out(t))(p)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
