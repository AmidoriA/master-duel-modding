# AGENTS.md

## Cursor Cloud specific instructions

Floowan is a **Windows-only .NET 8 WPF desktop app** (`net8.0-windows`, `UseWPF`) for replacing Yu-Gi-Oh! Master Duel card art. The Cursor Cloud VM is **Linux**, which affects what can run here.

### Projects
- `src/Floowan.Core` — non-UI class library (SQLite DB, path discovery, image prep, Unity AssetBundle I/O). Builds and runs on Linux.
- `src/Floowan.Desktop` — WPF GUI (`UseWPF=true`). **Cannot build or run on Linux** (requires the Windows Desktop targeting pack; fails with `NETSDK1100`). Build/run this only on Windows.
- `tests/Floowan.Core.Tests` — xUnit tests for Core. Builds and runs on Linux.

### Toolchain
- .NET 8 SDK is installed at `~/.dotnet` (added to `PATH`/`DOTNET_ROOT` in `~/.bashrc`). If `dotnet` is not found, run `export PATH="$HOME/.dotnet:$PATH"`.
- Because the target framework is `net8.0-windows`, Linux builds/restores **must** pass `-p:EnableWindowsTargeting=true`. Do not build `Floowan.sln` directly on Linux (it includes the WPF project and fails); target the Core/Tests projects instead.

### Build & test (Linux)
```bash
dotnet build tests/Floowan.Core.Tests/Floowan.Core.Tests.csproj -c Release -p:EnableWindowsTargeting=true
dotnet test  tests/Floowan.Core.Tests/Floowan.Core.Tests.csproj -c Release -p:EnableWindowsTargeting=true
```

### Known test behavior on Linux
3 tests fail on Linux and this is expected (not an environment problem): `GamePathLocatorTests.ResolveInstallRoot_WalksUpFromLocalData`, `GamePathLocatorTests.ResolveUnity3dPath_PointsAtMasterduelData`, and `BundlePathResolverTests.StreamingAssetsPath_UsesInstallRoot`. They assert against hardcoded Windows paths (`C:\...`, `D:\...`) with backslash separators, which `Path`/`Directory.GetParent` only interpret correctly on Windows. The other 10 tests pass. Run these tests on Windows for a full green suite.

### Data / resources
- `database.db` (repo root, ~5.6 MB, ~14k cards) is the **master** SQLite card catalog (identity fields: `id`, `name`, `description`, `bundle`, `data_index`, plus optional `card_type` / `created_at` added on open or by Tools → Update card database). Shipped with the app; treat as read-mostly. The Tools tab can rebuild this catalog from a local Master Duel install (CARD_* TextAssets + illust bundles) via `CardCatalogUpdater` / `CardCatalogExtractor`.
  - **Primary scan roots** (resolved from the Home LocalData player folder via `GamePathLocator.ResolveInstallRoot`, not a hardcoded drive): `LocalData/<playerid>/0000` (fresher player downloads preferred) and `{installRoot}\masterduel_Data\StreamingAssets\AssetBundle` (e.g. `D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\masterduel_Data\StreamingAssets\AssetBundle`).
  - `created_at` is the filesystem creation time (UTC, ISO-8601) of each card’s **illustration** AssetBundle file (LocalData preferred over StreamingAssets when both exist).
  - `card_type` is inferred from CARD_Desc type lines via the same heuristics as over-frame frame selection (`CardTypeLabels` / `CardFrameTemplates.InferStyle`).
- `user.db` holds writable state (`app_config`, per-card `favorite` / `has_backup` / modded text / over-frame / `art_id`). Default path is beside the exe (`AppContext.BaseDirectory/user.db`), same root as `backups/`. Do not commit `user.db`.
- `CardDatabase` opens master and `ATTACH`es `user.db`. On first open it migrates legacy user columns from a monolithic `database.db` into `user.db` (does not strip the shipped master file).
- `src/Floowan.Core/Resources/classdata.tpk` is required by AssetsTools.NET and is copied next to build output.
- The integration test (`CardArtBundleServiceIntegrationTests`) self-skips unless a real Master Duel install is present at a hardcoded Steam path, so it is a no-op here.

### Running / demoing core logic on Linux
The WPF GUI can't run here. To exercise core logic end-to-end, reference `src/Floowan.Core/Floowan.Core.csproj` from a small console project (`net8.0-windows` + `EnableWindowsTargeting=true`) and call `CardDatabase`, `ImagePreparation`, and `ClassDataLocator` against the bundled `database.db`.

### Lint/format
No linter/formatter is configured (no `.editorconfig`, no analyzers, no `dotnet format` step in the repo).
