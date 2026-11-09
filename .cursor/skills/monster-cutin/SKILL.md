---
name: monster-cutin
description: >-
  Creates and installs custom Master Duel Monster Cut-ins (summon animations) for a card, built
  from the card's own art with ComfyUI and Spine, or from a WAN 2.2 image-to-video clip, and
  restores them after game updates. Use when
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
- Video cut-ins (see "Video cut-in" below): `scene_still.py` renders a 16:9 start image, `comfy_video.py`
  runs the **Yu-Gi-Oh cut-in** ComfyUI workflow (WAN 2.2 image-to-video), and `video_cutin.py` turns the
  clip into cut-in content. The workflow is in `workflows/` next to this file: `Yu-Gi-Oh cut-in.json` to
  load in ComfyUI, `yugioh_cutin_api.json` for `comfy_video.py`.

## Hard rules

1. **Preview before patch.** Show the user `preview.mp4` (and `preview_sheet.png`) and wait for
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
- [ ] 6. Show preview.mp4 to the user; get explicit approval
- [ ] 7. Close the game; apply --preview-approved; verify
- [ ] 8. User tests in game (Settings > Monster cut-in: On)
- [ ] 9. Commit the scene file only
```

**1. Card.** `python scripts/cutin/monster_cutin.py find-card "<part of name>"` prints ids and tags
`official cut-in` / `custom cut-in`. A card that already has an official cut-in can only be replaced with
`--override-official` (step 7), and only when the user asked for that card.

**2. Art.**
`python scripts/cutin/monster_cutin.py extract-art --card <id> --out <work>\art.png`, then view the image.
It returns whatever art the game currently shows, including the user's Floowan art or over-frame mods:
- An over-frame canvas is 704x1024. RGB is the full card including the frame; alpha is a mask, not
  transparency. Use the RGB and crop above the text box (about `(0, 0, 704, 760)`).
- The of_card_asset gate maps a custom over-frame card to itself (`9279 -> 9279`), so the cut-in still
  lives at `P<id>`. Official over-frame variants use other ids (for example 22768 -> 22690).

**3. Plan.** Decide the hero (the monster or character the card is about) and the supporting
elements (props, effects), and how the background extends into a wide band. Lessons from the
Magician's Rod cut-in, which the user corrected:
- The **main character must be the focus**: big, near the centre, appearing first. Props come later and smaller.
- A glowing or translucent subject drawn only with additive blend **washes out**. Layer it as a soft
  blurred aura (additive), a solid "body" pass (normal blend, deeper tint) and a pulsing glow
  (additive, lower alpha). Dim the background band so the hero reads.
- **Framing (calibrated from an in-game screenshot):** scenes are designed in a space where the screen
  is about x ±3700, y ±2080 units. `scene_kit` scales the root by 0.58 so the game shows exactly what the
  preview shows. Official cut-ins fill about the full screen height. The user found our heroes too big at
  full height and preferred `SCALE = 0.9` in the scene. A wide band is about 2600 high.
- **Length: exactly 2.0 s.** Use `DUR = 2.0` with a fade-out from about 1.62 s. The user wants a
  calm pace, so when they ask for "slower", use fewer and slower motions inside that length: wing
  beats about 0.9/s, a slow hover, a handful of particles. Never lengthen the clip, and don't use
  `kit.stretch_time` with a factor above 1. Mention the length in the preview message.
- **Keep the hero's colours steady.** Layers drawn over or around the hero (additive rim glow, chest
  glows, embers, flames) must not pulse quickly: a pulsing additive layer reads as the character
  flickering. Hold the rim at a constant alpha after the intro, keep flicker within about ±6%, and put
  drifting particles behind the hero.
- **The game draws the card name and ATK/DEF bar over the bottom ~23% of the screen.** Keep key
  moments (flashes, charms, faces) above it; check `preview_sheet.png` with that in mind.
- **When the card already has an official cut-in and the user wants something "similar to the
  original"**, study the official one first:
  `monster_cutin.py extract-cutin --card <id> --out <work>\official` (skeleton JSON, atlas, pages,
  `prefab_scale`, duration), then render it with the real Spine runtime:
  `python scripts/cutin/spine_preview/serve.py --content <work>\official --skeleton P<id>.json --atlas P<id>.atlas --prefab-scale <s>`
  in the background and open `http://127.0.0.1:8765/` in the browser pane. It writes
  `runtime_preview.mp4`, `runtime_preview.gif`, `runtime_preview_sheet.png` and frames. You can reuse the official keys directly:
  `kit.spine_bone_motion(timeline, OFF_K)` (Bézier curves included) and sprites with
  `kit.atlas_regions(atlas, predicate)`. Use
  `OFF_K = kit.VIEW_H_UNITS / (2 * kit.GAME_ORTHO_SIZE / (0.01 * prefab_scale))` (about 1.22 for 0.17).
  See `scenes/baronne_de_fleur_16386.py`.

