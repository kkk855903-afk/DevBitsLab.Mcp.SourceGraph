<!-- Thank you for contributing! Please fill in the sections below. -->

## Summary

<!-- One or two sentences. What changes and why. -->

## Type of change

<!-- Check all that apply. -->

- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (a fix or feature that would cause existing behaviour to change)
- [ ] New MCP tool (also tick "New feature")
- [ ] New plugin SDK surface (also tick "New feature")
- [ ] Documentation only
- [ ] CI / build / dependency change

## Related issues

<!-- "Fixes #123", "Closes #456", or "Related to #789". -->

## How was this tested?

<!-- Required. Describe what you ran. -->

- [ ] `dotnet build` clean (TreatWarningsAsErrors is on)
- [ ] `dotnet test` passes locally
- [ ] New tests added under `tests/DevBitsLab.Mcp.SourceGraph.Tests/`
- [ ] Manual smoke test against a real MCP client (Claude Code / Cursor)

## Checklist

- [ ] My code follows the style described in [CONTRIBUTING.md](../CONTRIBUTING.md)
- [ ] I have updated `CHANGELOG.md` under `## [Unreleased]`
- [ ] I have updated `README.md` if a CLI flag, MCP tool, or scope option changed
- [ ] If this is a breaking change to the `Sdk` package, I noted it in the PR title
- [ ] If this adds a new dependency, I called it out in the summary above

## Notes for reviewers

<!-- Anything reviewers should look at first. Performance numbers, screenshots,
trace excerpts, etc. -->
