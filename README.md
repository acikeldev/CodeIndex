# CodeIndex MCP

**Stop paying your LLM to read whole files.** CodeIndex is a fast, **local** code-intelligence server for the
**Model Context Protocol (MCP)** that answers "where is X / what's in Y / who calls Z" with resolved, token-lean
results — so your coding agent spends its context window (and your pay-as-you-go budget) on *thinking*, not on
grepping and re-reading source.

It indexes **C#** (Roslyn) and **TypeScript / TSX / SCSS** (tree-sitter) in one process and serves **20 tools** to
any MCP client (Claude Code, Cursor, Copilot, …). No cloud, no embeddings, no API keys — everything runs on your
machine.

→ **[Install & configure](docs/INSTALL.md)** · [The 20 tools](#the-20-tools) · [How it works](#how-it-works)

---

## Why it exists: grep + read is a token tax

Give an agent only `grep` and "read file" and it does the obvious thing — greps a name, then reads whole files to
see structure, find a definition, or understand a type. Every one of those file reads lands in the context window
**in full**. On a pay-as-you-go plan you pay input-token rates for all of it, every turn it's resent, on every
task.

CodeIndex removes the tax. It parses your repo once and keeps a live index, so the agent asks a precise question
and gets a precise, pre-shaped answer:

| Without CodeIndex | With CodeIndex |
|---|---|
| grep a type name → read the whole 1,000-line file to list its members | `get_type_members` → just the signatures |
| open 6+ files to understand an unfamiliar repo | `repo_map` → the important symbols, PageRank-ranked, in one call |
| grep `catch` and read each block to find empty ones | `search_structural empty-catch` → only the real matches |
| grep an interface name, scan every usage to find implementors | `get_class_hierarchy` → the implementors, directly |

Same answers. A fraction of the tokens.

## The numbers

A reproducible benchmark ([`benchmarks/CodeIndex.Benchmarks/ContextCost`](benchmarks/CodeIndex.Benchmarks/ContextCost))
runs a fixed suite of realistic code-intelligence questions **against CodeIndex's own repository**, two ways:

- **Baseline** — ripgrep + file reads, i.e. what a competent agent does *without* CodeIndex.
- **CodeIndex** — the actual MCP tool output (the real tool code path, called in-process).

Both are measured in the same unit: **tokens ingested into the context window** (`ceil(chars / 4)`, applied
identically to each side). The baseline is charged conservatively — raw file content, no line-number prefixes —
so these reductions are a **floor**, not a best case.

| # | Question a coding agent gets | Baseline (grep+read) | CodeIndex | Reduction |
|---|------------------------------|---------------------:|----------:|----------:|
| 1 | List the full API of the `CodeIndexStore` class | 12,844 | 1,051 | **−91.8%** |
| 2 | Outline the types & members of `RepositoryWatcher.cs` | 3,538 | 368 | **−89.6%** |
| 3 | Show the source of `IsGenerated`, `EstimateTokens`, `IsWithinRepo` | 2,944 | 357 | **−87.9%** |
| 4 | Which classes implement `IFileSystem`? | 2,734 | 65 | **−97.6%** |
| 5 | Find every use of the `ReadFileLines` method | 688 | 303 | −56.0% |
| 6 | Where is the `CODEINDEX_ROOT` env var read? | 179 | 81 | −54.7% |
| 7 | New here — what are the most important types to read first? | 24,907 | 2,021 | **−91.9%** |
| 8 | Are there any empty `catch` blocks? | 13,757 | 86 | **−99.4%** |
| | **TOTAL (8 tasks)** | **61,591** | **4,332** | **−93.0%** |

**93% fewer input tokens** across the suite. Tasks 5 and 6 are deliberate honesty anchors — pure "list the
matches" lookups where grep is genuinely competitive and CodeIndex only wins ~55%; the big wins are exactly where
the baseline is forced to pull whole files into context.

**What that costs.** At Claude Opus 4.8 input pricing (**$5 / 1M tokens**, as of 2026-07):

| | Baseline | CodeIndex |
|---|---:|---:|
| This 8-task suite, once | $0.308 | $0.022 |
| Per 1,000 such lookups | **$38.49** | **$2.71** |

~$36 saved per thousand lookups.

**Read this as a per-operation number, not a whole-session number.** It measures the *code-navigation* slice of a
task in isolation. A real agent session also spends tokens on reasoning, editing, running tests, and its own
output — navigation is only part of that, and every extra tool call re-sends the growing context.

So the **session-level** saving is smaller — sometimes near zero. It pays off only when the smaller per-query
payload outweighs CodeIndex's extra round-trips: large files you need small slices of, or navigation scattered
across many files. A focused, few-file task can come out roughly even. In one real end-to-end A/B on this repo,
total tokens and wall-clock landed **within ~1%** either way (CodeIndex cut raw navigation bytes ~29% but spent
it back on more tool calls). Full numbers and a run-it-yourself protocol for *your* task and model:
**[docs/BENCHMARK.md](docs/BENCHMARK.md)**.

**When it wins, washes, or loses:**

| Situation | Verdict |
|---|---|
| Large files you need a small slice of; references scattered across many files; a symbol to understand or safely change | **Win** — one dossier (`explain_symbol` / `prepare_change`) replaces a whole chain, where grep would pull entire files |
| A focused task in a few mid-size files | **Wash** — smaller per-call payloads roughly cancel the extra round-trips |
| A one- or two-call lookup; text / log / config hunts; a cold or stale index; tiny files; an unindexed language | **Grep wins** — a single ripgrep with no index and no orientation is hard to beat |

The one-call dossiers, the usage playbook injected via MCP `ServerInstructions`, and the speculative next-hop
appendix all exist to shrink the round-trip count that makes the wash a wash.

**Reproduce it yourself** (numbers regenerate from your checkout — no hand-maintained figures):

```bash
cd benchmarks/CodeIndex.Benchmarks
dotnet run -c Release -- context-cost
```

The suite's questions target this repo's own symbols, so it runs against the CodeIndex checkout by default.
Point it at another repo (`-- context-cost /path/to/repo`) after adapting the scenarios in
[`ContextCostReport.cs`](benchmarks/CodeIndex.Benchmarks/ContextCost/ContextCostReport.cs) to symbols that exist
there.

Want the end-to-end session-level proof (the `$ / tokens / wall-time` your MCP client's status line shows)? Run
the same handful of code questions in two sessions — one with the CodeIndex tools enabled, one with only
grep/read — and compare the status line. The per-task token deltas above are what drives that difference.

## The 20 tools

**One-call dossiers** (prefer these — each collapses a multi-tool chain into a single round-trip)

| Tool | Languages | What it does |
|------|-----------|--------------|
| `explain_symbol` | C# + TS | A symbol's identity + members, inheritance, source, and references in ONE response — instead of chaining `search_symbol` → `get_type_members` → `get_symbol_source` → `find_references` |
| `prepare_change` | C# + TS | Pre-edit briefing: definition + every call site + callers + implementors/overrides, in one call |

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

- **Token-lean by design.** Ranked results, grouping by file, per-file caps with true totals, and generated-file
  demotion keep every response small — that's the whole point (see [the numbers](#the-numbers)).
- **One round-trip by design.** The `explain_symbol` / `prepare_change` dossiers compose a whole navigation
  chain server-side; a playbook sent via MCP `ServerInstructions` steers agents to them; and single-match results
  pre-fetch their likely next hop. The client re-bills the entire conversation each turn, so *fewer* tool calls —
  not just smaller ones — is what saves tokens.
- **Non-blocking startup.** The server publishes a snapshot straight from the on-disk cache and answers
  immediately; a background watcher revalidates against the working tree. TS/SCSS builds *after* C# is already
  serving — zero added startup latency.
- **Live & incremental.** A file edit or branch switch reparses only what changed (per-file modified-time delta)
  — typically sub-second — so the index stays fresh without a restart.
- **Lock-free reads.** The index is an immutable snapshot swapped in atomically; readers never block on a rebuild
  and never see a half-updated index.
- **Syntax-only.** No assembly resolution, no `Compilation`, no NuGet restore — that's what keeps startup fast.
  Cross-file/overload resolution is therefore name-based (see [SPEC.md](SPEC.md) Limitations).
- **Deterministically testable.** All disk access flows through a single `IFileSystem` abstraction, so the whole
  engine is exercised by an in-memory file system in the test suite — no real disk required.

## Install

Build from source (needs the **.NET 10 SDK**), then point your MCP client at it:

```bash
git clone https://github.com/acikeldev/CodeIndex.git
cd CodeIndex
dotnet build CodeIndex.slnx -c Release
```

Full setup — global-tool vs built-DLL, `.mcp.json` for every client, the `CLAUDE.md` snippet that makes your
agent reach for these tools, and per-repo `codeindex.json` options — is in **[docs/INSTALL.md](docs/INSTALL.md)**.

## License

MIT — see [LICENSE](LICENSE).
