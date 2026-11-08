"""Toolkit for building a custom Master Duel Monster Cut-in (Spine 4.2) from card art.

A *scene* is a small Python module in scripts/cutin/scenes/ that defines:

    CARD_ID   = 12418                  # card that gets the cut-in
    NAME      = "Magician's Rod"
    TEMPLATE  = 19172                  # single-atlas-page cut-in whose bundles get cloned (see docs)
    def comfy(work, kit): ...          # optional: ComfyUI jobs writing layer sources into <work>/layers_src
    def make_layers(work, kit): ...    # -> (images: {region: PIL RGBA straight alpha}, meta: dict)
    def rig(images, meta, kit): ...    # -> Rig(...) (bones, slots, attachments, motion + alpha functions)

Run:
    python scripts/cutin/scene_kit.py scripts/cutin/scenes/<scene>.py --work <dir> [--comfy]

Output in <work>/out: hd.png, sd.png, hd.atlas, sd.atlas, skeleton.json, meta.json,
preview.gif, preview_sheet.png. Show the preview to the user BEFORE running
`monster_cutin.py apply ... --preview-approved`.
"""
import argparse
import importlib.util
import json
import math
import os
import time
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from typing import Callable, Dict, List

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# Framing. Scenes are DESIGNED in a space where the screen is VIEW_H_UNITS tall (the prefab's
# ScreenEffect camera size 5, skeleton scale 0.01, prefab scale 0.24 of template P19172).
# The game actually shows a tighter view: calibrated from an in-game 16:9 screenshot of the
# Droll & Lock Bird cut-in (2026-11-08), the visible height is ~2420 units, i.e. an effective ortho
# size of ~2.9. write_skeleton scales the root bone by GAME_VIEW_H / VIEW_H_UNITS so the game shows
# exactly what the preview shows. Official cut-ins at this framing fill about the full screen height.
VIEW_H_UNITS = 10 / (0.01 * 0.24)          # ~4167: design space height
GAME_ORTHO_SIZE = 2.9                      # measured effective camera size in game
GAME_VIEW_H = 2 * GAME_ORTHO_SIZE / (0.01 * 0.24)   # ~2417 skeleton units visible in game
ROOT_SCALE = GAME_VIEW_H / VIEW_H_UNITS    # ~0.58
HD_PAGE, SD_PAGE = 4096, 2048


# ================================================================== image helpers

def arr(im):
    return np.asarray(im.convert("RGBA")).astype(np.float32) / 255.0


def img(a):
    return Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")


def premultiply(im):
    a = arr(im)
    a[..., :3] *= a[..., 3:4]
    return img(a)


def key_flat_background(path, size, t0=0.10, t1=0.30, fade_right=None, fade_bottom=None, despill="magenta"):
    """Cut out an object painted on a flat colour (magenta or green). Returns (RGBA, meta).

    The image is resized so its longest side is `size` (aspect kept). meta: crop box inside that
    image, `full` = size, `full_w`/`full_h` = its dimensions. fade_right / fade_bottom = (start, end)
    fractions fade alpha out towards that edge (for objects the source crops). despill: "magenta",
    "green" or None, matching the background colour asked for in the Qwen prompt."""
    src = arr(Image.open(path).convert("RGB"))[..., :3]
    h, w, _ = src.shape
    border = np.concatenate([src[:8].reshape(-1, 3), src[-8:].reshape(-1, 3), src[:, :8].reshape(-1, 3), src[:, -8:].reshape(-1, 3)])
    bg = np.median(border, axis=0)
    d = np.sqrt(((src - bg) ** 2).sum(-1))
    a = np.clip((d - t0) / (t1 - t0), 0, 1)
    fg = np.clip((src - (1 - a[..., None]) * bg) / np.maximum(a[..., None], 1e-3), 0, 1)
    if despill == "magenta":
        spill = np.clip(np.minimum(fg[..., 0], fg[..., 2]) - fg[..., 1], 0, 1) * (1 - a)
        fg[..., 0] -= spill
        fg[..., 2] -= spill
    elif despill == "green":
        # Strict: only for subjects with no green in them. Clearly green pixels (gaps in wings, hair
        # strands showing the screen) become transparent and any remaining green tint is clamped.
        excess = src[..., 1] - np.maximum(src[..., 0], src[..., 2])
        a *= np.clip(1 - (excess - 0.12) / 0.18, 0, 1)
        fg[..., 1] = np.minimum(fg[..., 1], np.maximum(fg[..., 0], fg[..., 2]))
    if fade_right:
        xs = np.linspace(0, 1, w)[None, :]
        a *= np.clip((fade_right[1] - xs) / (fade_right[1] - fade_right[0]), 0, 1)
    if fade_bottom:
        ys = np.linspace(0, 1, h)[:, None]
        a *= np.clip((fade_bottom[1] - ys) / (fade_bottom[1] - fade_bottom[0]), 0, 1)
    a = np.asarray(Image.fromarray((a * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2))).astype(np.float32) / 255
    k = size / max(w, h)
    fw, fh = round(w * k), round(h * k)
    full = img(np.dstack([fg, a])).resize((fw, fh), Image.LANCZOS)
    A = np.asarray(full)[..., 3]
    ys_, xs_ = np.nonzero(A > 6)
    x0, y0, x1, y1 = xs_.min(), ys_.min(), xs_.max() + 1, ys_.max() + 1
    return full.crop((x0, y0, x1, y1)), {"crop": [int(x0), int(y0), int(x1), int(y1)], "full": size,
                                          "full_w": fw, "full_h": fh, "_full": full}


