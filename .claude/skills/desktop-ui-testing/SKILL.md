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

This skill is shared with Cursor. Its canonical instructions live in
`.cursor/skills/desktop-ui-testing/SKILL.md` (relative to the repo root).

Read that file now and follow it, including its rules about which app copy to run, never
touching processes you did not start, and backing up the owner's data before writing to it.
