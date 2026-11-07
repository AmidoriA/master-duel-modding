# Summon animation (Monster Cut-in) — reverse-engineering notes

Target: Master Duel build on disk 2026-10-30 (Unity 6000.3.18f1, IL2CPP metadata v39,
`GameAssembly.dll` SHA-256 `9248d992…40f6`). These notes come from static analysis of shipped files only.
The game was not run, so nothing here has been checked at runtime.
Constraint: no `.exe`/`.dll` changes. Every change described here is to data files under `LocalData`.

## TL;DR

- The card-specific "summon animation" is the **Monster Cut-in**: a **Spine 4.2.20** 2D skeletal animation
  played by a Unity Timeline. There are 520 sets (tcg) today.
- Whether a card gets one is **pure data**: `CardIndividualData.monsterCutinTable` (a list of `{mrk: cardId}`)
  in an ordinary AssetBundle with typetrees. No ownership or server check in `IsMonsterCutin`.
- The asset path is built from the card ID:
  `Duel/Timeline/Duel/MonsterCutIn/<_CARD_ILLUST_>/P{id}/<_RESOURCE_TYPE_>/P{id}` (code literal),
  with `<_CARD_ILLUST_>` = `tcg` and `<_RESOURCE_TYPE_>` = `SD` / `HighEnd_HD`.
- **Bundle file name = `crc32(UTF-8(logical path)).ToString("x8")`** (standard CRC32, original casing),
  stored at `LocalData/<player>/0000/<first 2 hex>/<name>`. Example:
  `crc32("Duel/Timeline/Duel/MonsterCutIn/tcg/P19172/P19172Timeline")` = `870e1b5e`.
  Checked against all 26,943 manifest entries: 100% match.
- Dependencies come from an **encrypted JSON manifest** (`manifest_tcg`, `manifest_Universal`, …), and we can
  decrypt and re-encrypt it (algorithm below).
- **Verdict: feasible as an asset/data patch, the same class of mod as art and over-frame replacement.**
  1. Add the target card ID to `monsterCutinTable`.
  2. Provide `P{targetId}` bundles, either cloned from an existing cut-in or rebuilt with a custom Spine export.
  3. Add the matching entries to `manifest_tcg`.

## How a cut-in is chosen and played (observed in code)

| Step | Method (addr) | What it does |
|------|---------------|--------------|
| Load table | `YgomGame.Duel.CardIndividualSetting.LoadSetting` (0x18051c2e0) | `ResourceManager.Load("Duel/ScriptableObject/CardIndividualData")` |
| Gate | `CardIndividualSetting.IsMonsterCutin(cardId)` (0x18051c1c0) | id → `OverFrameCardSetting.IsOverFrameIllust` base id → `SpecialIllustCardSetting.IsSpecialIllust` id → linear search of `monsterCutinTable[].mrk` |
| Setup | `YgomGame.Duel.MonsterCutinEffect.Setup` (0x1805aebf0 / 0x1805af0f0) | Reads card name, ATK/DEF, level/rank/link, attribute from `YgomGame.Card.Content` (overlay text is **live from the summoned card**). Asset id = card id (over-frame/special-illust mapped). If not in table → falls back to **13750** (0x35b6). Formats the path literal above. |
| Play | `SummonEffectBase.PlayMonsterCutin` (0x1805d3400) → `MonsterCutinEffect.Play` | Plays the timeline during the summon effect |
| User setting | `SettingsUtil.GetSkipMonsterCutinType`, `Util.IsSkipMonsterCutin`, `Util.RecordPlayedMonsterCutinCardID` | Options: On / 1st only / Off. "1st only" is tracked per card ID |