def additive_layer(path, size, vignette=True):
    """Glowing subject on black -> straight-alpha RGBA usable for both normal and additive slots."""
    a = arr(Image.open(path).convert("RGB").resize((size, size), Image.LANCZOS))[..., :3]
    al = a.max(-1)
    vig = np.ones((size, size), np.float32)
    if vignette:
        yy, xx = np.mgrid[0:size, 0:size] / (size - 1.0)
        r = np.sqrt((xx - 0.5) ** 2 + (yy - 0.5) ** 2) / 0.5
        vig = np.clip((1.02 - r) / 0.25, 0, 1)
    rgb = a * vig[..., None]
    alpha = np.clip(al * vig, 1e-4, 1)
    return img(np.dstack([rgb / alpha[..., None], alpha]))


def blur_aura(layer, size=768, radius=28, gain=1.6):
    a = arr(layer.resize((size, size), Image.LANCZOS))
    rgb = Image.fromarray((np.clip(a[..., :3] * a[..., 3:4], 0, 1) * 255).astype(np.uint8), "RGB").filter(ImageFilter.GaussianBlur(radius))
    b = arr(rgb.convert("RGBA"))[..., :3]
    al = np.clip(b.max(-1) * gain, 1e-4, 1)
    return img(np.dstack([np.clip(b / al[..., None], 0, 1), al]))


def wide_band(path, W=3072, H=1024, overlap=192, stars=900, rng=None, feather_y=0.18, feather_x=0.06):
    """Square background -> wide band: mirrored tiles, fresh procedural stars, soft edges."""
    import random
    rng = rng or random.Random(1)
    c = arr(Image.open(path).convert("RGB").resize((H, H), Image.LANCZOS))[..., :3]
    out = np.zeros((H, W, 3), np.float32)
    wsum = np.zeros((H, W, 1), np.float32)
    mid = (W - H) // 2
    for x, t in [(mid - H + overlap, c[:, ::-1]), (mid, c), (mid + H - overlap, c[:, ::-1])]:
        wt = np.ones((H,), np.float32)
        ramp = np.linspace(0, 1, overlap)
        wt[:overlap] = ramp
        wt[-overlap:] = ramp[::-1]
        xa, xb = max(0, x), min(W, x + H)
        out[:, xa:xb] += t[:, xa - x: xb - x] * wt[None, xa - x: xb - x, None]
        wsum[:, xa:xb] += wt[None, xa - x: xb - x, None]
    out /= np.maximum(wsum, 1e-3)
    out[:, :overlap] = out[:, overlap:overlap + 1]          # outer margins: extend the edge column
    out[:, -overlap:] = out[:, -overlap - 1:-overlap]
    sky = Image.fromarray((out * 255).astype(np.uint8), "RGB")
    d = ImageDraw.Draw(sky)
    for _ in range(stars):
        x, y = rng.uniform(0, W), rng.uniform(0, H)
        r = rng.choice([1, 1, 1, 1.5, 2, 2.5, 3.5])
        d.ellipse((x - r, y - r, x + r, y + r), fill=rng.choice([(255, 255, 255), (190, 245, 255), (120, 230, 255)]))
    glow = sky.filter(ImageFilter.GaussianBlur(3))
    sky = Image.blend(sky, Image.fromarray(np.maximum(np.asarray(sky), np.asarray(glow))), 0.5)
    yy = np.linspace(0, 1, H)[:, None]
    xx = np.linspace(0, 1, W)[None, :]
    a = (np.clip(yy / feather_y, 0, 1) * np.clip((1 - yy) / feather_y, 0, 1) *
         np.clip(xx / feather_x, 0, 1) * np.clip((1 - xx) / feather_x, 0, 1)) ** 1.3
    return img(np.dstack([arr(sky)[..., :3], a]))


def pad(layer, margin):
    """Transparent margin around a layer so glows built from it are not clipped at its edges."""
    out = Image.new("RGBA", (layer.width + 2 * margin, layer.height + 2 * margin), (0, 0, 0, 0))
    out.paste(layer, (margin, margin))
    return out


def silhouette_glow(layer, size=640, radius=22, gain=1.5):
    """White glow following a layer's alpha (tint it with the slot colour, draw additive behind it)."""
    a = np.asarray(layer.resize((size, max(1, round(size * layer.height / layer.width))), Image.LANCZOS))[..., 3]
    g = np.asarray(Image.fromarray(a).filter(ImageFilter.GaussianBlur(radius))).astype(np.float32) / 255
    al = np.clip(g * gain, 0, 1)
    return img(np.dstack([np.ones(al.shape + (3,)), al]))


def fx_band(w=2048, h=512, feather_y=0.3, feather_x=0.08):
    """Plain soft-edged band (white; tint and alpha via the slot colour) to darken the field behind a hero."""
    yy = np.linspace(0, 1, h)[:, None]
    xx = np.linspace(0, 1, w)[None, :]
    a = (np.clip(yy / feather_y, 0, 1) * np.clip((1 - yy) / feather_y, 0, 1) *
         np.clip(xx / feather_x, 0, 1) * np.clip((1 - xx) / feather_x, 0, 1)) ** 1.2
    return img(np.dstack([np.ones((h, w, 3)), a]))


