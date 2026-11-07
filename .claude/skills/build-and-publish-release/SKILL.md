---
name: build-and-publish-release
description: >-
  Builds and publishes Masterduel modding tool by AmidoriA Windows releases
  (single-file self-contained win-x64 zip, annotated git tag, GitHub release).
  Use when the user asks to release, publish, tag, cut a version, ship a
  single-file exe, upload a GitHub release, or rebuild MasterDuelModding-vX.Y.Z-win-x64.zip.
---

# Build and publish release

This skill is shared with Cursor. Its canonical instructions live in
`.cursor/skills/build-and-publish-release/SKILL.md` (relative to the repo root).

Read that file now and follow it exactly, including its checklist and its rules:
build and zip only by default, and never tag, upload or force-push unless the user
explicitly asks.
