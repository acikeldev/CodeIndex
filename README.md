# CodeIndex MCP

A fast, **local** code-intelligence server that speaks the **Model Context Protocol (MCP)**. It indexes **C#**
(via Roslyn) and **TypeScript / TSX / SCSS** (via tree-sitter) in a single process and serves **18 token-lean
tools** for AI-assisted code navigation, search, and analysis. Works with any MCP client (Claude Code, Cursor,
Copilot, …). No cloud, no embeddings — everything runs on your machine.

## Install

```bash
dotnet tool install -g CodeIndex
```

Point your MCP client at the `codeindex` command (typically via an `.mcp.json` in your repo). The server
auto-detects the repo root from the nearest `.git`; override with `--root <path>` or the `CODEINDEX_ROOT`
environment variable.

Update with `dotnet tool update -g CodeIndex`.

## Configuration (optional)

Drop a `codeindex.json` at the repo root. Every key is optional; a missing file uses the defaults.

| Key | Default | Purpose |
|-----|---------|---------|
| `cacheDir` | `repo` | Cache location: `repo` (`{repo}/.codeindex`), `user` (`%LOCALAPPDATA%`, keeps the working tree clean & survives read-only checkouts), or an explicit path |
| `exclude` | — | Extra directory names to prune, on top of the built-ins (`node_modules`, `bin`, `obj`, `.git`, …) |
| `generatedGlobs` | built-ins | Globs marking generated files (demoted, not hidden, in results) |
| `looseProjects` | `false` | Also index `.csproj` not referenced by any solution (for solution-less repos) |
| `indexTypeScript` | `true` | Index TS/TSX/SCSS; set `false` for a C#-only index |

Nothing is hardcoded — the repo root, cache location, excludes, and language coverage are all resolved
dynamically or from this file.

## Tools (18)

**Orientation**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `suggest_queries` | C# + TS | Index overview (top projects, largest files, type distribution) + ready-to-run starting queries |
| `repo_map` | C# + TS | The most important symbols, ranked by PageRank over the symbol-reference graph; pass `focus=` for task-relevant (personalized) ranking |
| `index_stats` | — | Health/diagnostics: counts, last build kind/age, cache path + schema version |
| `list_projects` | C# + TS | All indexed projects with file counts |
| `list_files` | C# + TS | Source files in a project |

**Search**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `search_symbol` | C# + TS | Find types/members by name; kind & project filters, token budget |
| `search_text` | all | Full-text / regex search, grouped by file, generated files demoted |
| `find_references` | C# + TS | Lines referencing a symbol (word-boundary, skips comments) — spans C# **and** TS |
| `search_structural` | C# + TS | Find by **AST shape**: C# smells (`empty-catch`, `async-void`, `blocking-async`, …) and TS house rules (`inline-style`, `ts-ignore`, `default-export`, `any-type`, …) |

**Inspect & navigate**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `get_file_outline` | C# + TS | All types and members of a file |
| `get_type_members` | C# + TS | Members of a type, with optional kind filter |
| `get_class_hierarchy` | C# | Base types (up) and derived types / implementors (down) |
| `call_hierarchy` | C# + TS | Callers (each labelled by its enclosing member) / callees; `language=both` spans C# and TS in one call |

**Read**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `get_symbol_source` | all | Read specific source lines by file + range (restricted to indexed/in-repo files) |
| `get_context_bundle` | all | Batch-read several symbols' source in one call |

**Structure & resolution**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `get_project_dependencies` | C# | What a project references and what references it (`.csproj` graph, exact-name) |
| `resolve_bare_name` | C# | What an unqualified type name binds to in a given file (using its usings + aliases), incl. ambiguities |
| `check_dangling_references` | TS | Unused imports + used-but-not-imported project symbols — catches the post-merge build break where an import was dropped |

## How it works

- **Non-blocking startup.** The server publishes a snapshot straight from the on-disk cache and answers
  immediately; a background watcher revalidates against the working tree. A cold start with no cache builds once.
  TS/SCSS is built in the background **after** C# is already serving — zero added startup latency.
- **Lock-free reads.** The index is an immutable snapshot swapped in atomically. Readers never block on a rebuild
  and never see a half-updated index.
- **Incremental.** A file edit or a branch switch reparses only the files that changed (per-file modified-time
  delta) — typically sub-second.
- **Two segments, one index.** C# (Roslyn) and TS/SCSS (tree-sitter) are parsed independently and composed into a
  single queryable snapshot; each has its own cache with an independent schema version.
- **Syntax-only.** No assembly resolution, no `Compilation`, no NuGet restore — that's what keeps startup fast.
  Cross-file/overload resolution is therefore name-based (see [SPEC.md](SPEC.md) Limitations).
- **Token-lean output.** Ranked results, grouping by file, per-file caps, true totals, and generated-file
  demotion keep responses small.
- **Live.** A file watcher keeps the index fresh without restarting the server.

## Architecture note

All disk access flows through a single `IFileSystem` abstraction, so the scanners, parsers, caches, the
snapshot-swap store, and every tool are exercised end-to-end by an in-memory file system in the test suite — the
engine is deterministically testable without touching a real disk. The file watcher (inherently OS-bound) is the
only concrete-filesystem component.

## Project structure

```
CodeIndex/
├── src/CodeIndex/
│   ├── Program.cs        Entry point: repo-root resolution + config load + non-blocking startup + DI
│   ├── Abstractions/     IFileSystem + store/cache interfaces (the testability seam)
│   ├── Models/           MessagePack-annotated data models
│   ├── Parsing/          Roslyn (C#) + tree-sitter (TS/TSX/SCSS) parsers and workspace scanners
│   ├── Caching/          MessagePack caches — C# segment + independent TS segment
│   ├── Indexing/         Snapshot-swap store, C#+TS merge, dependency & mention graphs, file watcher
│   ├── Internal/         Ranking, output-shaping, structural search, call hierarchy, config, path security
│   └── Mcp/              The 18 MCP tool implementations
├── tests/CodeIndex.Tests/       xUnit unit tests over an in-memory file system
└── benchmarks/CodeIndex.Benchmarks/   BenchmarkDotNet startup / parse / cache / search benchmarks
```

## Dependencies

| Package | Purpose |
|---------|---------|
| ModelContextProtocol | Official C# MCP SDK |
| Microsoft.Extensions.Hosting | DI and host lifecycle |
| Microsoft.CodeAnalysis.CSharp | Roslyn — C# syntax parsing |
| TreeSitter.DotNet | tree-sitter — TS/TSX/SCSS parsing (bundled native grammars) |
| MessagePack | Binary cache serialization |

## License

MIT — see [LICENSE](LICENSE).
