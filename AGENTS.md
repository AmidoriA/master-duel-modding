# AGENTS.md

Instructions for coding agents (Cursor, Claude Code, Codex, ...) working in this repo. Cursor reads this file directly; Claude Code reads it through `CLAUDE.md`.

## Project

**Masterduel modding tool by AmidoriA** (code folders still named `Floowan.*`) is a **Windows-only .NET 8 WPF desktop app** (`net8.0-windows`, `UseWPF`) for replacing Yu-Gi-Oh! Master Duel card art. Repo: `AmidoriA/master-duel-modding`, default branch `master`.

- `src/Floowan.Core` — non-UI class library (SQLite DB, path discovery, image prep, Unity AssetBundle I/O). Builds and runs on Linux.
- `src/Floowan.Desktop` — WPF GUI (`UseWPF=true`). **Cannot build or run on Linux** (requires the Windows Desktop targeting pack; fails with `NETSDK1100`). Build/run this only on Windows.
- `tests/Floowan.Core.Tests` — xUnit tests for Core. Builds and runs on Linux.

## Agent configuration (Cursor + Claude Code)

The rules and skills are written once and shared by both tools:

| What | Canonical file | Cursor | Claude Code |
|------|----------------|--------|-------------|
| Rule: UTF-8, never Windows-1252 | `.cursor/rules/utf8-no-windows-1252.mdc` | always applied | imported by `CLAUDE.md` |
| Rule: CRLF working tree | `.cursor/rules/windows-line-endings.mdc` | always applied | imported by `CLAUDE.md` |
| Skill: build and publish a release | `.cursor/skills/build-and-publish-release/SKILL.md` | skill | `.claude/skills/build-and-publish-release/SKILL.md` (stub that loads the Cursor copy) |
| Skill: custom Monster Cut-in (summon animation) | `.cursor/skills/monster-cutin/SKILL.md` | skill | `.claude/skills/monster-cutin/SKILL.md` (stub that loads the Cursor copy) |
| Skill: run and test the desktop app (UI Automation, real-data safety, pinned build) | `.cursor/skills/desktop-ui-testing/SKILL.md` | skill | `.claude/skills/desktop-ui-testing/SKILL.md` (stub that loads the Cursor copy) |

When you add or change agent configuration, keep both tools working:

- **New rule:** add `.cursor/rules/<name>.mdc` (frontmatter `description` + `alwaysApply: true`), add an `@.cursor/rules/<name>.mdc` line to `CLAUDE.md`, and add a row above.
- **New skill:** write it in `.cursor/skills/<name>/SKILL.md`, then add `.claude/skills/<name>/SKILL.md` with the same `name` and `description` frontmatter and a body that only says to read and follow the Cursor copy. Add a row above.
- Edit only the canonical file; the Claude side picks the change up without edits.

## Repository rules (always apply)

Summary of the rule files above (the `.mdc` files are authoritative):

- **Encoding:** all text source (`.cs`, `.xaml`, `.md`, `.json`, ...) is **UTF-8**; WPF XAML is UTF-8 **with BOM** like `App.xaml`. Never save as Windows-1252 / "ANSI"; don't introduce U+FFFD or mojibake when editing non-ASCII (—, …, ×, ≈).
- **Line endings:** `.gitattributes` has `* text=auto eol=crlf`, so the working tree is **CRLF** and Git stores LF. New files are CRLF too (`.editorconfig`). No mixed endings, no LF↔CRLF churn, no repo-wide renormalize unless a dedicated commit is explicitly requested. Mark new binary types `-text` in `.gitattributes`.
- **Releases:** follow the `build-and-publish-release` skill. Never create or push a tag, upload a release, or force-push without an explicit request.
- Don't commit `user.db`, `artifacts/`, or downloaded model weights (see `.gitignore`).

## Build & test (Windows)

```powershell
dotnet build Floowan.sln -c Release
dotnet test Floowan.sln -c Release
dotnet run --project src/Floowan.Desktop -c Release
```

To click through or verify UI changes, follow the `desktop-ui-testing` skill: drive one app instance by process id with `scripts/ui-test/floowan-ui.ps1` (UI Automation; finds elements by `x:Name`). Every copy of the app works on the real game install, so back up before anything that writes bundles.

## Linux (Cursor Cloud, Claude Code cloud sessions)

Cloud VMs are **Linux**, which limits what can run there.

### Toolchain
- On Cursor Cloud the .NET 8 SDK is installed at `~/.dotnet` (added to `PATH`/`DOTNET_ROOT` in `~/.bashrc`). If `dotnet` is not found, run `export PATH="$HOME/.dotnet:$PATH"`. Other Linux environments may need the .NET 8 SDK installed first.
- Because the target framework is `net8.0-windows`, Linux builds/restores **must** pass `-p:EnableWindowsTargeting=true`. Do not build `Floowan.sln` directly on Linux (it includes the WPF project and fails); target the Core/Tests projects instead.

### Build & test (Linux)
```bash
dotnet build tests/Floowan.Core.Tests/Floowan.Core.Tests.csproj -c Release -p:EnableWindowsTargeting=true
dotnet test  tests/Floowan.Core.Tests/Floowan.Core.Tests.csproj -c Release -p:EnableWindowsTargeting=true
```

