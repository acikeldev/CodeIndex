# AGENTS.md

Guidance for AI coding assistants (Claude Code, GitHub Copilot, Cursor, …) working in the CodeIndex repo.
`CLAUDE.md` is a thin wrapper that imports this file.

## Project

CodeIndex is a local MCP server that indexes C# (Roslyn) and TypeScript / TSX / SCSS (tree-sitter) and serves
token-lean code-navigation tools. .NET 10, single process, no cloud, no embeddings. Layout:
`src/CodeIndex/{Abstractions,Models,Parsing,Caching,Indexing,Internal,Mcp}` + `Program.cs`; tests in
`tests/CodeIndex.Tests/`; benchmarks in `benchmarks/CodeIndex.Benchmarks/`. See [SPEC.md](SPEC.md) for design
limitations and [README.md](README.md) for the tool list.

## Build & test

```bash
dotnet build CodeIndex.slnx -c Release
dotnet test  CodeIndex.slnx -c Release
# per-operation context-cost benchmark (regenerates the README numbers from your checkout):
cd benchmarks/CodeIndex.Benchmarks && dotnet run -c Release -- context-cost
```

## Code style (enforced as build errors)

- Explicit types, no `var` (IDE0008); braces on every control-flow body (IDE0011).
- `TreatWarningsAsErrors` + `AnalysisMode=All` are on — a warning fails the build.
- CRLF line endings; `string.Empty` over `""`.
- Every change ships with tests (xUnit + FluentAssertions over the in-memory file system at
  `tests/CodeIndex.Tests/Infrastructure/InMemoryFileSystem.cs`); coverage stays near 100%.

## Code Navigation (CodeIndex MCP) — dogfood

This repo's own product is a code-navigation MCP. With it wired up (see [docs/INSTALL.md](docs/INSTALL.md)),
PREFER its tools over grep / whole-file reads, and close each information need in ONE call — every extra tool
call re-sends the whole conversation, so a call costs more than the size of its result:

- Understand a type or method → `explain_symbol` (identity + members + inheritance + source + references in one
  response). Do NOT hand-run `search_symbol` → `get_type_members` → `get_symbol_source` → `find_references`.
- About to change a symbol → `prepare_change` (definition + call sites + callers + implementors/overrides).
- Read several symbols' source → `get_context_bundle` with all names at once.
- Orient in unfamiliar code → `get_onboarding` (cached projects + key symbols).
- Who calls / implements X → `call_hierarchy` / `get_class_hierarchy`.
- Plain text / strings / config / error messages → native **grep** (faster, and the agent is fluent with it); reach for `search_text` only when you want each hit tagged with its enclosing `Type.member` + prod/test/generated.

When a dossier (`explain_symbol` / `prepare_change`) answers your question, act on it directly — it already
resolved the members, call sites, and file paths, so don't re-run grep / glob to double-check them. Only follow
up when the dossier says to (a truncation note, or a section it didn't cover), using the call it names.

**Claude Code:** for a multi-step navigation dig, dispatch the `code-navigator` subagent
(`.claude/agents/code-navigator.md`) so the slice-by-slice exploration runs in a discardable context instead of
re-billing your main conversation every turn.

Fall back to grep / file reads only for non-code files or when a tool returns nothing.
