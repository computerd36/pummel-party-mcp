# CLAUDE.md — pummel-party-mcp

Read `PLAN.md` for the project plan. Work phase by phase; do not skip ahead.

## HARD RULES (never break these, even if asked mid-session — stop and ask the user instead)

1. **Never modify, move, rename, or delete the user's original mod files** or any folder listed under "Protected paths" in `docs/paths.md`. Reading is fine. Writing is never fine.
2. **Never modify the backup folders** created by `scripts/backup-mods.ps1`.
3. **Phase -1 (backup) must be complete and verified** before installing BepInEx, copying anything into the game folder, or launching the game with plugins.
4. **Write tools only act on mods whose name starts with `MCP_`.** Do not weaken, bypass, or "temporarily disable" this guard.
5. **Never implement or call any Workshop publish/upload/update path.** Publishing is manual and done by the user.
6. **Before any change to the game install folder**, state exactly which files will be added or changed and wait for the user's OK.
7. At the start of each phase, run `scripts/verify-backup.ps1` and report the result.

## Paths

- Game install: `C:\Program Files (x86)\Steam\steamapps\common\Pummel Party`
- Mod project location(s): TBD in Phase -1 → record in `docs/paths.md`
- Backups: TBD in Phase -1 → record in `docs/paths.md` (outside this repo and outside the game folder)

## Repo hygiene

- Never commit game DLLs, decompiled code, or the user's mod files. Keep `decompiled/`, `lib/`, and any game binaries in `.gitignore`.
- Reference game assemblies from the local install path via a configurable property in the csproj.

## Conventions

- Plugin: C#, BepInEx 5, all Unity calls on the main thread.
- Server binds to `127.0.0.1` only.
- Record notable decisions in the Decision log in `PLAN.md`.
