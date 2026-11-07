"""Magician's Rod (12418): ghostly Dark Magician spirit (hero) + the rod as a late accent.

Approved in game on 2026-11-08 (spirit-focused version). Copy this file as the starting point for a new card.
Art: blue ghost magician + foreground rod with green orb + starry teal sky.
"""
import math
import os
import random

CARD_ID = 12418
NAME = "Magician's Rod"
TEMPLATE = 19172          # Odd-Eyes Arcray Dragon: one atlas page per quality tier
SCALE = 0.9               # whole-scene size in game (user: "slightly smaller", 2026-11-08)

# ComfyUI edit prompts (Qwen-Image-Edit 2509). Describe the card's actual subjects.
PROMPTS = {
    "rodonly": "Keep only the magic staff (the long blue rod with red rings and the green orb) exactly as it is, same position, size, angle, colors and details. Remove everything else: remove the ghostly blue magician, the stars and the sky. Replace the entire background with a flat solid pure magenta color (#FF00FF). No glow, no shadow on the background.",
    "norod": "Remove the magic staff (the long blue rod with red rings and the green orb) completely. Fill in the area where the staff was with the glowing blue ghostly magician spirit and the starry night sky so it looks natural. Keep the magician spirit, its glow, colors and the stars exactly the same everywhere else.",
    "skyonly": "Remove the magic staff and remove the ghostly blue magician spirit completely. Keep only the dark teal starry night sky with sparkling stars and soft nebula glow, filling the whole image naturally. Keep the same colors and painting style.",
    "spirit": "Remove all the stars, sparkles and the teal sky. Keep only the glowing blue ghostly magician spirit exactly as it is, same pose, position, size, colors and glow. Make the entire background pure black (#000000).",
}


