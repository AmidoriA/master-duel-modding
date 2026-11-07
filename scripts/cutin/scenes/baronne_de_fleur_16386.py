"""Baronne de Fleur (16386) - REPLACES the official cut-in (apply with --override-official).

Art: the user's custom over-frame (gate 16386 -> 16386): a white-haired, horned gothic girl riding
side-saddle on an armoured white unicorn (black helmet with a pink cross, gold armour), a big red
shield with a purple cross, a sword, black bat wings, blood moon.

Choreography follows the OFFICIAL cut-in, read from its skeleton (extract-cutin + spine_preview):
no background band; whole figure fades in over 0-0.17 s while scaling 0.7 -> 0.95 (by 0.367 s)
-> 1.1 (1.6 s); the horse drifts right-to-left and pitches 4.2 -> -0.7 deg; 13 petals spread outward
(group scale 0.7 -> 1.1), each spinning and flipping with the official keys; fade out 1.47 -> 1.6 s.
The official petal sprites are taken from the official atlas at build time and recoloured.
"""
import json
import math
import os
import random

CARD_ID = 16386
NAME = "Baronne de Fleur"
TEMPLATE = 19172

ART_CROP = (0, 0, 704, 760)    # card canvas down to the top of the text box

PROMPTS = {
    "rider": "Keep only the white-haired girl with black horns and her black bat wings, the white armored unicorn horse she rides with its black "
             "helmet, golden armor, white mane and horn, the large red shield with the purple cross, and the sword, exactly as they are: same pose, "
             "position, size, colors and details. Remove everything else: remove the card frame, the border, the title bar, the text box, the "
             "blood moon and the red sky background. Replace the entire background with a flat solid pure green color (#00FF00). No shadow.",
    "moon": "Keep only the large blood-red full moon that is behind the girl. Show the complete round moon with its soft red glow and its surface "
            "markings, with nothing in front of it: remove the girl, the unicorn, the shield, the sword, the wings, the card frame, the text box and "
            "the sky. Center the moon. Replace the entire background with a flat solid pure green color (#00FF00).",
}


def comfy(work, kit):
    from PIL import Image
    L = lambda n: os.path.join(work, "layers_src", n)
    Image.open(os.path.join(work, "art.png")).convert("RGB").crop(ART_CROP).save(L("crop.png"))
    kit.upscale(L("crop.png"), L("up.png"))
    kit.qwen_edit(L("up.png"), PROMPTS["rider"], L("rider.png"), seed=7)
    kit.upscale(L("rider.png"), L("riderup.png"))
    kit.qwen_edit(L("up.png"), PROMPTS["moon"], L("moon.png"), seed=21)
    kit.upscale(L("moon.png"), L("moonup.png"))


# The official cut-in is read from <work>/official (monster_cutin.py extract-cutin --card 16386 --out <work>/official).
OFFICIAL_PREFAB_SCALE = 0.17
OFFICIAL_FIGURE_H = 3540.0   # official skeleton bounds height (official units); our figure gets the same on-screen height


def _official(work):
    return json.load(open(os.path.join(work, "official", "P16386.json"), encoding="utf-8"))


def make_layers(work, kit):
    L = lambda n: os.path.join(work, "layers_src", n)
    hero, meta = kit.key_flat_background(L("riderup.png"), 2048, t0=0.12, t1=0.32, despill="green")
    meta.pop("_full")
    petals = kit.atlas_regions(os.path.join(work, "official", "P16386.atlas"), lambda n: n.startswith("Petal"))
    moon, _ = kit.key_flat_background(L("moonup.png"), 1400, t0=0.10, t1=0.34, despill="green")
    meta["official"] = _official(work)
    return {"hero": hero, "moon": moon, "moonhalo": kit.blur_aura(moon, 512, 40, 1.4), **petals}, meta