def fx_lightning(w=384, h=2048, seed=1, branches=3):
    """Jagged vertical lightning bolt with a soft glow (white; tint via the slot colour)."""
    import random
    rng = random.Random(seed)
    core = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(core)

    def bolt(x, y, length, width, drift):
        pts = [(x, y)]
        while pts[-1][1] < y + length and pts[-1][1] < h:
            px, py = pts[-1]
            pts.append((min(w - 8, max(8, px + rng.uniform(-1, 1) * w * 0.12 + drift)), py + rng.uniform(h * 0.03, h * 0.07)))
        d.line(pts, fill=255, width=width, joint="curve")
        return pts
    main = bolt(w / 2, 0, h, 7, 0)
    for _ in range(branches):
        sx, sy = main[rng.randrange(2, max(3, len(main) - 3))]
        bolt(sx, sy, h * rng.uniform(0.15, 0.35), 3, rng.choice([-1, 1]) * w * 0.04)
    glow = core.filter(ImageFilter.GaussianBlur(14))
    a = np.clip(np.asarray(core).astype(np.float32) / 255 + np.asarray(glow).astype(np.float32) / 255 * 1.6, 0, 1)
    return img(np.dstack([np.ones((h, w, 3)), a]))


def split_wing_pair(path, size=2048, despill="green"):
    """Key a symmetric wing pair painted on a flat colour and split it at the centre joint.
    Returns {'left', 'right', 'left_joint_y', 'right_joint_y', 'span_px'}: each wing's root (the joint)
    is at its inner edge, joint_y px from its top. Rig each wing on a bone at the joint and rotate it."""
    _, m = key_flat_background(path, size, despill=despill)
    full = m["_full"]
    A = np.asarray(full)[..., 3]
    cx = full.width // 2
    col = np.nonzero(A[:, cx - 8:cx + 8].max(axis=1) > 128)[0]
    jy = float(col.mean()) if len(col) else full.height / 2
    out = {}
    for side, box in (("left", (0, 0, cx, full.height)), ("right", (cx, 0, full.width, full.height))):
        half = largest_blob(full.crop(box))
        ys, xs = np.nonzero(np.asarray(half)[..., 3] > 6)
        y0, y1 = ys.min(), ys.max() + 1
        x0, x1 = (xs.min(), half.width) if side == "left" else (0, xs.max() + 1)
        out[side] = half.crop((x0, y0, x1, y1))
        out[side + "_joint_y"] = jy - y0
    out["span_px"] = out["left"].width + out["right"].width
    return out


def largest_blob(layer, min_share=0.02):
    """Clear stray specks: keep connected shapes holding at least `min_share` of the largest one's
    pixels. A single leftover pixel far from the subject would otherwise stretch its crop box and
    move the joint."""
    from scipy import ndimage
    a = np.asarray(layer).copy()
    lab, n = ndimage.label(a[..., 3] > 32)
    if n == 0:
        return layer
    sizes = ndimage.sum(np.ones_like(lab), lab, index=np.arange(1, n + 1))
    keep = np.zeros(n + 1, bool)
    keep[1:] = sizes >= sizes.max() * min_share
    near = ndimage.binary_dilation(keep[lab], iterations=4)          # keep soft edges around kept shapes
    a[..., 3] = np.where(near, a[..., 3], 0)
    return Image.fromarray(a, "RGBA")


def wing_attachments(wings, span_units):
    """Attachment entries placing each wing's root on its bone (see split_wing_pair)."""
    k = span_units / wings["span_px"]
    l, r = wings["left"], wings["right"]
    return ({"x": round(-l.width * k / 2, 1), "y": round((l.height / 2 - wings["left_joint_y"]) * k, 1),
             "width": round(l.width * k, 1), "height": round(l.height * k, 1)},
            {"x": round(r.width * k / 2, 1), "y": round((r.height / 2 - wings["right_joint_y"]) * k, 1),
             "width": round(r.width * k, 1), "height": round(r.height * k, 1)})


def fx_bat(size=256):
    """Bat silhouette (white; tint via the slot colour), wings spread."""
    s = size / 256.0
    im = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(im)
    wing = [(128, 118), (96, 96), (60, 84), (22, 92), (6, 120), (30, 116), (44, 134), (62, 126), (74, 146),
            (90, 136), (106, 150), (120, 140)]
    d.polygon([(x * s, y * s) for x, y in wing], fill=255)
    d.polygon([((256 - x) * s, y * s) for x, y in wing], fill=255)
    d.ellipse((112 * s, 104 * s, 144 * s, 156 * s), fill=255)                  # body
    d.polygon([(114 * s, 110 * s), (118 * s, 92 * s), (124 * s, 106 * s)], fill=255)   # ears
    d.polygon([(142 * s, 110 * s), (138 * s, 92 * s), (132 * s, 106 * s)], fill=255)
    a = np.asarray(im.filter(ImageFilter.GaussianBlur(1.2))).astype(np.float32) / 255
    return img(np.dstack([np.ones((size, size, 3)), a]))


