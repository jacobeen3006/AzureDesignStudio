# Plan 02-03 Summary: Treat Warnings as Errors + Whitespace Normalisation

**Status:** complete — 2/2 tasks, each gated and committed per task.

## Commits

| Task | SHA | Subject |
|------|-----|---------|
| 1 | `82155c9` | build(02-03): treat warnings as errors (compiler warnings only) |
| 2 | `4cecbcb` | style(02-03): normalise whitespace under a minimal .editorconfig |

## Task 1 — Warnings as errors

- `Directory.Build.props` gained `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in the existing `PropertyGroup`. No `WarningsAsErrors`, `WarningsNotAsErrors`, `NoWarn`, `MSBuildTreatWarningsAsErrors`, `CodeAnalysisTreatWarningsAsErrors`, or `EnforceCodeStyleInBuild`.
- CS0169 probe: a one-file `net10.0` class library importing the repo props via `DirectoryBuildPropsPath` failed with `error CS0169` (rc=1).
- Clean Release rebuild (`--no-incremental -p:RunAOTCompilation=false`): rc=0, 0 CS/NU/IDE/CA/MSB warnings, PRI263 still present.
- Offline suite: Passed: 755, Failed: 0.

## Task 2 — .editorconfig + whitespace normalisation

`.editorconfig` contents:

```
root = true

[*.cs]
indent_style = space
indent_size = 4
tab_width = 4
trim_trailing_whitespace = true
insert_final_newline = true

# end_of_line is intentionally omitted: the index is LF for all files while this
# worktree checks out 519 of them as CRLF; pinning would rewrite hundreds of files.
# charset is intentionally omitted: 102 of 820 files carry a UTF-8 BOM.
```

- Observed normalised file count: 443 `.cs` files (+ `.editorconfig` = 444 paths), 722 insertions / 698 deletions.
- `dotnet format src/AzureDesignStudio.sln --verify-no-changes --no-restore`: exit 0.
- Release build: rc=0. Offline suite: Passed: 755, Failed: 0.

## Deviations

- 6 `.cs` files exhibit non-whitespace line splits under `git diff -w` (FunctionAppModel.cs, StorageAccountModel.cs, TopologyPositions.cs, BicepParamsTextTests.cs, ReconcileEngineTests.cs, TopMenu.razor.cs) — formatter split single-line initializers / removed a double BOM. Reported; not weakened. The remaining 437 `.cs` files are provably whitespace-only.