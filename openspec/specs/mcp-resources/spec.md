# MCP Resources

## Purpose

Surface the code graph to MCP clients as browsable resources (URIs that
return markdown content), so hosts can render `@graph://...` mentions and
agents can pull snapshots without spending a tool call.

## Requirements

### Requirement: Symbol detail resource
The server SHALL expose a `graph://symbol/{symbolId}` resource that returns
a markdown card describing one symbol: kind, definition location, signature,
and up to 10 callers and 10 callees.

#### Scenario: Read a known symbol id
- **WHEN** an MCP client reads `graph://symbol/42` against a graph that has
  a symbol with `id = 42`
- **THEN** the response is `text/markdown` content beginning with the
  symbol's FQN, followed by Kind, Defined-in, Signature, then "## Callers"
  and "## Callees" sections (each with the corresponding edge target list,
  or `_(none)_` when empty)

#### Scenario: Read an unknown symbol id
- **WHEN** the requested `symbolId` is not a positive integer or doesn't
  match any row
- **THEN** the response is `# Invalid symbol id: <input>` or
  `# Symbol id <id> not found`

### Requirement: File outline resource
The server SHALL expose a `graph://file/{path}` resource that returns a
markdown outline of every indexed symbol in a file (URL-decoded path).

#### Scenario: Read an indexed file
- **WHEN** an MCP client reads `graph://file/<url-encoded-path>`
- **THEN** the response is markdown that begins with the file's absolute
  path as `# {path}`, then lists namespace and type declarations as `##` /
  `###` sections and members as bullet items with line numbers and
  signatures

### Requirement: Namespace summary resource
The server SHALL expose a `graph://namespace/{name}` resource that lists the
top symbols in a namespace ranked by inbound call count, up to 50 entries.

#### Scenario: Read a namespace summary
- **WHEN** an MCP client reads `graph://namespace/Sample.Domain`
- **THEN** the response is markdown showing each symbol whose FQN equals or
  starts with `Sample.Domain.`, ordered by in-degree, with file:line refs
