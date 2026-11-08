---
name: desktop-ui-testing
description: >-
  Runs and checks the Masterduel modding tool WPF app on Windows: builds a dev copy, drives it
  with UI Automation by process id (scripts/ui-test/floowan-ui.ps1), takes screenshots, tests on
  real game data without harming the owner's install, and updates the owner's pinned build. Use
  when the user asks to run, open, click through, test, verify or screenshot the app or a UI
  change, to test with their user.db, or to update the pinned / taskbar version.
---

# Run and test the desktop app

Windows only. The app is `Floowan.Desktop.exe` (title "Masterduel modding tool by AmidoriA").

## Which copy to run

| Copy | Path | Data | Use for |
|------|------|------|---------|
| **Dev build** | `<worktree>/src/Floowan.Desktop/bin/Release/net8.0-windows/` after `dotnet build Floowan.sln -c Release` | Its own `database.db` and `user.db` (no over-frame history) | Clicking through new UI |
| **Pinned build** (owner's taskbar shortcut) | `<main checkout>/src/Floowan.Desktop/bin/Release/net8.0-windows/` | The owner's real `user.db` (hundreds of over-frames) and `backups/` | Only when the owner asks to test with their data, or to update the pin |
| **Release zip** | `artifacts/MasterDuelModding-vX.Y.Z-win-x64.zip` | Clean | Smoke test after publishing |

All copies read and write the **real game install** (`LocalData/<player>/0000`). Anything that
writes bundles (Apply, Replace art, Restore, After game patch, gate tools) changes the owner's
game. Do not trigger those unless the task needs it, and then back up first (below).

## Launch and identify

- Launch from bash in the exe's folder: `(./Floowan.Desktop.exe > /dev/null 2>&1 &)`, or with
  PowerShell `Start-Process <exe> -WorkingDirectory <dir> -PassThru`.
- Several copies can run at once with the **same window title**. Always work by process id:
  `Get-Process Floowan.Desktop | Select-Object Id, Path, StartTime`. The owner may have the
  pinned build open; never close or click a process you did not start.
- A process holds its DLLs: close your copy before rebuilding into the same folder.

## Drive the UI with UI Automation (preferred)

`scripts/ui-test/floowan-ui.ps1` targets one process id, finds elements by `x:Name`
(= AutomationId), and works without screenshots or a granted app. Run it with Windows
PowerShell 5.1:

```powershell
$ui = "scripts/ui-test/floowan-ui.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $ui -ProcessId <pid> -Action tab -Id OverFrameTab
```

Actions: `windows`, `tab`, `text`, `settext`, `selectfirst`, `expand`, `invoke`, `combo`, `pick`,
`toggle`, `click`, `close`. `-Window "<title prefix>"` targets a dialog, for example
`"Custom overframe art"`. See the script header for an end-to-end example (open Custom OF for a
card, switch the cutout model, run Auto detect, read the status line).

Behaviour to know:

- `invoke` presses a button on a background runspace, because a button that opens a modal
  dialog would otherwise block the script.
- UIA `Select` on a RadioButton does **not** raise `Click` (Custom OF "Auto detect" runs on
  Click). Use `click`, which brings the window to the front and does a real mouse click at the
  element (physical pixels).
- `pick` expands a ComboBox and selects an item by its `x:Name`, which fires `SelectionChanged`.
- Status lines (`StatusText`, `ToolsUpdateDbStatusText`, ...) are the easiest way to read results.
- After an action that runs a model, wait (`-WaitMs`) before reading the status: isnet-anime
  takes a few seconds, SAM 3 about 10–30 s.

## Screenshots (computer-use)

Use computer-use only to look, or when UIA cannot do it.

- Grant the app by its **full exe path** in `request_access`; the display name
  "Floowan.desktop" is shared by every copy.
- Do not call `open_application` for this app: it launches a **new** copy of the first granted
  exe instead of focusing the running one.
- "The desktop shell is frontmost" blocks computer-use clicks. Use the helper's `click` action
  instead.
- The machine has two monitors (LG ULTRAGEAR is primary). If the app is not in a screenshot,
  `switch_display`. Physical screen is 2560×1440; screenshot frames are scaled.
- Approval dialogs need the owner at the PC. If `request_access` is denied while they are away,
  fall back to UIA plus a console harness, and say so.

## Test cards

- Pick cards with a real character that are **not** already over-framed in the game: Custom OF
  is disabled for over-framed cards without saved layers. **Effect Veiler** (id 21224) works.
- Avoid art with no subject, such as **"A Case for K9"**: SAM 3 correctly reports "did not find
  a character or monster", which looks like a failure.
- To check whether a card is over-framed, read `card_state` (`floowan_overframe`, `is_overframe`)
  in the pinned build's `user.db`.

## Test Core logic on real data without the UI

For long or risky flows (catalog scan, after-patch restore), write a small console project in
the session scratchpad that references `src/Floowan.Core/Floowan.Core.csproj`
(`net8.0-windows`) and call the same methods the button calls, for example
`CardCatalogUpdater.UpdateFromGame` then `OverFrameModService.RestoreOverframesAfterPatch`.
Point it at copies of `database.db` / `user.db` unless the owner asked to use the real ones.

## Before writing to the owner's data

1. Copy the pinned folder's `user.db` and `database.db` to the session scratchpad.
2. Copy the live `of_card_asset` gate bundle (id in `user.db` `app_config.of_card_asset_bundle`,
   path `LocalData/<player>/0000/<first 2 chars>/<id>`).
3. Snapshot game file sizes and times:
   `find . -type f -printf '%p %s %T@\n' | sort > before.txt` in `LocalData/<player>/0000`,
   then diff after the run to report exactly what changed.
4. Make sure Master Duel is not running (`tasklist | grep -i masterduel`).

## Update the owner's pinned build

The taskbar pin targets `<main checkout>/src/Floowan.Desktop/bin/Release/net8.0-windows/Floowan.Desktop.exe`.

1. Check the pinned app is closed (`Get-Process Floowan.Desktop` paths).
2. Back up `user.db` and `database.db` from that folder.
3. Build the wanted commit (usually the release tag, checked out in your worktree) into it:
   `dotnet build src/Floowan.Desktop/Floowan.Desktop.csproj -c Release -o "<main checkout>/src/Floowan.Desktop/bin/Release/net8.0-windows"`.
   `user.db` is not a build item, so it is kept; `database.db` is copied only when the repo copy
   is newer.
4. Check `(Get-Item <exe>).VersionInfo.ProductVersion`, start it once and confirm the window
   title, then close it.

Do not pull or reset the main checkout's branches to do this; build from your worktree.
