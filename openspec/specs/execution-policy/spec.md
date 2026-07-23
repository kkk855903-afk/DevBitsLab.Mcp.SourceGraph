# Execution policy

## Purpose

Prevent repository-controlled project files, source generators, analyzers, and plugins from
gaining executable authority merely by being discovered. Executable pathways consume a common,
read-only, user-owned trust decision before any evaluation, restore, load, or invocation.

## Requirements

### Requirement: Every executable capability is denied by default

The execution policy SHALL model independent capabilities named `MsBuildEvaluation`,
`ProjectSourceGenerators`, `PluginLanguageIndexer`, `PluginAnalyzer`, and `PluginTool`.
An absent grant for the exact requested capability SHALL deny execution. A grant for one
capability SHALL NOT imply any other capability.

#### Scenario: MSBuild evaluation does not authorize source generators

- **WHEN** a repository grant contains only `MsBuildEvaluation`
- **THEN** evaluation of `MsBuildEvaluation` is allowed and evaluation of
  `ProjectSourceGenerators` is denied with a machine-readable reason

#### Scenario: Plugin contracts remain isolated

- **WHEN** a plugin grant contains only `PluginLanguageIndexer`
- **THEN** that same plugin remains denied for `PluginAnalyzer` and `PluginTool`

### Requirement: Trust records are external and user-owned

The default trust file SHALL be
`%LOCALAPPDATA%/MedInteropLens/trust-v1.json` on Windows. The evaluator SHALL reject a trust file
that is lexically or physically inside the repository being evaluated. Repository files,
`.sourcegraph.json`, and files below `.sourcegraph/` SHALL NOT add grants or redirect the
evaluator to a repository-owned trust record.

The trust-file path and every existing ancestor SHALL be free of symbolic links, junctions, and
other reparse points. Failure to establish either the repository's or trust file's physical path
SHALL deny with `trust-boundary-resolution-failed`; a trust path containing a reparse point SHALL
deny with `trust-store-contains-reparse-point`. Physical-boundary validation SHALL run before and
after reading the trust bytes. An existing trust file SHALL also be an ordinary, single-link file;
on Windows it SHALL have no alternate data streams.

#### Scenario: Repository forges a trust file

- **WHEN** an untrusted repository places a syntactically valid `trust-v1.json` below its
  `.sourcegraph` directory and names itself as trusted
- **THEN** the evaluator denies with `trust-store-inside-repository`

### Requirement: The trust format is versioned and strict

Trust schema version 1 SHALL use the following user-authored shape:

```json
{
  "schemaVersion": 1,
  "repositories": [
    {
      "path": "C:\\src\\medical-device",
      "capabilities": ["MsBuildEvaluation"]
    }
  ],
  "pathPlugins": [
    {
      "fingerprint": "sha256:<64 hexadecimal characters>",
      "capabilities": ["PluginLanguageIndexer"]
    }
  ],
  "nugetPlugins": [
    {
      "packageId": "Contoso.Medical.Plugin",
      "version": "1.2.3",
      "capabilities": ["PluginAnalyzer"]
    }
  ]
}
```

Unknown schema versions, unknown properties, unknown or misplaced capabilities, duplicate
capabilities, malformed subjects, malformed JSON, oversized input, and read failures SHALL fail
closed. Missing grant arrays MAY be treated as empty. The evaluator SHALL return a stable
machine-readable reason code for every allow or deny result.

Duplicate JSON property names at any object depth SHALL be malformed rather than last-value-wins.
Capability strings SHALL equal one declared enum name exactly; numeric strings, surrounding
whitespace, case aliases, and comma-composed enum values SHALL be rejected. Repository subjects
compare by normalized platform path, path-plugin fingerprints compare case-insensitively, and
NuGet subjects compare by case-insensitive package id plus exact version; a duplicate normalized
subject in any grant array SHALL make the whole document malformed.

#### Scenario: Malformed trust JSON

- **WHEN** the external trust file cannot be parsed
- **THEN** every executable capability is denied with `trust-file-malformed`

#### Scenario: Trust file cannot be read

- **WHEN** the external trust file is missing or unreadable
- **THEN** execution is denied with `trust-file-missing` or `trust-file-read-failed`
  respectively

### Requirement: Path plugins are authorized by complete bundle identity

