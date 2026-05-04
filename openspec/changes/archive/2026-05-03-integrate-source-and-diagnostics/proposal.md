## Why

Roslyn already produces two streams of information that the graph currently throws away:

1. **Source-generated documents.** ASP.NET routing, MVVM Toolkit observable properties, regex source-gen, JSON source-gen — all produce real C# code that participates in the user's runtime. The agent asking *"where is the generated `OnPropertyChanged` for `MyVm.Title`?"* gets nothing today because we filter out `SourceCodeKind.Regular`-only and never emit symbols from generated docs.
2. **Diagnostic warnings.** Roslyn analyzers report warnings and errors that already describe code-quality issues, deprecations, and bugs. The agent asking *"what does this codebase warn about?"* or *"is this code being warned on?"* has no graph-side answer.

Both are "free" data — Roslyn has them, we just don't capture them.

## What Changes

- Indexer accepts source-generated documents in addition to regular ones, marking them `is_generated = true` on the file row.
- New table `diagnostics(symbol_id, file_id, severity, code, message, line, col)` captures every Roslyn diagnostic produced during compilation.
- New tools `list_generated_files` and `find_diagnostics(severity?, code?, symbol?)`.
- `find_definition`, `list_symbols_in_file`, etc. include a `(generated)` marker when relevant.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `indexing`: walks `Document.SourceCodeKind == Generated` alongside `Regular`; captures `compilation.GetDiagnostics()` per file.
- `storage`: new `is_generated` column on `files`, new `diagnostics` table.
- `mcp-tools`: two new tools, generated marker in existing tool output.

## Impact

- Schema bump.
- ~50 lines in the indexer (filter relaxation + diagnostic emit).
- Diagnostics table can be large on hot warning hours; we add `idx_diagnostics_severity` and `idx_diagnostics_code` for fast filtering.
- Generated docs roughly double symbol count in MVVM-heavy codebases. Worth surfacing via the `(generated)` marker so agents understand what they're seeing.