def circle_mask(layer, inner=0.82, outer=1.0):
    """Fade a square image to a soft disc (for rune circles / moons painted on a square)."""
    a = arr(layer)
    hh, ww = a.shape[:2]
    yy, xx = np.mgrid[0:hh, 0:ww]
    r = np.sqrt(((xx - (ww - 1) / 2) / ((ww - 1) / 2)) ** 2 + ((yy - (hh - 1) / 2) / ((hh - 1) / 2)) ** 2)
    a[..., 3] *= np.clip((outer - r) / (outer - inner), 0, 1)
    return img(a)


def fx_radial(size=256, power=2.2, core=0.6):
    yy, xx = np.mgrid[0:size, 0:size] / (size - 1)
    r = np.sqrt((xx - 0.5) ** 2 + (yy - 0.5) ** 2) / 0.5
    a = np.clip(1 - r, 0, 1) ** power + core * np.clip(1 - r * 4, 0, 1)
    return img(np.dstack([np.ones((size, size, 3)), np.clip(a, 0, 1)]))


def fx_sparkle(size=256):
    yy, xx = np.mgrid[0:size, 0:size] / (size - 1) - 0.5
    ax, ay = np.abs(xx) * 2, np.abs(yy) * 2
    arm = np.clip(1 - ax, 0, 1) ** 6 * np.exp(-(ay * 40) ** 2) + np.clip(1 - ay, 0, 1) ** 6 * np.exp(-(ax * 40) ** 2)
    a = np.clip(arm + np.clip(1 - np.sqrt(ax ** 2 + ay ** 2) * 3, 0, 1) ** 2, 0, 1)
    return img(np.dstack([np.ones((size, size, 3)), a]))


def fx_streak(w=1024, h=64):
    yy, xx = np.mgrid[0:h, 0:w]
    u, v = xx / (w - 1), (yy - (h - 1) / 2) / ((h - 1) / 2)
    a = np.exp(-(v * 2.6) ** 2) * np.sin(np.pi * u) ** 1.5
    return img(np.dstack([np.ones((h, w, 3)), a]))


def fx_magic_circle(size=1024):
    im = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(im)
    c = size / 2

    def circ(r, wdt):
        d.ellipse((c - r, c - r, c + r, c + r), outline=255, width=wdt)
    circ(c - 8, 6); circ(c - 40, 3); circ(c * 0.62, 4); circ(c * 0.58, 2)
    for k in range(24):
        ang = k * math.pi / 12
        d.line((c + (c - 40) * math.cos(ang), c + (c - 40) * math.sin(ang), c + (c - 8) * math.cos(ang), c + (c - 8) * math.sin(ang)), fill=255, width=3)
    pts = [(c + c * 0.58 * math.cos(math.pi / 2 + k * math.pi / 3), c + c * 0.58 * math.sin(math.pi / 2 + k * math.pi / 3)) for k in range(6)]
    d.polygon([pts[0], pts[2], pts[4]], outline=255, width=3)
    d.polygon([pts[1], pts[3], pts[5]], outline=255, width=3)
    for k in range(12):
        ang = k * math.pi / 6 + math.pi / 12
        r = c - 24
        d.ellipse((c + r * math.cos(ang) - 7, c + r * math.sin(ang) - 7, c + r * math.cos(ang) + 7, c + r * math.sin(ang) + 7), fill=255)
    blur = im.filter(ImageFilter.GaussianBlur(6))
    a = np.clip(np.asarray(im).astype(np.float32) / 255 + np.asarray(blur).astype(np.float32) / 255 * 0.8, 0, 1)
    return img(np.dstack([np.ones((size, size, 3)), a]))


# ================================================================== reuse parts of a shipped cut-in

def atlas_regions(atlas_path, names=None):
    """Read regions from a Spine atlas (4.x 'bounds:' or legacy 'xy:/size:' format) as straight-alpha
    RGBA images. Pages are looked up next to the atlas; premultiplied pages are un-premultiplied and
    rotated regions are turned back upright. names: optional iterable / predicate to filter."""
    lines = open(atlas_path, encoding="utf-8").read().splitlines()
    base = os.path.dirname(atlas_path)
    out, page, pma, i = {}, None, False, 0
    want = (lambda n: n in names) if names is not None and not callable(names) else (names or (lambda n: True))
    while i < len(lines):
        ln = lines[i].strip()
        if not ln:
            page, i = None, i + 1
            continue
        if page is None:      # page header
            page = Image.open(os.path.join(base, ln)).convert("RGBA")
            i += 1
            while i < len(lines) and ":" in lines[i]:      # page properties (size, format, filter, pma...)
                i += 1
            # Master Duel's cut-in pages are premultiplied even when a legacy atlas does not say so
            pma = True
            continue
        name, props = ln, {}
        i += 1
        while i < len(lines) and ":" in lines[i] and not lines[i].strip().endswith(".png"):
            k, v = [x.strip() for x in lines[i].split(":", 1)]
            props[k] = v
            i += 1
        if not want(name):
            continue
        rot = props.get("rotate", "false") in ("true", "90")
        if "bounds" in props:
            x, y, w, h = map(int, props["bounds"].split(","))
            cw, ch = (h, w) if rot else (w, h)
        else:
            x, y = map(int, props["xy"].split(","))
            w, h = map(int, props["size"].split(","))
            cw, ch = (h, w) if rot else (w, h)
        reg = page.crop((x, y, x + cw, y + ch))
        if rot:
            reg = reg.rotate(-90, expand=True)     # packer stores rotated regions 90 deg counter-clockwise
        if pma:
            a = arr(reg)
            a[..., :3] = np.where(a[..., 3:4] > 1e-3, a[..., :3] / np.maximum(a[..., 3:4], 1e-3), 0)
            reg = img(a)
        out[name] = reg
    return out


