## ADDED Requirements

### Requirement: find_by_attribute tool
The server SHALL expose a `find_by_attribute` tool that returns symbols matching an attribute name and optional argument substring.

#### Scenario: Find every POST endpoint
- **WHEN** the agent invokes `find_by_attribute(name = "HttpPost")`
- **THEN** the response lists every symbol carrying `[HttpPost]` regardless of arguments, with location and one-line summary

#### Scenario: Find a specific route
- **WHEN** the agent invokes `find_by_attribute(name = "HttpGet", argValue = "/api/v2/users")`
- **THEN** the response is restricted to `[HttpGet]`-attributed symbols whose argument text matches `/api/v2/users` via trigram FTS

### Requirement: Attributes surfaced in existing tool output
`find_definition`, `list_symbols_in_file`, `neighborhood`, and `module_summary` SHALL include an `attributes:` line per result that lists each attached attribute's name (with truncated arg preview when present), so an agent reads `[HttpGet("/api/users"), Authorize]` without a second call.

#### Scenario: Attributed method in find_definition output
- **WHEN** `find_definition` returns a method that carries `[HttpGet("/api/users")]` and `[Authorize]`
- **THEN** the markdown for that result includes a line like `attributes: [HttpGet("/api/users"), Authorize]`
