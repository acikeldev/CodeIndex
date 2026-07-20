# CodeIndex MCP

**Stop paying your LLM to read whole files.** CodeIndex is a fast, **local** code-intelligence server for the
**Model Context Protocol (MCP)** that answers "where is X / what's in Y / who calls Z" with resolved, token-lean
results — so your coding agent spends its context window (and your pay-as-you-go budget) on *thinking*, not on
grepping and re-reading source.

It indexes **C#** (Roslyn) and **TypeScript / TSX / SCSS** (tree-sitter) in one process and serves **22 tools** to
any MCP client (Claude Code, Cursor, Copilot, …). No cloud, no embeddings, no API keys — everything runs on your
machine.

→ **[Install & configure](docs/INSTALL.md)** · [The 22 tools](#the-22-tools) · [How it works](#how-it-works)

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
| 1 | List the full API of the `CodeIndexStore` class | 12,956 | 1,070 | **−91.7%** |
| 2 | Outline the types & members of `RepositoryWatcher.cs` | 3,538 | 398 | **−88.8%** |
| 3 | Show the source of `IsGenerated`, `EstimateTokens`, `IsWithinRepo` | 2,945 | 364 | **−87.6%** |
| 4 | Which classes implement `IFileSystem`? | 3,185 | 76 | **−97.6%** |
| 5 | Find every use of the `ReadFileLines` method | 688 | 315 | −54.2% |
| 6 | Where is the `CODEINDEX_ROOT` env var read? | 179 | 108 | −39.7% |
| 7 | New here — what are the most important types to read first? | 25,009 | 2,016 | **−91.9%** |
| 8 | Are there any empty `catch` blocks? | 14,361 | 103 | **−99.3%** |
| 9 | Understand the `CodeIndexStore` class and where it's used | 24,265 | 4,208 | **−82.7%** |
| 10 | Which methods contain the phrase "not indexed", and is each in production or test code? | 3,077 | 359 | **−88.3%** |
| | **TOTAL (10 tasks)** | **90,203** | **9,017** | **−90.0%** |

**90% fewer input tokens** across the suite. Tasks 5 and 6 are deliberate honesty anchors — pure "list the
matches" lookups where grep is genuinely competitive and CodeIndex wins only ~40–54%. Task 9 is the flagship
`explain_symbol` dossier: its 4,208 tokens are the *largest* CodeIndex response here (identity + members +
source + references in one payload), yet it still beats the grep chain — locate the class, read the whole
~1,100-line file, grep the usages — by ~83%, in **one** call instead of several. Task 10 is where structured
text search earns its keep: grep can print the matching lines but not the *method* each sits in, so it must
read a window around every hit; `search_text` tags each hit with its enclosing `Type.member` and adds a
prod/test tally in one call — a −88% cut.

**What that costs.** At Claude Opus 4.8 input pricing (**$5 / 1M tokens**, as of 2026-07):

| | Baseline | CodeIndex |
|---|---:|---:|
| This 10-task suite, once | $0.45 | $0.05 |
| Per 1,000 such lookups | **$45.10** | **$4.51** |

~$41 saved per thousand lookups.

**Read this as a per-operation number, not a whole-session number.** It measures the *code-navigation* slice of a
task in isolation. A real agent session also spends tokens on reasoning, editing, running tests, and its own
output — navigation is only part of that, and every extra tool call re-sends the growing context.

So the **session-level** saving is smaller than the per-operation figure, and it depends on *what the session is
made of*. It pays off when the smaller per-query payload and fewer round-trips outweigh CodeIndex's fixed
overhead: large files you need small slices of, hierarchy/reference/usage questions, or navigation scattered
across many files. A session that is nothing but one- or two-call literal lookups comes out roughly even.

A balanced end-to-end A/B (a mix of literal, structural, and multi-step questions; **hybrid = CodeIndex + grep**
vs **grep-only**, same model, cache-cold alternation) brackets the range:

| Session workload | Cost, hybrid vs grep-only (n=10, 95% CI) |
|---|---:|
| **Best** — structural-heavy (transitive hierarchy, references, usage facets) | **−45%** [−52%, −38%] |
| **Typical** — a realistic mixed / composite session | **−25%** [−35%, −15%] |
| **Worst** — pure literal, or "locate an entry point then read the chain" | **hybrid _loses_: +22% to +37%** |

On structural cells the turn count roughly halves — *fewer round-trips*, the mechanism behind the cost win. At
**n=10 the direction is statistically resolved** (95% CIs exclude 0) for every cell except a genuine tie on a
multi-step "explain the flow" task. Two metrics still carry honesty flags: wall-clock is noisy (the server pays a
fixed start-up / tool-load cost, so a *tiny* task can be slower), and raw token totals are inflated by cheap
cache-read volume — so **cost and turn count are the reliable signals**. Full numbers, 95% CIs, the per-tool
transcript breakdown, and a run-it-yourself protocol: **[docs/BENCHMARK.md](docs/BENCHMARK.md)**.

**When it wins, washes, or loses:**

| Situation | Verdict |
|---|---|
| Large files you need a small slice of; references scattered across many files; a symbol to understand or safely change | **Win** — one dossier (`explain_symbol` / `prepare_change`) replaces a whole chain, where grep would pull entire files |
| A focused task in a few mid-size files | **Wash** — smaller per-call payloads roughly cancel the extra round-trips |
| A one- or two-call lookup; text / log / config hunts; a cold or stale index; tiny files; an unindexed language; or "find an entry point then read the whole chain" (the agent over-reads after the resolved answer) | **Grep wins** — a single ripgrep with no index and no orientation is hard to beat |

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

## The 22 tools

**One-call dossiers** (prefer these — each collapses a multi-tool chain into a single round-trip)

| Tool | Languages | What it does |
|------|-----------|--------------|
| `explain_symbol` | C# + TS | A symbol's identity + members, inheritance, source, and references in ONE response — instead of chaining `search_symbol` → `get_type_members` → `get_symbol_source` → `find_references`; `verbosity=concise` drops bodies for a signatures-only view (~⅓ the tokens) |
| `prepare_change` | C# + TS | Pre-edit briefing: definition + every call site + callers + implementors/overrides, in one call; `verbosity=concise` for the blast radius without the definition body |
| `trace_calls` | C# | Transitive call trace in one call — follow the callee chain (downstream flow) or caller chain (upstream impact) N levels deep, instead of hand-running `call_hierarchy` per node; cycle-safe, depth + node bounded |

**Orientation**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `get_onboarding` | C# + TS | One-call session orientation: projects + top PageRank symbols + where to start, cached (keyed to the index build, refreshes on change); appends any notes left via `remember` |
| `get_task_context` | C# + TS | Task-conditioned orientation: anchors a free-text task to real types, then PageRank focused on them + a dossier for the strongest anchor; says so and falls back to plain orientation when it can't anchor |
| `remember` | C# + TS | Persist a short repo fact (an entry point, a gotcha) to the repo's cache; it's surfaced by `get_onboarding` in every future session and survives restart + reindex, so orientation is discovered once, not re-derived |
| `suggest_queries` | C# + TS | Index overview (top projects, largest files, type distribution) + ready-to-run starting queries |
| `repo_map` | C# + TS | The most important symbols, ranked by PageRank over the symbol-reference graph; pass `focus=` for task-relevant (personalized) ranking |
| `repo_info` | C# + TS | Repo overview (projects + file counts) and index health (build kind/age, cache path + schema); `project=` lists that project's files |

**Search**

| Tool | Languages | What it does |
|------|-----------|--------------|
| `search_symbol` | C# + TS | Find types/members by name; kind & project filters, token budget |
| `search_text` | all | Structural text / regex search: each hit tagged with its enclosing `Type.member` + a prod/test/generated split (for plain text, native grep is faster) |
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
  chain server-side, and `trace_calls` collapses a multi-level call walk into a single call; a playbook sent via
  MCP `ServerInstructions` steers agents to them; and single-match results pre-fetch their likely next hop. The
  client re-bills the entire conversation each turn, so *fewer* tool calls — not just smaller ones — is what saves
  tokens.
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