The cut-in plays from each client's local data, so a modded cut-in is visible only to the modder
(inference: opponents' clients use their own unmodified table).

## Asset layout of one cut-in (P19172, Odd-Eyes Arcray Dragon)

Root assets loaded by path: `…/P{id}/SD/P{id}` and `…/P{id}/HighEnd_HD/P{id}` (prefab:
`SkeletonAnimation`, `PlayableDirector`, `YgomSystem.Effect.ScreenEffect`, `ElementObjectManager`).
The manifest lists each root's dependencies:

```
P{id}/P{id}Timeline            TimelineAsset + SpineAnimationStateTrack/Clip (spine-timeline)
P{id}/animation                Spine.Unity.AnimationReferenceAsset
P{id}/<RT>/P{id}JS             TextAsset — Spine 4.2.20 skeleton JSON (bones 89, slots 20, anim "animation")
P{id}/<RT>/P{id}JS_SkeletonData Spine.Unity.SkeletonDataAsset
P{id}/<RT>/<scale>/P{id}       Texture2D atlas page (SD scale 0.43, HighEnd_HD 0.86)
P{id}/<RT>/<scale>/P{id}.atlas TextAsset — Spine atlas text
P{id}/<RT>/<scale>/P{id}_Atlas Spine.Unity.SpineAtlasAsset
P{id}/<RT>/<scale>/P{id}_Material Material
```

All bundles carry typetrees, so AssetsTools.NET (already used by Floowan.Core) can read and write them.

## Name hashing (observed)

`LocalFile.HashFileName` → `Internal.Helper.HashFileName` (0x18044a640) →
`YgomSystem.Hash.CRC32.GetStringCRC32` (0x180fe21b0): standard reflected CRC32 table, init `0xFFFFFFFF`,
final NOT, over `Encoding.UTF8.GetBytes(path)`, formatted `x8`. Asset-bundle paths first go through
`sanitizeAssetBundlePath` = `path.Replace("#/", "")`. Equivalent to `zlib.crc32`.

## Manifest (DependenciesList) format and crypto (observed)

- Files: `manifest`, `manifest_Universal`, `manifest_<lang>` (e.g. `en-US`), `manifest_<illust>` (`tcg`).
  Each file name is the CRC32 of that name, e.g. `manifest_tcg` → `70e201b4` (LocalData, ~4.2 MB).
- `DependenciesList.EncryptFromBytes` → `YgomSystem.SimpleCrypto.Encrypt(ref bytes, "manifest")` → UTF-8 JSON:
  `{"informations":[{"assetName":"…","dependencies":["…",…]},…]}`.
- Cipher (symmetric XOR stream): `seed = sum(UTF8("manifest"))`. Build a .NET-`System.Random`-style
  subtractive generator (MSEED 161803398, 56-int seed array, same init loops as .NET) **but with
  `inextp = 31`** (.NET uses 21). Per byte: `b ^= (byte)(int)(Sample() * (1/int.MaxValue) * 256)`.
- `AssetBundleLoader.GetWithDependenciesList` (0x180fded40): deps = `manifest.GetDependencies(path)`
  (falls back to the streaming manifest), then adds the path itself. **A path missing from the manifest is
  not rejected.** It loads with no dependencies, so a self-contained bundle would also work.

## Other per-card animation data (all editable ScriptableObjects in `Duel/ScriptableObject/*`)

- `CardRunEffectSetting` (70 entries): card `mrk` → activation effect timeline **path strings**
  (e.g. `Duel/Timeline/Card/<_CARD_ILLUST_>/Ef04354/Ef04354`). New cards can reuse existing effects by
  editing data only.
- `FusionEffectSetting`: Fusion spell `mrk` → fusion summon variant (Polymerization / Super Polymerization 7445 / Invocation 12852).
- `SpecialWinSetting` (Exodia, Destiny Board, …), `SummonCountSetting`, `AttackMotionSetting`
  (Blue-Eyes, Dark Magician, Egyptian gods, Red-Eyes), `CardIndividualData.mrkPowerTable` (meaning not confirmed;
  it may be the "strong summon" tier).

## Proposed patch design (for Floowan.Core)

1. **Table patch:** in bundle `crc32("Duel/ScriptableObject/CardIndividualData")` = `60c98f0d`, append
   `{mrk: targetId}` to `monsterCutinTable`. Back up the bundle first (as for art).
2. **Clone a cut-in** from `srcId` to `targetId`: for every asset in the src set, write a copy whose container path,
   `m_AssetBundleName` (`xx/xxxxxxxx`), `m_Dependencies` and CAB name use `P{targetId}`. Save each copy at
   `crc32(newPath)`. Renaming CABs avoids clashes when both cards appear in one duel.
   - Lighter variant: clone only the two root prefab bundles and point their manifest entries at src's deps.
3. **Manifest patch:** decrypt `manifest_tcg`, add entries for the new paths (deps rewritten), re-encrypt, back up.
4. **Custom animation (optional):** replace the `P{id}JS` skeleton JSON, the `.atlas` text and the atlas
   `Texture2D` in the cloned set with a Spine **4.2.x** export. Keep atlas page names consistent and keep the
   track name `animation`. No Unity build is needed.

## Open risks / unknowns (test in this order)

1. Does the game re-check the CRC of or re-download a modified `manifest_tcg` (or new, unknown files) at launch?
   Art mods survive launches, but nobody has tested this for the manifest.
2. A game update overwrites `manifest_tcg` and `CardIndividualData`, so the mod has to be re-applied (same as art mods).
3. The 5 table IDs with no tcg assets (13090, 13529, 13668, 16091, 16740) have not been explained. They may be alt-art or ocg/common.
4. Whether the timeline or prefab has any other per-card hook (voice/SE labels) has not been checked.
5. Smallest runtime check: clone an existing cut-in (e.g. Blue-Eyes 4007) onto a monster with none,
   with "Monster cut-in: On", in Solo mode.

## Proof of concept: Blue-Eyes cut-in on Magician's Rod

Now `monster_cutin.py apply --card 12418 --borrow 4007` (see "Tooling" below). It uses the lighter variant of
the patch design: only the two root prefab bundles are cloned, and they share Blue-Eyes' other 18 bundles
through new manifest entries.

Files changed under `LocalData/2addcbb5/0000`:

| File | Change |
|------|--------|
| `60/60c98f0d` CardIndividualData | `monsterCutinTable` 525 → 526, `{mrk: 12418}` appended |
| `70/70e201b4` manifest_tcg | 2 entries added at the front (`P12418/SD/P12418`, `P12418/HighEnd_HD/P12418`) |
| `38/38d29a7b` new | clone of `e70e4962` (P4007 SD root), CAB `CAB-a7280876…` |
| `a7/a7a999c1` new | clone of `1b436b39` (P4007 HighEnd_HD root), CAB `CAB-d618eb34…` |

Checked offline: UnityPy and AssetsTools.NET both reload the files. In each bundle, only the intended object
changed (AssetBundle header or the table). Header flags match the shipped `0x243` layout apart from LZ4 vs LZ4HC.
All dependency files exist on disk.

**Confirmed in game (2026-11-07):** with Monster cut-in **On**, summoning Magician's Rod plays the
Blue-Eyes cut-in. The game launched with the edited `manifest_tcg` and did not re-download or reject it
(this answers open risk 1 for at least one launch).

## Custom animation: Magician's Rod from its own art

This replaces the borrowed Blue-Eyes cut-in with a new Spine animation built from Magician's Rod's own illustration.

Scene: [scripts/cutin/scenes/magicians_rod_12418.py](../scripts/cutin/scenes/magicians_rod_12418.py)
(confirmed in game on 2026-11-08 after the user asked for more focus on the spirit).

**Art layers** (the scene's `comfy()` jobs, run by `scene_kit.py --comfy` against a local ComfyUI):
RealESRGAN 4× → Qwen-Image-Edit 2509 (Lightning 8-step) edits of the card art. The edits produce:
the rod on magenta (chroma-keyed), the art with the rod removed, sky only, and the spirit on black. Each result
is upscaled 4× again. FLUX Fill outpainting of the sky came out starless, so the wide sky is the Qwen sky
mirrored left/right, with procedural stars added.

**Animation** (the scene's `rig()`, written out by [scripts/cutin/scene_kit.py](../scripts/cutin/scene_kit.py)): a
generated Spine 4.2.20 JSON, 2.0 s long, with linear keys at 30/s.

The spirit is the focus and the rod is a supporting accent. The spirit is drawn in three passes: a blurred
additive aura, a solid deep-blue "body" pass (normal blend, so it reads against the sky), and a pulsing additive glow.

| Time (s) | What happens |
|----------|--------------|
| 0–0.22 | The dimmed star band opens (scaleY) |
| 0.03–0.6 | The spirit materializes: the aura flares and settles, and the body and glow rise in from below, shrinking from 1.18× to 1.0× |
| 0.2–0.7 | A faint magic circle appears behind the spirit and rotates |
| 0.4–1.6 | Sparkles pop around the spirit's outline |
| 0.62–1.0 | A diagonal light sweep passes across the spirit |
| 0.75–1.07 | The rod (0.66×) swoops in at the bottom right |
| 1.05 | The orb flashes and a light streak runs along the rod |
| 1.62–2.0 | Everything fades out and the band closes |

Framing: camera ortho size 5 → 10 world units tall. Skeleton scale 0.01 × prefab scale 0.24 makes the
visible area about ±3700 × ±2080 skeleton units. Textures are premultiplied (`pma:true`), packed into 4096² (HD)
and 2048² (SD) pages, and encoded BC7 inline (via `etcpak`), so there is no `.resS`.
The script also renders `preview.gif` / `preview_sheet.png` with the same bone math, so the animation can be
checked without starting the game.

**Packaging** (`monster_cutin.py apply --card 12418 --content <work>/out --preview-approved`):
1. Clone all 16 bundles of a single-page template cut-in (P19172, Odd-Eyes Arcray Dragon) as P12418.
2. Give every bundle a new CAB name and rewrite every `archive:/CAB-…` external, `m_Dependencies` and
   `m_Container` entry, so nothing is shared with the template.
3. Replace both skeleton JSONs, both atlas texts and both textures (keeping the texture/page name `P19172`,
   which the atlas uses to match materials), and set the timeline clip length to 2.0 s.
4. Rebuild the manifest from the original backup with 16 `P12418` entries.

Checked offline: each bundle's externals point only inside the new set, all dependency files exist, the
text and textures match the generated content, the timeline is 2.0 s, and AssetsTools.NET reloads all of them.

## Tooling: making, installing and restoring cut-ins

The step-by-step workflow for agents is the `monster-cutin` skill (`.cursor/skills/monster-cutin/SKILL.md`,
shared with Claude Code). It requires showing the user `preview.gif` and getting approval before any patch.

| Tool | Purpose |
|------|---------|
| [scripts/cutin/monster_cutin.py](../scripts/cutin/monster_cutin.py) | `status`, `find-card`, `extract-art`, `apply` (custom with `--preview-approved`, or `--borrow SRC`), `restore`, `verify`, `remove` |
| [scripts/cutin/scene_kit.py](../scripts/cutin/scene_kit.py) | Builds layers, atlases, Spine JSON and the preview from a scene file; includes the ComfyUI client |
| [scripts/cutin/scenes/](../scripts/cutin/scenes/) | One scene file per card (art prompts and animation) |

**Registry and game updates.** Installed cut-ins are recorded in `%LOCALAPPDATA%\Floowan\cutins\registry.json`,
together with their content (`content/<id>/`) and pristine copies of the two shared game files
(`pristine/`). Every `apply` / `restore` / `remove` rebuilds the game state from the pristine copies plus all
registered cut-ins, so the result does not depend on what was patched before.

A game update overwrites CardIndividualData and `manifest_tcg`. The tool notices because the files no longer
match the SHA-256 it recorded when it wrote them. `status` then reports `NEEDS RESTORE`. A single

```
python scripts/cutin/monster_cutin.py restore
```

takes the updated files as the new pristine copies and re-applies every registered cut-in. If an update gives a
card its own official cut-in, that card is reported as SKIPPED and the official one is left alone.
`remove --all` restores the original files byte for byte.

Tested on copies of the game files: install a custom and a borrowed cut-in, simulate an update (new manifest
entry plus an official cut-in for the borrowed card), `status`, `restore`, `verify`, `remove`. The updated
originals came back byte-identical after `remove --all`.