def _bezier_at(t, t0, v0, t1, v1, c):
    """Spine 4 curve segment (cx1, cy1, cx2, cy2 in absolute time/value) evaluated at time t."""
    cx1, cy1, cx2, cy2 = c
    best_v, best_d = v0, 1e9
    for k in range(65):
        s = k / 64
        u = 1 - s
        tt = u ** 3 * t0 + 3 * u * u * s * cx1 + 3 * u * s * s * cx2 + s ** 3 * t1
        vv = u ** 3 * v0 + 3 * u * u * s * cy1 + 3 * u * s * s * cy2 + s ** 3 * v1
        d = abs(tt - t)
        if d < best_d:
            best_d, best_v = d, vv
    return best_v


def _channel(keys, field, default, idx, nch):
    """Value of one channel of a Spine timeline at time t, honouring linear / stepped / bezier."""
    def f(t):
        if not keys:
            return default
        times = [k.get("time", 0.0) for k in keys]
        if t <= times[0]:
            return keys[0].get(field, default)
        for i in range(len(keys) - 1):
            t0, t1 = times[i], times[i + 1]
            if t0 <= t < t1:
                v0, v1 = keys[i].get(field, default), keys[i + 1].get(field, default)
                c = keys[i].get("curve")
                if c == "stepped":
                    return v0
                if isinstance(c, list):
                    return _bezier_at(t, t0, v0, t1, v1, c[idx * 4: idx * 4 + 4])
                return v0 + (v1 - v0) * (t - t0) / (t1 - t0)
        return keys[-1].get(field, default)
    return f


def spine_bone_motion(timeline, unit_scale=1.0):
    """Turn a shipped skeleton's bone timeline ({rotate, translate, scale, ...}) into a Rig motion
    function. unit_scale converts translate values into our skeleton units."""
    parts = {}
    if "rotate" in timeline:
        parts["rotate"] = _channel(timeline["rotate"], "value", 0.0, 0, 1)
    if "translate" in timeline:
        fx, fy = _channel(timeline["translate"], "x", 0.0, 0, 2), _channel(timeline["translate"], "y", 0.0, 1, 2)
        parts["translate"] = lambda t: (fx(t) * unit_scale, fy(t) * unit_scale)
    if "scale" in timeline:
        sx, sy = _channel(timeline["scale"], "x", 1.0, 0, 2), _channel(timeline["scale"], "y", 1.0, 1, 2)
        parts["scale"] = lambda t: (sx(t), sy(t))

    def f(t):
        return {k: (v(t) if k == "rotate" else v(t)) for k, v in parts.items()}
    return f


# ================================================================== easing / keys

def lerp(a, b, t): return a + (b - a) * t
def ease_out(t): return 1 - (1 - t) ** 3
def ease_in(t): return t ** 3
def ease_inout(t): return 3 * t * t - 2 * t * t * t
def back_out(t, s=1.4): t -= 1; return t * t * ((s + 1) * t + s) + 1


def seg(t, t0, t1, a, b, ease=lambda x: x):
    """Value a before t0, b after t1, eased in between."""
    if t <= t0: return a
    if t >= t1: return b
    return lerp(a, b, ease((t - t0) / (t1 - t0)))


@dataclass
class Rig:
    """Scene description. Bone motions are offsets on top of the setup pose (Spine semantics:
    translate/rotate are added, scale multiplies). alpha[slot](t) in 0..1 drives the slot colour alpha."""
    bones: List[dict]
    slots: List[dict]
    attachments: Dict[str, dict]
    motions: Dict[str, Callable] = field(default_factory=dict)
    alpha: Dict[str, Callable] = field(default_factory=dict)
    duration: float = 2.0
    keys_per_sec: int = 30


def stretch_time(rig, factor):
    """Play the whole rig `factor` times slower (2.0 = half speed): every motion, fade and loop
    is evaluated at t / factor and the duration grows by `factor`. The game's timeline clip takes
    the new length (monster_cutin.py sets it from the animation)."""
    wrap = lambda fn: (lambda t: fn(t / factor))
    return Rig(bones=rig.bones, slots=rig.slots, attachments=rig.attachments,
               motions={k: wrap(v) for k, v in rig.motions.items()},
               alpha={k: wrap(v) for k, v in rig.alpha.items()},
               duration=rig.duration * factor, keys_per_sec=rig.keys_per_sec)


# ================================================================== atlas packing

