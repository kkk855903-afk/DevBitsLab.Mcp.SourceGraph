## MODIFIED Requirements

### Requirement: Cold index of a solution
The indexer SHALL dispatch each indexable document to a registered `ILanguageIndexer` matching the document's file extension; the built-in `RoslynLanguageIndexer` is registered automatically for `.cs`.

#### Scenario: Index a fresh solution end-to-end
- **WHEN** `sourcegraph-mcp index <solution>` is invoked against a solution whose graph DB is empty or absent
- **THEN** every regular `.cs` document with `File.Exists(path) == true` is dispatched to `RoslynLanguageIndexer`, plus any document whose extension matches a third-party `ILanguageIndexer`; an `IndexResult` is returned with the per-language file counts merged

#### Scenario: Document with no matching language indexer
- **WHEN** the workspace contains a file whose extension has no registered `ILanguageIndexer`
- **THEN** the file is skipped with a debug log and no error
