# Project Working Instructions

These notes are intended for AI/code agents working in this repository.

## Editing Discipline

- Start each coding/documentation task with `git status --short --branch`.
- Treat a clean working tree as valuable. If the tree is dirty, identify whether changes are user-owned before editing nearby files.
- Preserve existing file encoding and line endings whenever possible.
- Do not rewrite whole Markdown, XAML, or C# files just to change one line.
- Prefer targeted patches over bulk rewrites.
- If a file contains non-ASCII text or older mojibake text, avoid broad automated replacements that may re-encode the entire file.
- For small documentation edits, use patch-based edits when the exact text is stable. If text contains encoding artifacts, replace the smallest possible block and verify the diff size afterward.
- After editing documentation, run `git diff --check`.
- Before finalizing, inspect `git diff --stat`; unexpectedly large diffs in documentation usually mean line endings or encoding were changed accidentally.

## Markdown Notes

- Keep project documentation in the style already used by the file.
- Prefer ASCII text unless the file already consistently uses local characters and the edit requires them.
- Avoid converting CRLF/LF across the whole file.
- Avoid changing generated or packaging files unless the task explicitly needs it.

## Workflow Notes

- For broad new features, first update or create a short project plan/checkpoint Markdown file, then implement in small low-risk steps.
- If the user says the current version is stable, avoid unrelated refactors and avoid changing generated RDL behavior outside the requested area.
- When resuming after Claude/user work, first inspect commits/status/diffs and summarize the current state before changing files.
- Prefer one focused change per turn when the user asks for “small steps” or “low risk”.
- Keep final summaries short: changed files, behavior change, verification command, and any known limitation.

## UI And Generation Hotspots

- Main UI is concentrated in `Dialogs/ReportSetupDialog.xaml` and `Dialogs/ReportSetupDialog.xaml.cs`; these files are high-conflict zones.
- RDL XML generation is concentrated in `Generation/RdlBuilder.cs`; verify generated XML behavior carefully after touching it.
- Persistent state flows through `Model/ReportModel.cs` and child config classes; every saved UI option needs both build-from-UI and apply-to-UI mapping.
- If a new option affects generated output, update the model, UI save/load, generator, JSON behavior, and docs together.

## C# Commenting Rule

- Every new C# method, and every materially changed existing C# method, must have an XML documentation comment with a short explanation of intent/behavior.
- Keep XML comments concise. Explain why the method exists or what contract it provides; do not restate obvious line-by-line implementation.

## Build Notes

- For compile-only validation without repackaging the VSIX, use:

```powershell
dotnet build .\sp2rdlGenExtension.csproj -p:CreateVsixContainer=false
```

- Full solution build may fail if the existing `.vsix` is locked by Visual Studio or Explorer preview.
- Do not treat a locked `.vsix` packaging error as a code compile failure. Re-run compile-only build before investigating code.

## Localization Notes

- Current localization workflow is table-based.
- `accessMode` and `translationProcedure` remain in JSON only for compatibility.
- Generated localization seed file name is `*.translations.sql`.
- Seed SQL uses `MERGE`; existing rows for the same `ReportId/LanguageId/Key` can be updated.
- `ReportId` comes from the configured report registry table via `Register report`.
- `Default label` on stored procedure columns is the default-language/fallback text for localization labels.