def pack_atlases(images, out_dir, page_name):
    """Skyline-pack straight-alpha layers into premultiplied HD (4096) and SD (2048) pages.

    Positions are multiples of 4 with 4 px gaps so the half-size SD page keeps 2 px gaps."""
    pad = 4
    sky = [[0, HD_PAGE, 0]]          # skyline segments: [x, width, top y]
    pos = {}
    for name, im in sorted(images.items(), key=lambda kv: (-max(kv[1].size), -kv[1].width * kv[1].height)):
        w, h = im.width + pad, im.height + pad
        best = None
        for i in range(len(sky)):
            x = sky[i][0]
            if x + w > HD_PAGE:
                break
            # top of the span [x, x+w)
            y, span, j = 0, 0, i
            while span < w and j < len(sky):
                y = max(y, sky[j][2])
                span += sky[j][1] if j > i else sky[j][0] + sky[j][1] - x
                j += 1
            if span >= w and y + h <= HD_PAGE and (best is None or (y, x) < (best[0], best[1])):
                best = (y, x)
        if best is None:
            raise RuntimeError(f"layers do not fit a {HD_PAGE} page; shrink '{name}' ({im.size})")
        y, x = best
        pos[name] = (x + 2, y + 2)
        # raise the skyline over [x, x+w)
        new, end = [], x + w
        for sx, sw, sy in sky:
            se = sx + sw
            if se <= x or sx >= end:
                new.append([sx, sw, sy])
                continue
            if sx < x:
                new.append([sx, x - sx, sy])
            if se > end:
                new.append([end, se - end, sy])
        new.append([x, w, y + h])
        sky = sorted(new)
    texts = {}
    for tag, page, scale, label in (("hd", HD_PAGE, 1.0, "0.86"), ("sd", SD_PAGE, 0.5, "0.43")):
        canvas = Image.new("RGBA", (page, page), (0, 0, 0, 0))
        lines = [page_name, f"size:{page},{page}", "filter:Linear,Linear", "pma:true", f"scale:{label}"]
        for name, im in images.items():
            w, h = max(1, round(im.width * scale)), max(1, round(im.height * scale))
            sx, sy = round(pos[name][0] * scale), round(pos[name][1] * scale)
            src = im if scale == 1.0 else im.resize((w, h), Image.LANCZOS)
            canvas.alpha_composite(premultiply(src), (sx, sy))
            lines += [name, f"bounds:{sx},{sy},{w},{h}"]
        canvas.save(os.path.join(out_dir, f"{tag}.png"))
        texts[tag] = "\n".join(lines) + "\n"
        open(os.path.join(out_dir, f"{tag}.atlas"), "w", encoding="utf-8", newline="\n").write(texts[tag])
    return pos


# ================================================================== spine json

def write_skeleton(rig, out_dir, card_id):
    T = [round(i / rig.keys_per_sec, 4) for i in range(int(rig.duration * rig.keys_per_sec) + 1)]

    def bone_keys(fn):
        tr, ro, sc = [], [], []
        for t in T:
            v = fn(t)
            if "translate" in v: tr.append({"time": t, "x": round(v["translate"][0], 2), "y": round(v["translate"][1], 2)})
            if "rotate" in v: ro.append({"time": t, "value": round(v["rotate"], 3)})
            if "scale" in v: sc.append({"time": t, "x": round(v["scale"][0], 4), "y": round(v["scale"][1], 4)})
        return {k: v for k, v in (("translate", tr), ("rotate", ro), ("scale", sc)) if v}

    def slot_keys(base, fn):
        return {"rgba": [{"time": t, "color": base[:6] + "%02x" % int(round(max(0.0, min(1.0, fn(t))) * 255))} for t in T]}

    anim = {"slots": {s["name"]: slot_keys(s.get("color", "ffffffff"), rig.alpha[s["name"]]) for s in rig.slots if s["name"] in rig.alpha},
            "bones": {b: bone_keys(fn) for b, fn in rig.motions.items()}}
    skel = {"skeleton": {"hash": f"cutin{card_id}", "spine": "4.2.20", "x": -3900, "y": -1300, "width": 7800, "height": 2600,
                         "fps": 60, "images": "./images/", "audio": ""},
            "bones": rig.bones, "slots": rig.slots,
            "skins": [{"name": "default", "attachments": rig.attachments}],
            "animations": {"animation": anim}}
    json.dump(skel, open(os.path.join(out_dir, "skeleton.json"), "w", encoding="utf-8", newline="\n"), separators=(",", ":"))
    return skel


# ================================================================== preview (same bone math as Spine)