### Known test behavior on Linux
3 tests fail on Linux and this is expected (not an environment problem): `GamePathLocatorTests.ResolveInstallRoot_WalksUpFromLocalData`, `GamePathLocatorTests.ResolveUnity3dPath_PointsAtMasterduelData`, and `BundlePathResolverTests.StreamingAssetsPath_UsesInstallRoot`. They assert against hardcoded Windows paths (`C:\...`, `D:\...`) with backslash separators, which `Path`/`Directory.GetParent` only interpret correctly on Windows. The other 10 tests pass. Run these tests on Windows for a full green suite.

### Running / demoing core logic on Linux
The WPF GUI can't run here. To exercise core logic end-to-end, reference `src/Floowan.Core/Floowan.Core.csproj` from a small console project (`net8.0-windows` + `EnableWindowsTargeting=true`) and call `CardDatabase`, `ImagePreparation`, and `ClassDataLocator` against the bundled `database.db`.

## Data / resources
- `database.db` (repo root, ~5.6 MB, ~14k cards) is the **master** SQLite card catalog (identity fields: `id`, `name`, `description`, `bundle`, `data_index`, plus optional `card_type` / `created_at` added on open or by Tools DB update). Shipped with the app; treat as read-mostly. The Tools tab can rebuild or incrementally refresh this catalog from a local Master Duel install via `CardCatalogUpdater` / `CardCatalogExtractor`:
  - **CARD_* TextAssets** (names/descriptions/ids/types): primarily under `LocalData/<playerId>/0000` (also scanned under StreamingAssets).
  - **Illustration bundles**: both `LocalData/<playerId>/0000` and `{install}/masterduel_Data/StreamingAssets/AssetBundle` (via `BundlePathResolver.GetStreamingAssetsRoot` / `GamePathLocator.ResolveInstallRoot`; LocalData wins when the same art id exists in both). Example StreamingAssets root: `D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\masterduel_Data\StreamingAssets\AssetBundle`.
  - **`card_type`**: from `CARD_Prop` type bytes (`CardPropTypeDecoder`), with description/type-line fallback (`CardTypeLabels`). Special summon faces (Synchro/Xyz/Link/Fusion/Ritual and pendulum variants) use bit masks that win over the generic Effect fallback. After fixing the decoder, refresh existing wrong labels with Tools → **Update DB** (no need to commit a new `database.db`).
  - **`created_at`**: filesystem creation time (UTC, ISO-8601) of the chosen illustration AssetBundle file (`File.GetCreationTimeUtc`).
  - **Update DB**: full scan + replace all master `card` rows. It takes a few seconds for ~41k files because `BundleSerializedFileReader` decodes only each bundle's serialized `CAB-…` entries (never the `.resS` texture payload) and the scan uses each bundle's embedded type tree, loading the class database only for files without one. Don't go back to `LoadBundleFile(unpackIfPacked: true)` plus a per-file class-database load in the scan: that unpacked ~14 GB, serialized every thread on a lock and took ~8 minutes.
  - **After game patch** (Tools): Update DB, then Restore overframes after patch, in that order (restore reads each card's current bundle from the DB).
- `user.db` holds writable state (`app_config`, per-card `favorite` / `has_backup` / modded text / over-frame / `art_id`). Default path is beside the exe (`AppContext.BaseDirectory/user.db`), same root as `backups/`. Do not commit `user.db`.
- `CardDatabase` opens master and `ATTACH`es `user.db`. On first open it migrates legacy user columns from a monolithic `database.db` into `user.db` (does not strip the shipped master file).
- `src/Floowan.Core/Resources/classdata.tpk` is required by AssetsTools.NET and is copied next to build output.
- Auto OF subject cutout is isnet-anime or SAM 3 + ViTMatte (`Sam3CutoutService`, see `docs/sam3-cutout.md`). Custom OF "Add more selection…" stays on SAM 2 (painted-region prompts) and refines each SAM 2 result's edge with ViTMatte (`Sam3CutoutService.RefineMaskEdge`; off with `FLOOWAN_SAM2_MATTE=0`). Core references `Microsoft.ML.OnnxRuntime.DirectML` on Windows and the plain `Microsoft.ML.OnnxRuntime` elsewhere. `Resources/sam3/prompt_features.bin` (embedded) is SAM 3-derived, so `licenses/SAM_LICENSE.txt` must ship with releases. Model weights download to `%LOCALAPPDATA%\Floowan\models\` and are never committed.
- **ImageSharp stays on 3.1.x.** 3.2.0+ fails to build without a Six Labors license key. 3.1.12 has known TIFF and ICC advisories, so decode image files only through `Floowan.Core.Imaging.SafeImage`. It registers only PNG, JPEG, BMP and WebP, skips metadata and drops ICC profiles. Never call `Image.Load` / `Image.Identify` directly in app code; tests may.
- The integration test (`CardArtBundleServiceIntegrationTests`) self-skips unless a real Master Duel install is present at a hardcoded Steam path, so it is a no-op on cloud VMs.

## Lint/format
`.editorconfig` sets UTF-8, CRLF, a final newline, trimmed trailing whitespace, and space indentation. No analyzers or `dotnet format` step are configured.