def comfy(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    kit.upscale(os.path.join(work, "art.png"), L("up.png"))
    for tag in ("rodonly", "norod", "skyonly"):
        kit.qwen_edit(L("up.png"), PROMPTS[tag], L(f"{tag}.png"), seed=7)
    kit.qwen_edit(L("norod.png"), PROMPTS["spirit"], L("spirit.png"), seed=11)
    for src, dst in (("rodonly", "rodup"), ("spirit", "spiritup"), ("skyonly", "skyup")):
        kit.upscale(L(f"{src}.png"), L(f"{dst}.png"))


def make_layers(work, kit):
    import numpy as np
    L = lambda n: os.path.join(work, "layers_src", n)
    rng = random.Random(CARD_ID)   # shared: sky stars first, then sparkles (keeps the approved layout)
    rod, meta = kit.key_flat_background(L("rodup.png"), 2048, fade_right=(0.875, 0.985))
    f = np.asarray(meta.pop("_full")).astype(np.int32)
    m = (f[..., 1] > f[..., 0] + 40) & (f[..., 1] > 90) & (f[..., 3] > 200) & (f[..., 2] < f[..., 1] + 30)
    oy, ox = np.nonzero(m)                                   # the green orb
    meta["orb"] = (float(ox.mean()), float(oy.mean()))
    A = np.asarray(rod)[..., 3]
    ys, xs = np.nonzero(A > 6)
    meta["tip"] = (float(meta["crop"][0] + xs.min()), float(meta["crop"][1] + ys[xs == xs.min()].mean()))
    spirit = kit.additive_layer(L("spiritup.png"), 1536)
    sky = kit.wide_band(L("skyup.png"), rng=rng)
    meta["rng"] = rng
    images = {"sky": sky, "rod": rod, "spirit": spirit, "aura": kit.blur_aura(spirit),
              "ring": kit.fx_magic_circle(), "glow": kit.fx_radial(), "sparkle": kit.fx_sparkle(), "streak": kit.fx_streak()}
    return images, meta


def rig(images, meta, kit):
    seg, lerp, ease_out, ease_in, ease_inout, back_out = kit.seg, kit.lerp, kit.ease_out, kit.ease_in, kit.ease_inout, kit.back_out
    rng, DUR = meta["rng"], 2.0
    rod = images["rod"]
    RODK = 5000 / meta["full"]                     # the full 2048 px rod square spans 5000 units
    x0, y0, x1, y1 = meta["crop"]
    full = meta["full"]
    P = lambda p: ((p[0] - full / 2) * RODK, -(p[1] - full / 2) * RODK)
    rcx, rcy = ((x0 + x1) / 2 - full / 2) * RODK, -((y0 + y1) / 2 - full / 2) * RODK
    orb, tip = P(meta["orb"]), P(meta["tip"])
    rod_ang = math.degrees(math.atan2(orb[1] - tip[1], orb[0] - tip[0]))

    # Composition: spirit is the hero (big, near centre, first); rod is a smaller late accent.
    SPX, SPY = -400, 120
    ROD_X, ROD_Y, ROD_S = 1350, 120, 0.66      # orb clears the in-game card name bar (bottom ~23%)
    bones = [{"name": "root", "scaleX": SCALE, "scaleY": SCALE},
             {"name": "band", "parent": "root"},
             {"name": "aura", "parent": "root", "x": SPX, "y": SPY},
             {"name": "spirit", "parent": "root", "x": SPX, "y": SPY},
             {"name": "ring", "parent": "root", "x": SPX, "y": SPY - 150},
             {"name": "sweep", "parent": "root", "x": SPX, "y": SPY, "rotation": 62},
             {"name": "rod", "parent": "root", "x": ROD_X, "y": ROD_Y, "scaleX": ROD_S, "scaleY": ROD_S},
             {"name": "orb", "parent": "rod", "x": round(orb[0], 1), "y": round(orb[1], 1)},
             {"name": "streak", "parent": "rod", "x": round(tip[0], 1), "y": round(tip[1], 1), "rotation": round(rod_ang, 2)},
             {"name": "flash", "parent": "rod", "x": round(orb[0], 1), "y": round(orb[1], 1)}]
    sparks = []
    for i in range(14):
        t0 = 0.4 + i * 0.08 + rng.uniform(-0.03, 0.03)
        if i % 5 == 4:   # a few along the rod once it has arrived
            u = rng.uniform(0.2, 1.0)
            x, y = ROD_X + ROD_S * lerp(tip[0], orb[0], u), ROD_Y + ROD_S * lerp(tip[1], orb[1], u)
            t0 = max(t0, 1.1)
        else:            # outlining the spirit
            ang, r = rng.uniform(0, 2 * math.pi), rng.uniform(900, 1900)
            x, y = SPX + r * math.cos(ang), SPY + 0.8 * r * math.sin(ang)
        sparks.append({"t0": t0, "size": rng.uniform(0.6, 1.4), "spin": rng.choice([-1, 1]) * rng.uniform(60, 150)})
        bones.append({"name": f"spark{i}", "parent": "root", "x": round(x, 1), "y": round(y, 1)})

    slots = [{"name": "sky", "bone": "band", "attachment": "sky", "color": "a9c9d600"},
             {"name": "ring", "bone": "ring", "attachment": "ring", "color": "7fe8ff00", "blend": "additive"},
             {"name": "aura", "bone": "aura", "attachment": "aura", "color": "8fe6ff00", "blend": "additive"},
             {"name": "spirit_body", "bone": "spirit", "attachment": "spirit", "color": "5fa6e000"},
             {"name": "spirit", "bone": "spirit", "attachment": "spirit", "color": "ffffff00", "blend": "additive"},
             {"name": "sweep", "bone": "sweep", "attachment": "streak", "color": "d8fbff00", "blend": "additive"},
             {"name": "rod", "bone": "rod", "attachment": "rod", "color": "ffffff00"},
             {"name": "streak", "bone": "streak", "attachment": "streak", "color": "c8f8ff00", "blend": "additive"},
             {"name": "orbglow", "bone": "orb", "attachment": "glow", "color": "7dffd000", "blend": "additive"}]
    slots += [{"name": f"spark{i}", "bone": f"spark{i}", "attachment": "sparkle", "color": "e0faff00", "blend": "additive"} for i in range(14)]
    slots.append({"name": "flash", "bone": "flash", "attachment": "glow", "color": "ffffff00", "blend": "additive"})

    att = {"sky": {"sky": {"width": 9200, "height": 2600}},
           "ring": {"ring": {"width": 3100, "height": 3100}},
           "aura": {"aura": {"width": 4600, "height": 4600}},
           "spirit_body": {"spirit": {"width": 3800, "height": 3800}},
           "spirit": {"spirit": {"width": 3800, "height": 3800}},
           "sweep": {"streak": {"width": 5200, "height": 420}},
           "rod": {"rod": {"x": round(rcx, 1), "y": round(rcy, 1), "width": round(rod.width * RODK, 1), "height": round(rod.height * RODK, 1)}},
           "streak": {"streak": {"x": 900, "width": 1800, "height": 220}},
           "orbglow": {"glow": {"width": 1300, "height": 1300}},
           "flash": {"glow": {"width": 7500, "height": 7500}}}
    for i in range(14):
        att[f"spark{i}"] = {"sparkle": {"width": 420, "height": 420}}

    out_t0 = 1.62
    fade_out = lambda t: seg(t, out_t0, DUR, 1, 0, ease_in)
    R0 = 0.75          # rod arrival
    F0 = R0 + 0.3      # orb flash

    def band(t):
        sy = seg(t, 0.0, 0.22, 0.02, 1.0, ease_out) if t < out_t0 else seg(t, out_t0, DUR, 1.0, 0.25, ease_in)
        return {"scale": (1.0, sy), "translate": (seg(t, 0, DUR, 150, -150), 0)}

    def ring_m(t):
        s = seg(t, 0.2, 0.7, 0.6, 1.0, ease_out) + 0.03 * math.sin(t * 6)
        return {"rotate": -50 * t, "scale": (s, s)}

    def aura_m(t):
        s = seg(t, 0.03, 0.6, 1.45, 1.02, ease_out) * (1 + 0.04 * math.sin(t * 8))
        return {"scale": (s, s), "translate": (0, seg(t, 0.03, 0.6, -350, 0, ease_out))}

    def spirit_m(t):
        s = seg(t, 0.05, 0.6, 1.18, 1.0, ease_out) * seg(t, 0.6, DUR, 1.0, 1.05)
        y = seg(t, 0.05, 0.6, -350, 0, ease_out) + seg(t, 0.6, DUR, 0, 90)
        return {"translate": (seg(t, 0.6, DUR, 0, -80), y), "scale": (s, s)}

    def sweep_m(t):
        u = seg(t, 0.62, 1.0, -1, 1, ease_inout)
        a = math.radians(62 - 90)
        return {"translate": (u * 2400 * math.cos(a), u * 2400 * math.sin(a))}

    def rod_m(t):
        u = seg(t, R0, R0 + 0.32, 0, 1)
        k = back_out(u, 1.1) if 0 < u < 1 else u
        x = lerp(2600, 0, k) + seg(t, R0 + 0.32, DUR, 0, 70)
        y = lerp(-1900, 0, k) + seg(t, R0 + 0.32, DUR, 0, 30)
        r = lerp(-22, 0, k) + seg(t, R0 + 0.32, DUR, 0, 1.5)
        if t > out_t0:
            x += seg(t, out_t0, DUR, 0, 700, ease_in)
            y += seg(t, out_t0, DUR, 0, 400, ease_in)
        return {"translate": (x, y), "rotate": r}

    def streak_m(t):
        Ln = math.hypot(orb[0] - tip[0], orb[1] - tip[1])
        u = seg(t, F0 + 0.01, F0 + 0.32, -0.25, 1.0, ease_inout)
        return {"translate": (u * Ln * math.cos(math.radians(rod_ang)), u * Ln * math.sin(math.radians(rod_ang)))}

    def orb_m(t):
        s = seg(t, F0, F0 + 0.15, 0.3, 1.3, ease_out) if t < F0 + 0.15 else 1.0 + 0.18 * math.sin((t - F0 - 0.15) * 9)
        return {"scale": (s, s)}

    def flash_m(t):
        s = seg(t, F0, F0 + 0.3, 0.15, 1.0, ease_out)
        return {"scale": (s, s)}

    def spark_m(p):
        def f(t):
            u = (t - p["t0"]) / 0.4
            s = 0 if u <= 0 or u >= 1 else math.sin(math.pi * u) * p["size"]
            return {"scale": (max(s, 0.001), max(s, 0.001)), "rotate": p["spin"] * max(0, min(1, u))}
        return f

    alpha = {"sky": lambda t: seg(t, 0, 0.18, 0, 0.92) * fade_out(t),
             "ring": lambda t: seg(t, 0.2, 0.6, 0, 0.32) * fade_out(t),
             "aura": lambda t: (seg(t, 0.03, 0.25, 0, 0.75) if t < 0.25 else seg(t, 0.25, 0.8, 0.75, 0.22) * (0.9 + 0.1 * math.sin(t * 8))) * fade_out(t),
             "spirit_body": lambda t: seg(t, 0.12, 0.55, 0, 0.7) * fade_out(t),
             "spirit": lambda t: seg(t, 0.15, 0.6, 0, 0.55) * (0.8 + 0.2 * math.sin(t * 7)) * fade_out(t),
             "sweep": lambda t: seg(t, 0.62, 0.72, 0, 0.7) * seg(t, 0.88, 1.0, 1, 0),
             "rod": lambda t: seg(t, R0, R0 + 0.08, 0, 1) * fade_out(t),
             "streak": lambda t: seg(t, F0, F0 + 0.06, 0, 1) * seg(t, F0 + 0.25, F0 + 0.38, 1, 0),
             "orbglow": lambda t: seg(t, F0, F0 + 0.1, 0, 1) * fade_out(t),
             "flash": lambda t: seg(t, F0, F0 + 0.05, 0, 0.6) * seg(t, F0 + 0.05, F0 + 0.4, 1, 0, ease_out)}
    motions = {"band": band, "ring": ring_m, "aura": aura_m, "spirit": spirit_m, "sweep": sweep_m, "rod": rod_m,
               "streak": streak_m, "orb": orb_m, "flash": flash_m}
    for i, p in enumerate(sparks):
        alpha[f"spark{i}"] = (lambda p: (lambda t: 1.0 if p["t0"] < t < p["t0"] + 0.4 else 0.0))(p)
        motions[f"spark{i}"] = spark_m(p)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=DUR)