def render_preview(rig, images, out_dir, W=960, H=540, fps=30, background=None):
    ppu = H / GAME_VIEW_H      # game framing; the root bone carries ROOT_SCALE
    bones = {b["name"]: b for b in rig.bones}
    bg = Image.open(background).convert("RGBA").resize((W, H)) if background else Image.new("RGBA", (W, H), (40, 44, 52, 255))
    S = np.array([[ppu, 0, W / 2], [0, -ppu, H / 2], [0, 0, 1]])

    def world(name, t):
        b = bones[name]
        m = rig.motions.get(name, lambda t: {})(t)
        tx, ty = m.get("translate", (0, 0))
        r = math.radians(b.get("rotation", 0) + m.get("rotate", 0))
        sx, sy = m.get("scale", (1, 1))
        sx *= b.get("scaleX", 1)
        sy *= b.get("scaleY", 1)
        local = np.array([[math.cos(r) * sx, -math.sin(r) * sy, b.get("x", 0) + tx],
                          [math.sin(r) * sx, math.cos(r) * sy, b.get("y", 0) + ty], [0, 0, 1]])
        return world(b["parent"], t) @ local if "parent" in b else local

    frames = []
    for fi in range(int(rig.duration * fps) + 1):
        t = fi / fps
        canvas = (np.asarray(bg).astype(np.float32) / 255)[..., :3].copy()
        for s in rig.slots:
            al = rig.alpha.get(s["name"], lambda t: 1.0)(t)
            if al <= 0.003:
                continue
            region = s["attachment"]
            a = rig.attachments[s["name"]][region]
            im = images[region]
            M = world(s["bone"], t) @ np.array([[1, 0, a.get("x", 0)], [0, 1, a.get("y", 0)], [0, 0, 1]])
            if a.get("rotation"):
                r = math.radians(a["rotation"])
                M = M @ np.array([[math.cos(r), -math.sin(r), 0], [math.sin(r), math.cos(r), 0], [0, 0, 1]])
            w, h = a["width"], a["height"]
            Q = S @ M @ np.array([[w / im.width, 0, -w / 2], [0, -h / im.height, h / 2], [0, 0, 1]])
            Qi = np.linalg.inv(Q)
            layer = im.transform((W, H), Image.AFFINE, (Qi[0, 0], Qi[0, 1], Qi[0, 2], Qi[1, 0], Qi[1, 1], Qi[1, 2]), resample=Image.BILINEAR)
            L = np.asarray(layer).astype(np.float32) / 255
            col = np.array([int(s.get("color", "ffffff")[i:i + 2], 16) / 255 for i in (0, 2, 4)])
            rgb, A = L[..., :3] * col, L[..., 3:4] * al
            canvas = canvas + rgb * A if s.get("blend") == "additive" else canvas * (1 - A) + rgb * A
        frames.append(Image.fromarray((np.clip(canvas, 0, 1) * 255).astype(np.uint8), "RGB"))
    save_previews(frames, out_dir, fps, "preview")