A path-plugin fingerprint SHALL be SHA-256 over a versioned domain separator, the main assembly's
normalized repository-independent relative path, and every file beneath the bundle root. Each
file's normalized forward-slash relative path, byte length, and complete bytes SHALL be hashed in
ordinal relative-path order. The fingerprint SHALL therefore change when the main DLL, a managed
dependency, a native runtime asset, or any other bundle file is added, removed, renamed, or
modified.

The fingerprint walker SHALL NOT follow symbolic links, junctions, or other reparse points.
A reparse point in the bundle or in the bundle-root path SHALL fail closed, including links that
escape the bundle and links that form a cycle.

Fingerprinting is an inspection primitive, not an execution authorization for a mutable path.
The walker SHALL capture a complete manifest, validate every file immediately before and after
reading it, and capture the manifest again after hashing. Paths, attributes, file/directory kind,
length, and last-write metadata SHALL remain identical; additions, removals, replacements, or
metadata changes SHALL fail. Non-regular files SHALL be rejected. On Windows, multiple hard links
and alternate data streams SHALL also be rejected.

Even when the inspected fingerprint matches a user grant,
`EvaluatePathPluginCapability` SHALL deny with `path-plugin-snapshot-required` and return the
fingerprint. A future execution integration must copy the bundle into a host-controlled immutable
snapshot, hash that snapshot, and load from that same snapshot. `PluginHost` MUST NOT treat a hash
of the original mutable path as authority to load from that path.

Known limitation: there is no single portable managed API for link count or alternate streams.
The current primitive rejects Windows hard links and alternate data streams. On Linux, native
link-count inspection supports only the explicitly encoded glibc LP64 `struct stat` layouts for
x86_64 (144 bytes, 64-bit link count before mode) and AArch64 (128 bytes, mode before a 32-bit
link count); other Linux architectures fail closed. Layout decoding is covered by
platform-independent tests, but the native `lstat` calls still require validation in real Linux
x86_64 and AArch64 CI environments. Supported macOS ABIs use native link metadata; unknown Unix
ABIs fail closed. macOS resource forks and arbitrary extended attributes are not part of the
portable byte stream. This limitation is safe for execution because mutable path evaluation never
returns `IsAllowed=true`; a future snapshot implementation must define platform-specific identity
checks before enabling path-plugin execution.

#### Scenario: Dependency changes after approval

- **WHEN** a path plugin's main DLL is unchanged but one dependency byte changes
- **THEN** the recomputed fingerprint no longer matches the user grant and execution is denied
  with `path-plugin-not-trusted`

#### Scenario: Mutable path fingerprint matches a grant

- **WHEN** a path-plugin bundle fingerprint matches an external grant exactly
- **THEN** inspection returns that fingerprint but execution remains denied with
  `path-plugin-snapshot-required`; no assembly is loaded from the inspected mutable path

#### Scenario: Bundle contains an external junction

- **WHEN** a directory below the plugin bundle is a junction to a directory outside the bundle
- **THEN** fingerprinting stops without reading through the junction and returns
  `path-plugin-bundle-contains-reparse-point`

### Requirement: NuGet trust is exact and precedes restore

A NuGet plugin SHALL be eligible for restore only when the external trust record contains the
same package id and exact version plus the requested plugin capability. Package ids compare
case-insensitively; version strings compare exactly. Floating versions, ranges, and aliases SHALL
be invalid requests and SHALL NOT authorize restore.

The trust evaluator SHALL make this decision from package id, exact version, and the trust file
alone. It SHALL NOT contact a package source, consult restored package contents, or start restore
as part of evaluation.

#### Scenario: Exact NuGet grant

- **WHEN** the user grants `Contoso.Medical.Plugin` version `1.2.3` for `PluginTool`
- **THEN** package id `contoso.medical.plugin` version `1.2.3` is allowed for `PluginTool`
  before restore, while `1.2.4`, `1.2.3.0`, and `1.*` are denied

### Requirement: Trust evaluation is a pure read-only gate

Evaluation SHALL only normalize paths, read the external trust record, read path-plugin bundle
metadata and bytes when needed, and return a decision. It SHALL NOT write or create a trust
record, mutate repository configuration, access the network, restore packages, start processes,
or load managed/native plugin code.

#### Scenario: Repeated evaluation

- **WHEN** the same valid request is evaluated repeatedly
- **THEN** it returns the same decision while the trust file bytes and last-write timestamp remain
  unchanged
