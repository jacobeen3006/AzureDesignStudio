# Phase 4: Database Durability - Discussion Log

> Audit trail only. Not read by downstream agents.

**Date:** 2026-11-14

## Backup UX
Options: menu action with file picker / automatic rolling backups plus restore list / both. **Chosen: both manual and automatic.**

## Corruption at startup
Options: stop on a recovery screen / open read-only with a banner / quarantine and start fresh automatically. **Chosen: quarantine and start fresh automatically.**

## Unique-index violations
Options: back up, dedupe keep-newest, report (recommended) / back up, report, stop / skip the index. **Chosen: back up, dedupe keep-newest, write a report.**

## Server rollback
Options: pre-upgrade backup restore rehearsed in CI (recommended) / EF Down migrations. **Chosen: pre-upgrade backup restore.**

## Decided autonomously
[auto] [Backups] - Q: "retention and location" -> Selected: "backups subfolder, newest 5 plus pre-migration backups of the last 30 days" (reversible technical - constant)
[auto] [Backups] - Q: "backup file naming" -> Selected: "workspace-<UTC>-<reason>.db" (reversible technical - internal naming)
[auto] [Quarantine] - Q: "quarantine file naming" -> Selected: "workspace.corrupt-<UTC>.db" (reversible technical - rename)
[auto] [Migrations] - Q: "where migration classes live" -> Selected: "Workspace project and Services project with design-time factories" (reversible technical - file move)
[auto] [Testing] - Q: "concurrency and corruption test approach" -> Selected: "xUnit on temp SQLite files; SQL Server parity in a container job" (reversible technical - test-local)