def save_previews(frames, out_dir, fps, stem):
    """Write <stem>.mp4 (exact colours; show this one to the user), <stem>.gif and a contact sheet.

    A GIF has at most 256 colours per frame; with a per-frame palette a detailed character is
    re-quantized differently every frame and its colours visibly shimmer. The GIF therefore uses ONE
    palette built from frames across the whole animation, so colours stay put between frames."""
    try:
        import imageio_ffmpeg
        W, H = frames[0].size
        w = imageio_ffmpeg.write_frames(os.path.join(out_dir, stem + ".mp4"), (W, H), fps=fps, codec="libx264",
                                        pix_fmt_out="yuv420p", macro_block_size=1,
                                        output_params=["-crf", "14", "-preset", "slow", "-movflags", "+faststart"])
        w.send(None)
        for f in frames:
            w.send(np.ascontiguousarray(np.asarray(f.convert("RGB"))))
        w.close()
    except Exception as e:                     # imageio-ffmpeg missing: the GIF still works
        print(f"  (no mp4 preview: {e})")
    step = max(1, len(frames) // 16)
    sample = frames[::step][:16]
    mosaic = Image.new("RGB", (frames[0].width, frames[0].height * len(sample)))
    for i, f in enumerate(sample):
        mosaic.paste(f, (0, i * frames[0].height))
    pal = mosaic.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    gif = [f.quantize(palette=pal, dither=Image.Dither.FLOYDSTEINBERG) for f in frames]
    gif[0].save(os.path.join(out_dir, stem + ".gif"), save_all=True, append_images=gif[1:], duration=int(1000 / fps), loop=0)
    W, H = frames[0].size
    picks = [min(len(frames) - 1, round(k * (len(frames) - 1) / 7)) for k in range(8)]
    sheet = Image.new("RGB", (W // 2 * 4, H // 2 * 2))
    for k, fi in enumerate(picks):
        sheet.paste(frames[fi].resize((W // 2, H // 2)), ((k % 4) * W // 2, (k // 4) * H // 2))
    sheet.save(os.path.join(out_dir, stem + "_sheet.png"))


# ================================================================== ComfyUI client

COMFY = os.environ.get("COMFYUI_URL", "http://127.0.0.1:8188")


def comfy_upload(path, name=None):
    name = name or os.path.basename(path)
    boundary = uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"{name}\"\r\nContent-Type: image/png\r\n\r\n").encode() \
        + open(path, "rb").read() + f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(COMFY + "/upload/image", data=body, headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    return json.load(urllib.request.urlopen(req))["name"]


def comfy_run(prompt, out_path, timeout=1800):
    """Queue a workflow, wait, save the first output image to out_path."""
    req = urllib.request.Request(COMFY + "/prompt", data=json.dumps({"prompt": prompt, "client_id": "cutin"}).encode(),
                                 headers={"Content-Type": "application/json"})
    r = json.load(urllib.request.urlopen(req))
    if r.get("node_errors"):
        raise RuntimeError(r["node_errors"])
    pid, t0 = r["prompt_id"], time.time()
    while time.time() - t0 < timeout:
        h = json.load(urllib.request.urlopen(f"{COMFY}/history/{pid}"))
        if pid in h:
            if h[pid].get("status", {}).get("status_str") == "error":
                raise RuntimeError(json.dumps(h[pid]["status"])[:2000])
            for o in h[pid]["outputs"].values():
                for im in o.get("images", []):
                    q = urllib.parse.urlencode({"filename": im["filename"], "subfolder": im["subfolder"], "type": im["type"]})
                    open(out_path, "wb").write(urllib.request.urlopen(f"{COMFY}/view?{q}").read())
                    return out_path
            raise RuntimeError("workflow produced no image")
        time.sleep(2)
    raise TimeoutError(pid)


def wf_upscale(image_name, model="RealESRGAN_x4plus.safetensors"):
    return {"1": {"class_type": "LoadImage", "inputs": {"image": image_name}},
            "2": {"class_type": "UpscaleModelLoader", "inputs": {"model_name": model}},
            "3": {"class_type": "ImageUpscaleWithModel", "inputs": {"upscale_model": ["2", 0], "image": ["1", 0]}},
            "4": {"class_type": "SaveImage", "inputs": {"images": ["3", 0], "filename_prefix": "cutin/up"}}}


def wf_qwen_edit(image_name, prompt, seed=7, megapixels=1.0, steps=8):
    """Qwen-Image-Edit 2509 + Lightning 8-step LoRA (the models on the user's ComfyUI)."""
    return {"1": {"class_type": "LoadImage", "inputs": {"image": image_name}},
            "2": {"class_type": "ImageScaleToTotalPixels", "inputs": {"image": ["1", 0], "upscale_method": "lanczos", "megapixels": megapixels, "resolution_steps": 8}},
            "3": {"class_type": "UNETLoader", "inputs": {"unet_name": "qwen_image_edit_2509_fp8_e4m3fn.safetensors", "weight_dtype": "default"}},
            "4": {"class_type": "LoraLoaderModelOnly", "inputs": {"model": ["3", 0], "lora_name": "Qwen-Image-Edit-2509-Lightning-8steps-V1.0-bf16.safetensors", "strength_model": 1.0}},
            "5": {"class_type": "ModelSamplingAuraFlow", "inputs": {"model": ["4", 0], "shift": 3.0}},
            "6": {"class_type": "CLIPLoader", "inputs": {"clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors", "type": "qwen_image"}},
            "7": {"class_type": "VAELoader", "inputs": {"vae_name": "qwen_image_vae.safetensors"}},
            "8": {"class_type": "TextEncodeQwenImageEditPlus", "inputs": {"clip": ["6", 0], "prompt": prompt, "vae": ["7", 0], "image1": ["2", 0]}},
            "9": {"class_type": "TextEncodeQwenImageEditPlus", "inputs": {"clip": ["6", 0], "prompt": "", "vae": ["7", 0], "image1": ["2", 0]}},
            "10": {"class_type": "VAEEncode", "inputs": {"pixels": ["2", 0], "vae": ["7", 0]}},
            "11": {"class_type": "KSampler", "inputs": {"model": ["5", 0], "seed": seed, "steps": steps, "cfg": 1.0, "sampler_name": "euler", "scheduler": "simple",
                                                      "positive": ["8", 0], "negative": ["9", 0], "latent_image": ["10", 0], "denoise": 1.0}},
            "12": {"class_type": "VAEDecode", "inputs": {"samples": ["11", 0], "vae": ["7", 0]}},
            "13": {"class_type": "SaveImage", "inputs": {"images": ["12", 0], "filename_prefix": "cutin/qe"}}}


def qwen_edit(src_path, prompt, out_path, seed=7):
    return comfy_run(wf_qwen_edit(comfy_upload(src_path, "cutin_" + os.path.basename(src_path)), prompt, seed=seed), out_path)


def upscale(src_path, out_path):
    return comfy_run(wf_upscale(comfy_upload(src_path, "cutin_" + os.path.basename(src_path))), out_path)


# ================================================================== runner

def load_scene(path):
    spec = importlib.util.spec_from_file_location("cutin_scene", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main():
    import sys
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scene")
    ap.add_argument("--work", required=True, help="work folder; expects <work>/art.png (see monster_cutin.py extract-art)")
    ap.add_argument("--comfy", action="store_true", help="(re)run the scene's ComfyUI jobs first")
    a = ap.parse_args()
    kit = sys.modules[__name__]
    scene = load_scene(a.scene)
    os.makedirs(os.path.join(a.work, "layers_src"), exist_ok=True)
    out = os.path.join(a.work, "out")
    os.makedirs(os.path.join(out, "layers"), exist_ok=True)
    if a.comfy:
        scene.comfy(a.work, kit)
    images, meta = scene.make_layers(a.work, kit)
    for n, im in images.items():
        im.save(os.path.join(out, "layers", n + ".png"))
    pack_atlases(images, out, f"P{scene.TEMPLATE}.png")
    rig = scene.rig(images, meta, kit)
    root = next(b for b in rig.bones if b["name"] == "root")        # fit design space to the game camera
    root["scaleX"] = round(root.get("scaleX", 1) * ROOT_SCALE, 5)
    root["scaleY"] = round(root.get("scaleY", 1) * ROOT_SCALE, 5)
    write_skeleton(rig, out, scene.CARD_ID)
    json.dump({"card_id": scene.CARD_ID, "name": scene.NAME, "template": scene.TEMPLATE, "duration": rig.duration,
               "scene": os.path.basename(a.scene), "built": time.strftime("%Y-%m-%dT%H:%M:%S")},
              open(os.path.join(out, "meta.json"), "w", encoding="utf-8"), indent=1)
    bg = os.path.join(a.work, "duelbg.png")
    render_preview(rig, images, out, background=bg if os.path.exists(bg) else None)
    print(f"built {scene.NAME} ({scene.CARD_ID}) -> {out}\n  preview: {os.path.join(out, 'preview.gif')}")


if __name__ == "__main__":
    main()