def rig(images, meta, kit):
    j = meta["official"]
    an = j["animations"]["animation"]
    # official units -> our design units: both screens are the same height in game
    # (our design screen VIEW_H_UNITS vs the official prefab's calibrated in-game view) ~1.22
    OFF_K = kit.VIEW_H_UNITS / (2 * kit.GAME_ORTHO_SIZE / (0.01 * OFFICIAL_PREFAB_SCALE))
    HERO_H = OFFICIAL_FIGURE_H * OFF_K
    ob = {b["name"]: b for b in j["bones"]}
    sk = j["skins"][0]["attachments"]
    hero = images["hero"]
    HW = hero.width * HERO_H / hero.height
    # official setup bounds centre (figure) -> where our figure sits
    cx = (j["skeleton"]["x"] + j["skeleton"]["width"] / 2) * OFF_K
    cy = (j["skeleton"]["y"] + j["skeleton"]["height"] / 2) * OFF_K

    bones = [{"name": "root"},
             {"name": "zoom", "parent": "root"},                       # official root: 0.7 -> 0.95 -> 1.1
             {"name": "hero", "parent": "zoom", "x": round(cx, 1), "y": round(cy, 1)},
             {"name": "petalscale", "parent": "zoom"}]                # official PetalScale: 0.7 -> 1.1
    motions = {"zoom": kit.spine_bone_motion(an["bones"]["root"]),
               "hero": kit.spine_bone_motion(an["bones"]["HorseLowerBody"], OFF_K),   # gallop drift + pitch
               "petalscale": kit.spine_bone_motion(an["bones"]["PetalScale"])}

    def fade(t):    # official slot colour: 0 -> 1 by 0.1667 s, 1 until 1.4667 s, -> 0 at 1.6 s
        return kit.seg(t, 0, 0.1667, 0, 1) * kit.seg(t, 1.4667, 1.6, 1, 0)

    # Blood moon (from the user's art, not in the official cut-in): behind the rider, upper right,
    # moving with slow parallax against the gallop (official drift is right -> left, the moon goes
    # left -> right), swelling a little less than the zoom, with a pulsing red halo.
    moon = images["moon"]
    MOON_D = 0.40 * HERO_H
    MX, MY = cx + 0.12 * HW, cy + 0.22 * HERO_H
    bones[1:1] = [{"name": "moon", "parent": "root", "x": round(MX, 1), "y": round(MY, 1)}]
    motions["moon"] = lambda t: {"translate": (kit.seg(t, 0, 1.6, -140, 140), kit.seg(t, 0, 1.6, -30, 30)),
                                 "scale": (kit.seg(t, 0, 1.6, 0.93, 1.03),) * 2}
    slots = [{"name": "moonhalo", "bone": "moon", "attachment": "moonhalo", "color": "ff3c5000", "blend": "additive"},
             {"name": "moon", "bone": "moon", "attachment": "moon", "color": "ffffff00"},
             {"name": "hero", "bone": "hero", "attachment": "hero", "color": "ffffff00"}]
    att = {"moonhalo": {"moonhalo": {"width": round(MOON_D * 1.6, 1), "height": round(MOON_D * 1.6, 1)}},
           "moon": {"moon": {"width": round(MOON_D * moon.width / moon.height, 1), "height": round(MOON_D, 1)}},
           "hero": {"hero": {"width": round(HW, 1), "height": round(HERO_H, 1)}}}
    alpha = {"hero": fade,
             "moon": lambda t: fade(t) * 0.95,
             "moonhalo": lambda t: fade(t) * (0.5 + 0.15 * math.sin(t * 5.5))}
    # petals: same bones, sprites, keys and draw order as the official cut-in (drawn in front, 13 -> 1)
    for i in range(13, 0, -1):
        n = f"Petal{i}"
        b = ob[n]
        bones.append({"name": n, "parent": "petalscale", "x": round(b["x"] * OFF_K, 2), "y": round(b["y"] * OFF_K, 2),
                      "rotation": b.get("rotation", 0)})
        a = sk[n][n]
        att[n] = {n: {"x": round(a.get("x", 0) * OFF_K, 2), "y": round(a.get("y", 0) * OFF_K, 2), "rotation": a.get("rotation", 0),
                      "width": round(a["width"] * OFF_K, 2), "height": round(a["height"] * OFF_K, 2)}}
        slots.append({"name": n, "bone": n, "attachment": n, "color": "ffffff00"})
        alpha[n] = fade
        if n in an["bones"]:
            motions[n] = kit.spine_bone_motion(an["bones"][n], OFF_K)
    return kit.Rig(bones=bones, slots=slots, attachments=att, motions=motions, alpha=alpha, duration=1.6)
