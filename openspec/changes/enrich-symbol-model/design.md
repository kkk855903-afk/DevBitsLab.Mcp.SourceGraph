## Context

`SymbolMapping.ToCoreKind` already extracts the kind. The `Symbol` record in `Core` and the `symbols` table in `Storage` carry `name`, `fqn`, `kind`, `file_id`, span, `signature`, `container_id` (unused). Modifier and accessibility data is one Roslyn property away; XML doc text is one method call away. The only real design choice is how to encode modifiers compactly so the FTS index stays cheap.

## Goals / Non-Goals

**Goals:**
- One round-trip from the indexer to the store per symbol, no extra queries.
- `search_symbols` over XML doc summary works without inflating FTS by orders of magnitude.
- No breaking change for tools that ignore the new fields (response shape stays stable, fields are additive).
- Hierarchical browsing (`list_members`) without recursive in-process traversal — push to SQL via the populated `container_id`.

**Non-Goals:**
- Indexing `<param>`, `<returns>`, `<remarks>` separately. v1 only stores the parsed `<summary>` text. (Future change can pivot if agents demand it.)
- Ranking by accessibility or modifiers. The data is queryable; ranking heuristics are deferred.
- Indexing locals, parameters, type parameters (still excluded by `IsIndexable`).

## Decisions

**1. Modifiers as a comma-joined token string, not a bitmap.**
A bitmap is 4 bytes but opaque to FTS and SQL `LIKE`. A token string `"public,static,async"` is tiny, sortable in any order, trivially queryable (`modifiers LIKE '%async%'`), and renders as-is in markdown. Order is canonical: `accessibility, static, async, virtual, abstract, sealed, override, extern, readonly, partial`. Empty when none apply.

**2. Accessibility as the integer enum value, not a string.**
Roslyn's `Accessibility` enum maps cleanly to a small int. Surfaced as `"public" / "internal" / …` in tool output; stored compactly. Allows fast `WHERE accessibility = ?` filtering.

**3. XML doc summary parsed, not stored as raw XML.**
Raw XML is verbose and cluttered with format tags. We parse `<summary>` (preserving inline `<see>` and `<paramref>` as plain text) into a single normalised string. Inheriteddoc (`<inheritdoc/>`) is resolved by walking up the symbol's overrides until a non-inherited summary is found. The result is what an agent would *read* from a `<summary>` block.

**4. Populate `container_id` during pass 1 in two phases.**
Pass-1a: insert all symbols, recording the canonical-key chain. Pass-1b: a single `UPDATE` per symbol setting `container_id` to its `ContainingSymbol`'s row id (looked up in `_symbolIdByKey`). Cheap (one lookup per symbol). Avoids forward-reference issues.

**5. FTS5 trigger surface gains `xml_summary`.**
The existing `symbols_fts` virtual table is rebuilt to include `xml_summary` alongside `name, fqn, signature`. This is part of the schema-v5 rebuild; no migration logic needed (existing DBs are dropped and rebuilt).

## Risks / Trade-offs

- **FTS bloat from XML summary.** Mitigated by storing only parsed summary text (not raw XML) and trigram tokenization (which compresses well). On a 500k-symbol monorepo we expect FTS index growth ~30 MB; acceptable on a developer machine.
- **`<inheritdoc/>` resolution failure modes.** If the inherited target is in an external assembly (no XML docs available), we leave the field NULL rather than synthesising. Tools render `_(no summary)_` in that case.
- **Accessibility for explicit interface implementations.** Roslyn reports `Private` even though they're effectively public via the interface. We accept Roslyn's view; if an agent wants "things callable through interface I", the implements-at-member edge (in a separate change) is the right primitive.