**4. Scene file.** Copy `scenes/magicians_rod_12418.py` to `scenes/<slug>_<id>.py` and set
`CARD_ID`, `NAME`, `TEMPLATE` (keep 19172 unless it is gone; see Troubleshooting).
- `PROMPTS` / `comfy()`: Qwen-Image-Edit jobs that split the art into layers. Describe the card's
  real subjects precisely. Put props on flat **magenta** (`#FF00FF`) so `key_flat_background` can cut
  them out, glowing subjects on **black** for `additive_layer`, and ask for a background-only image for
  `wide_band`. Upscale each layer with `upscale`. Seeds are fixed so results are repeatable.
- `make_layers()`: return `{region: RGBA}`. Available helpers: `key_flat_background` (magenta or
  `despill="green"` for subjects without green; `fade_bottom` / `fade_right`), `additive_layer`,
  `blur_aura`, `wide_band`, `fx_radial`, `fx_sparkle`, `fx_streak`, `fx_magic_circle`, `atlas_regions`.
  Qwen can also complete parts the card hides (full unicorn legs, a whole moon, a creature's wings):
  ask for that part alone on green, then animate it as its own layer (see the wings in
  `droll_lock_bird_9279.py`, flapping around their root at a speed the user likes; they preferred about 1.8 beats/s).
  Pin the wing bone where the wings grow from the body, not at the shoulders by default: the user
  wanted Moa Regina's wings to meet behind the middle of her back, and Queen's Knight's to come from her
  hips, swept upwards with a rest rotation so they show beside her. If Qwen keeps the card frame or
  draws a whole bat when asked for wings alone, run the wings prompt on the green hero cutout instead
  (see `queens_knight_5503.py`).
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
The preview uses the in-game framing. To check with the real Spine runtime, run `spine_preview/serve.py`
on `<work>\out` with `--skeleton skeleton.json --atlas hd.atlas --page-alias P19172.png=hd.png --prefab-scale 0.24`.
Optional: put a duel screenshot at `<work>\duelbg.png` to preview over a real field.

**6. Preview gate.** Send `<work>\out\preview.mp4` to the user, briefly describe the timing, and
ask for approval. Use the MP4, not the GIF: a GIF has 256 colours, so even with the kit's single shared
palette the character's colours dither and band, and the user may read that as the animation flickering. Iterate on the scene until they approve.

**7. Install.**
```
python scripts/cutin/monster_cutin.py apply --card <id> --content <work>\out --preview-approved
python scripts/cutin/monster_cutin.py verify
```
This copies the content into the registry, clones the template's 16 bundles as `P<id>` with new
CAB names, swaps in the Spine data, and patches CardIndividualData and the manifest.
For a card with an official cut-in, add `--override-official`. Konami's files that get replaced
are backed up (and backed up again after an update changes them); `remove --card <id>` restores them.

**8.** Ask the user to test it in game.

**9.** Commit `scripts/cutin/scenes/<scene>.py` (and any `scene_kit.py` improvements). Do not commit images.

## Video cut-in (WAN 2.2 image-to-video)

A full-screen cut-in made from a 2 s AI video of the card's character, played as a Spine flipbook
(approved in game on Azamina Mu Rcielago, 20507). The hard rules above still apply: preview before
patch, game closed, never commit generated images or videos, and **exactly 2.0 s**.

```
Video cut-in Progress:
- [ ] 1. Card + art as in steps 1-2 above
- [ ] 2. 16:9 start image of the character at the cut-in framing; show it to the user
- [ ] 3. Write the motion idea; run the workflow; look at the frames yourself
- [ ] 4. video_cutin.py; show preview.mp4; get explicit approval
- [ ] 5. Close the game; apply --preview-approved; verify; user tests in game
```

**2. Start image (1920x1080).** WAN keeps the start image's aspect and identity, so this frame decides
the look. For a card that already has a scene, render it (uses `<work>/layers_src/bgup.png` as the
background behind the band, or `--background`):
```
python scripts/cutin/scene_still.py scripts/cutin/scenes/<scene>.py --work <work> --out <work>\video\start.png --t 1.0
```
Otherwise make a 16:9 picture from the art (hero cut-out over a widened background, as in a scene's
layers). Keep the face and key moments above the bottom ~23% (name and ATK/DEF bar).

**3. Video.** ComfyUI must be running; `comfy_video.py` uploads the image, queues the workflow and
downloads the final mp4 (plus a `.json` with the seed and prompts):
```
python scripts/cutin/comfy_video.py --image <work>\video\start.png --out <work>\video\wan.mp4 --idea "<motion>" [--global "<subject, style, camera>"] [--seed N]
```
- Defaults (tested): **720p** generation (0.88 MP, 1280x720) + **DLSS 1.5x** -> 1920x1080, one segment of
  33 frames at 16 fps, RIFE to 65 frames at 32 fps (2.03 s). About 100 s on the RTX 5080.
  480p + DLSS 3x (`--megapixels 0.38 --dlss "3x (Ultra Performance)"`) takes ~50 s but smears the face and
  lost the head of Azamina's spear; use it only for quick motion tests.
- The GLOBAL PROMPT asks for the same character and art style, centred, static camera. `--idea` is the
  motion for these 2 s: one or two actions (look into the camera, one wing beat, hair and flames moving,
  a slight push-in). The workflow's READ ME says "slowly" / "gently" really slow WAN down; the user wants a
  calm pace, so keep motion calm but describe it with concrete verbs.
- Check frames from the start, middle and end at full size: face, hands, weapons and props must stay
  intact. Colours drift slightly pinker/lower-contrast after the first frame; re-roll the seed if the
  character changes.
- The workflow's prompt writer (Gemini / local LLM) stays off (mode 1). The repo copy has an empty Gemini
  key box; never commit a key (the UI JSON also stores widget values under `widgets_values_named`).

**4. Content + preview.**
```
python scripts/cutin/video_cutin.py <work>\video\wan.mp4 --card <id> --name "<name>" --out <work>\video\out
```
It spreads the clip over 2.0 s at 30 fps (60 frames), packs them on one sheet (11544x10840 for 1080p,
2 px bled gutters), sizes the attachment to the game camera (+3% overscan) and writes `preview.mp4`,
`preview.gif` and `preview_sheet.png` at the in-game framing. For an exact check run `spine_preview/serve.py`
on the out folder (`--skeleton skeleton.json --atlas hd.atlas --page-alias P19172.png=hd.png --prefab-scale 0.24`).
Show `preview.mp4` to the user and wait for approval.

**5. Install** as in step 7 above (`apply ... --preview-approved`, then `verify`). Encoding the BC7 sheets
takes about a minute per video cut-in, and the HD bundle is about 90 MB. Before replacing an existing
custom cut-in, copy `%LOCALAPPDATA%\Floowan\cutins\content\<id>` into the work folder so the user can switch
back with `apply --content <copy> --preview-approved`.

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
