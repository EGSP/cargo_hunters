# Cargo Hunters mod development

This repository contains a BepInEx IL2CPP mod and local development tools for Cargo Hunters (Steam App ID `4197990`).

## Local setup

Machine-specific paths, game launch authorization, save locations, and local bridge details belong in the ignored `AGENTS.local.md`. Do not copy its private values into tracked files or reports.

Pass `-GameDirectory` to the PowerShell tools or set the MSBuild `GameDirectory` property to the game installation directory. The game must be closed when building and deploying with `tools/Build-Deploy.ps1`.

## Development workflow

- Read `docs/dev-bridge.md` for the local HTTP bridge routes and their limits.
- Use `/health`, `/scenes`, and `/objects?depth=0` to establish the current game state before inspecting it.
- For UI work, use `/ui/tree` or `tools/Get-GameUi.ps1`; after a click, fetch a fresh tree. Use only object IDs from the current process.
- Treat `/invoke` and property getters as potentially state-changing. Inspect method purpose first; after a timeout, check game state before retrying.
- Run `tools/Test-GameBridge.ps1` and `tools/Test-GameUi.ps1` for bridge/UI checks. Do not choose “New Game” during testing unless the task specifically requires it.
- Inventory export reads game item containers and writes JSON. It does not sort or mutate inventory.

## Repository hygiene

Save backups, game captures, generated output, downloaded BepInEx binaries, and `AGENTS.local.md` are local-only and excluded by `.gitignore`.
