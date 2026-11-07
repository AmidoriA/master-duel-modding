---
name: monster-cutin
description: >-
  Creates and installs custom Master Duel Monster Cut-ins (summon animations) for a card, built
  from the card's own art with ComfyUI and Spine, and restores them after game updates. Use when
  the user asks to add, make, redo, borrow, preview, restore, re-apply, list or remove a summon
  animation / cut-in / summon cut-in for a card, or says a game update removed their cut-ins.
---

# Monster Cut-in (summon animation)

Data-only mod: never modify `masterduel.exe`, `GameAssembly.dll` or any other binary. How the game
picks and loads cut-ins is documented in `docs/summon-animation-research.md`; read it if something
here does not match what you see.

Tools (Python 3 with `UnityPy`, `etcpak`, `numpy`, `Pillow`):

- `scripts/cutin/monster_cutin.py` installs, restores, verifies and removes cut-ins. Its registry and
  backups live in `%LOCALAPPDATA%\Floowan\cutins` (outside the repo).
- `scripts/cutin/scene_kit.py` builds a cut-in (layers, atlases, Spine 4.2 JSON, preview) from a scene file.
- `scripts/cutin/scenes/<slug>_<id>.py` holds one card's art prompts and animation. Copy
  `magicians_rod_12418.py` (approved in game) to start a new one.

## Hard rules

1. **Preview before patch.** Show the user `preview.gif` (and `preview_sheet.png`) and wait for
   an explicit approval in the conversation before running `apply ... --preview-approved`. Never pass
   that flag on your own judgement. If they ask for changes, rebuild and show the new preview.
2. **Game closed** for `apply`, `restore` and `remove` (the tool refuses while `masterduel.exe` runs).
   `status`, `verify`, `find-card` and `extract-art` are read-only and can run any time.
