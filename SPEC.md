# CodeIndex MCP — Specification

## Problem

AI coding assistants need to navigate large codebases, but the naive options are expensive or weak:

- **Reading files directly** — millions of tokens to load a big repo; slow and costly per question.
- **grep** — slow on large trees, returns noisy raw text, no structure awareness.
- **Generic indexers** — JSON-heavy responses, shallow language understanding, file-count caps.

## Solution

CodeIndex is a **Roslyn + tree-sitter** MCP server. It parses the repository once, holds an **immutable in-memory
index**, and serves **18 token-lean tools** over stdio/JSON-RPC. It indexes **C#** and **TypeScript / TSX / SCSS**,
runs **fully locally** (no cloud, no embeddings, no network), and ships as a .NET global tool that a repo
`.mcp.json` starts automatically.

## Design principles

1. **Token efficiency over feature count** — compact, ranked, grouped output; never JSON dumps.
2. **Speed through simplicity** — immutable in-memory snapshots, syntax-only parsing, lock-free reads.
3. **Correct language understanding** — Roslyn for C#, tree-sitter for TS/SCSS; never regex-guessing.
4. **Never block the client** — cache-first non-blocking startup; heavy work happens in the background.
5. **Local & dependency-light** — no assembly restore, no embeddings, no external services.
6. **LLM/tool independent** — MCP is an open standard (Claude Code, Cursor, Copilot, …).
7. **Repo-agnostic & configurable** — repo root auto-detected; behavior via `codeindex.json`; nothing hardcoded.
8. **Abstracted & testable** — all disk access flows through one `IFileSystem`, so the whole engine is
   deterministically exercised by an in-memory file system, no real disk required.

## Architecture

```
AI agent  ←── stdio / JSON-RPC ──→  CodeIndex server (.NET 10)
                                     ├── 18 MCP tools (Mcp/)
                                     ├── Immutable snapshot  =  C# segment  +  TS/SCSS segment
                                     ├── Roslyn parser (C#)   +  tree-sitter parser (TS/TSX/SCSS)
                                     ├── MessagePack caches (one per segment, independent versions)
                                     ├── IFileSystem abstraction (production: real disk; tests: in-memory)
                                     └── File watcher (keeps the index live)
```

- **Non-blocking startup.** On launch the server loads the on-disk cache and starts serving instantly; a
  background watcher revalidates against the working tree. A cold start with no cache builds once. The TS/SCSS
  segment is built in the background *after* C# is already serving.
- **Snapshot-swap concurrency.** The index is an immutable `IndexSnapshot`. A rebuild happens entirely off to the
  side, then the new snapshot is published with a single atomic `Volatile.Write`; readers take no locks and never
  observe a torn state. One gate serializes rebuilds; the TS rebuild runs its multi-second parse off that gate.
- **Two segments, one snapshot.** C# (Roslyn) and TS/SCSS (tree-sitter) are indexed independently and composed
  into a single queryable snapshot. The C# segment is the delta baseline; a TS rebuild can never drop the C# side
  and vice-versa.
- **Incremental delta.** Per-file modified-time comparison reparses only changed/added files; a branch switch is
  handled as an ordinary delta.
- **Derived graphs (lazy).** A project-dependency graph (C#) and a symbol-mention graph (for `repo_map`'s
  PageRank) are computed lazily from the snapshot and cached until the files change — never serialized, so they
  add no cache-schema cost.

## Parsing pipeline

```
C#:   .sln/.slnx → referenced .csproj → .cs files → Roslyn SyntaxTree
        → namespaces, types, members (all visibilities), signatures, line ranges, inheritance, usings
TS:   tsconfig.json / package.json boundaries → .ts/.tsx/.scss → tree-sitter
        → classes/interfaces/enums/functions/consts + members; SCSS selectors, mixins, $vars, %placeholders
        → merged into the in-memory index → MessagePack binary caches on disk
```

Syntax-only: no assembly resolution, no NuGet restore, no `Compilation` object. Every disk read goes through
`IFileSystem`; a tree-sitter `Node` never escapes its `using Tree` scope (native use-after-free safety).

## Configuration

`codeindex.json` at the repo root (all keys optional): `cacheDir` (`repo` / `user` / explicit path), `exclude`
(extra prune dirs), `generatedGlobs` (generated-file markers), `looseProjects` (index non-solution `.csproj`),
`indexTypeScript` (TS/SCSS on/off). Repo root resolves via `--root` → `CODEINDEX_ROOT` env → nearest `.git`.

## Caching

- C# segment: `{cacheDir}/index.v{N}.cache`; TS/SCSS segment: `index.ts.v{N}.cache`.
- MessagePack binary; per-file timestamps drive the delta rebuild.
- The two segments have **independent** schema versions — bumping one never invalidates the other. Writes are
  atomic (temp file + rename); stale-cache cleanup is segment-scoped (never reaps the other segment's live cache).

## Distribution

- Packaged as a .NET global tool (`PackAsTool=true`); the `benchmarks/` and `tests/` projects are separate and
  excluded from the pack.
- Installed via `dotnet tool install -g CodeIndex`; a repo `.mcp.json` points to the PATH-resolved `codeindex`
  command so MCP clients start it automatically.

## Limitations

- **Syntax-only, name-based resolution.** `find_references` and `call_hierarchy` match by name (word-boundary),
  with no overload/receiver-type resolution; there is no compiler-accurate go-to-definition. This is a deliberate
  trade for fast, local, restore-free startup.
- **Language coverage is not uniform.** `get_class_hierarchy`, `resolve_bare_name`, and `get_project_dependencies`
  are C#-only; `search_structural` and `call_hierarchy` have separate C# and TS engines; `check_dangling_references`
  is TS-only. SCSS is indexed for symbol/text search only.
- **No embeddings / natural-language search** — by design (keeps it local, fast, and dependency-light).

## Testing

The `IFileSystem` seam makes the engine deterministically testable: scanners, parsers, caches, the snapshot-swap
store, the query analyzers, and every MCP tool run against an in-memory file system in the unit suite — no real
disk, no flakiness. The only concrete-filesystem component is the live `FileSystemWatcher`, whose pure event-
classification decisions are unit-tested while the OS plumbing is excluded from coverage.

## Dependencies

| Package | Purpose |
|---------|---------|
| ModelContextProtocol | Official C# MCP SDK |
| Microsoft.Extensions.Hosting | DI and host lifecycle |
| Microsoft.CodeAnalysis.CSharp | Roslyn — C# syntax parsing |
| TreeSitter.DotNet | tree-sitter — TS/TSX/SCSS parsing (bundled native grammars) |
| MessagePack | Binary cache serialization |
