@AGENTS.md

## Always-applied rules (shared with Cursor)

@.cursor/rules/utf8-no-windows-1252.mdc
@.cursor/rules/windows-line-endings.mdc

## Claude Code specifics

- Skills: `.claude/skills/build-and-publish-release` is a stub that loads `.cursor/skills/build-and-publish-release/SKILL.md`. Use it for any release, tag, publish or zip request. To add a rule or skill, follow "Agent configuration" in `AGENTS.md` so Cursor keeps working.
- The Write tool saves new files as UTF-8 without BOM and with LF endings. After creating a file, convert it to CRLF (for example `sed -i 's/$/\r/' <file>` on a file that has no CR yet), and add the BOM for new XAML. Edit keeps a file's existing encoding and endings.
- On Windows, the PowerShell tool is Windows PowerShell 5.1: no `&&`, and pass `-Encoding utf8` to `Set-Content` / `Out-File` (its default is the ANSI codepage, which breaks the UTF-8 rule).
- In a Linux cloud session, use the Linux build and test commands in `AGENTS.md`; the WPF project can't build there.