3. **Never commit generated images** (they are derived from Konami's card art). Commit only the
   scene `.py` file. Keep work folders outside the repo, e.g. `%LOCALAPPDATA%\Floowan\cutin-work\<id>`.
4. Do not edit `registry.json` or the pristine backups by hand.

## New custom cut-in: checklist

```
Cut-in Progress:
- [ ] 1. Identify the card and check it has no official cut-in
- [ ] 2. Extract the art into a work folder and look at it
- [ ] 3. Plan layers and the hero subject; agree briefly with the user
- [ ] 4. Write the scene file (prompts, layers, rig)
- [ ] 5. Build; inspect layers and preview yourself; iterate
- [ ] 6. Show preview.gif to the user; get explicit approval
- [ ] 7. Close the game; apply --preview-approved; verify
- [ ] 8. User tests in game (Settings > Monster cut-in: On)
- [ ] 9. Commit the scene file only
```

**1. Card.** `python scripts/cutin/monster_cutin.py find-card "<part of name>"` prints ids and tags
`official cut-in` / `custom cut-in`. A card with an official cut-in cannot get a custom one.

**2. Art.**
`python scripts/cutin/monster_cutin.py extract-art --card <id> --out <work>\art.png`, then view the image.

**3. Plan.** Decide the hero (the monster or character the card is about) and the supporting
elements (props, effects), and how the background extends into a wide band. Lessons from the
Magician's Rod cut-in, which the user corrected:
- The **main character must be the focus**: big, near the centre, appearing first. Props come later and smaller.
- A glowing or translucent subject drawn only with additive blend **washes out**. Layer it as a soft
  blurred aura (additive), a solid "body" pass (normal blend, deeper tint) and a pulsing glow
  (additive, lower alpha). Dim the background band so the hero reads.
- Visible area: about x ±3700, y ±2080 skeleton units. The band is about 2600 high. Keep the animation at
  about 2.0 s with a fade-out from about 1.6 s.

**4. Scene file.** Copy `scenes/magicians_rod_12418.py` to `scenes/<slug>_<id>.py` and set
`CARD_ID`, `NAME`, `TEMPLATE` (keep 19172 unless it is gone; see Troubleshooting).
- `PROMPTS` / `comfy()`: Qwen-Image-Edit jobs that split the art into layers. Describe the card's
  real subjects precisely. Put props on flat **magenta** (`#FF00FF`) so `key_flat_background` can cut
  them out, glowing subjects on **black** for `additive_layer`, and ask for a background-only image for
  `wide_band`. Upscale each layer with `upscale`. Seeds are fixed so results are repeatable.
- `make_layers()`: return `{region: RGBA}`. Available helpers: `key_flat_background`, `additive_layer`,
  `blur_aura`, `wide_band`, `fx_radial`, `fx_sparkle`, `fx_streak`, `fx_magic_circle`.
- `rig()`: bones, slots (`"blend": "additive"` for light), attachments (sizes in skeleton units), and
  per-bone `motions` / per-slot `alpha` functions of time, built from `seg` / easing helpers.
  Motions are offsets on the setup pose (translate and rotate add, scale multiplies).

ComfyUI must be running (default `http://127.0.0.1:8188`; override with the `COMFYUI_URL` env var).
Models used: `RealESRGAN_x4plus`, `qwen_image_edit_2509_fp8_e4m3fn` with
`Qwen-Image-Edit-2509-Lightning-8steps-V1.0-bf16`, `qwen_2.5_vl_7b_fp8_scaled`, `qwen_image_vae`.
The user allows downloading additional models when a job needs them. FLUX Fill outpainting did
not extend starfields well; mirrored tiles with procedural stars (`wide_band`) worked better.

**5. Build.**
```
python scripts/cutin/scene_kit.py scripts/cutin/scenes/<scene>.py --work <work> --comfy
```
Drop `--comfy` to rebuild without re-running ComfyUI. Look at `<work>\layers_src\*.png`,
`<work>\out\layers\*.png` (check the keying edges) and `<work>\out\preview_sheet.png` before showing anything.
Optional: put a duel screenshot at `<work>\duelbg.png` to preview over a real field.

**6. Preview gate.** Send `<work>\out\preview.gif` to the user, briefly describe the timing, and
ask for approval. Iterate on the scene until they approve.

**7. Install.**
```
python scripts/cutin/monster_cutin.py apply --card <id> --content <work>\out --preview-approved
python scripts/cutin/monster_cutin.py verify
```
This copies the content into the registry, clones the template's 16 bundles as `P<id>` with new
CAB names, swaps in the Spine data, and patches CardIndividualData and the manifest.

**8.** Ask the user to test it in game.

**9.** Commit `scripts/cutin/scenes/<scene>.py` (and any `scene_kit.py` improvements). Do not commit images.

## Borrow an existing cut-in

`apply --card <dst> --borrow <src>` gives card `dst` the existing cut-in of card `src`. Confirm
the source card with the user; there is no preview step because the animation already exists in the game.

## After a game update

Updates overwrite the patched files. Ask the user to close the game, then:
```
python scripts/cutin/monster_cutin.py status     # shows NEEDS RESTORE when the game replaced files
python scripts/cutin/monster_cutin.py restore    # re-applies every registered cut-in
python scripts/cutin/monster_cutin.py verify
```
`restore` backs up the new game files as the new pristine copies before patching. A card that
now has an official cut-in is reported as SKIPPED and left alone.

## Remove

`remove --card <id>` uninstalls one cut-in and deletes its stored content; `remove --all` uninstalls
everything and puts the original game files back byte for byte.

## Troubleshooting

- **Template missing after an update** (`template P19172 is not in the manifest`): choose another
  single-page cut-in. Look for a card whose `manifest_tcg` entries under
  `Duel/Timeline/Duel/MonsterCutIn/tcg/P<id>/` number exactly 16 (one `SD/<scale>/P<id>` texture and one
  `HighEnd_HD/<scale>/P<id>` texture). Set `TEMPLATE` in the scene, rebuild, preview, apply.
- **Player folder not found:** pass `--player-dir "<install>\LocalData\<playerId>"` once; it is remembered.
- **Nothing plays in game:** check Settings > Monster cut-in is On and run `verify`. Unity logs are in
  `%USERPROFILE%\AppData\LocalLow\Konami Digital Entertainment Co., Ltd_\masterduel\Player.log`.
- **Spine/Unity upgrade by Konami:** the skeleton says `"spine": "4.2.20"`. If shipped cut-ins move
  to a new Spine version, compare a shipped `P<id>JS` TextAsset with ours before applying.
