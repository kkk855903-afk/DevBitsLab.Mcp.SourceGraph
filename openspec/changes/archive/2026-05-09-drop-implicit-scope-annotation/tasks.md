## 1. Implementation

- [x] 1.1 In [src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs](src/DevBitsLab.Mcp.SourceGraph.Server/Scoping/ScopedExecution.cs) collapse the implicit-scope branch in the single-host short-circuit so the tool body returns unchanged. Leave `ScopeResolution.IsImplicit` untouched on the record.
- [x] 1.2 No new test required — no existing test asserts on the `_(scope: …)_` annotation (verified by `grep -rn '_(scope:\|implicit' tests/`). Existing scope-routing tests cover the rest of the requirement; the surrounding multi-scope and per-row-tag behaviours are unchanged.
- [x] 1.3 Run `dotnet build` and `dotnet test` to confirm the suite stays green.

## 2. Config hygiene (rides along)

- [x] 2.1 Rename the `.mcp.json` registration key from `"sourcegraph"` to `"SourceGraph"` (PascalCase brand spelling). Add a matching `"name": "SourceGraph"` field for clients that read it.
- [x] 2.2 Drop the `${workspaceFolder}/` placeholders from the `args` paths — the extension launches with `cwd` = repo root, so relative paths work and remove a placeholder-expansion dependency.

## 3. Verification

- [x] 3.1 Drive a real `initialize` + `tools/call` JSON-RPC roundtrip against `tests/fixtures/Sample.sln`; confirm the `find_definition` response leads with the leaf followed by substantive content (`🌿 N hits for 'X':`) and that no `_(scope:` substring appears anywhere in the body.
- [x] 3.2 Eyeball in the Claude VS Code extension after restart — leaf adjacent to actual prose, no italic chrome competing.
- [x] 3.3 Run `openspec validate drop-implicit-scope-annotation --strict`.
