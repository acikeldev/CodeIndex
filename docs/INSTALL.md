# Installing & configuring CodeIndex

Setup, MCP wiring, and per-repo configuration for the CodeIndex MCP server. For **what it is and why it helps**, see the [README](../README.md).

## Install

CodeIndex isn't on NuGet yet — build it from source. You need the **.NET 10 SDK**.

```bash
git clone https://github.com/acikeldev/CodeIndex.git
cd CodeIndex
dotnet build CodeIndex.slnx -c Release
```

That produces the server at `src/CodeIndex/bin/Release/net10.0/CodeIndex.dll`. From here, pick one of two ways
to run it.

**Option A — install as a global tool** (gives you a `codeindex` command on your PATH; closest to the eventual
NuGet experience):

```bash
dotnet pack src/CodeIndex/CodeIndex.csproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg CodeIndex --version 0.0.0-dev
```

`codeindex` is now on your PATH (via `~/.dotnet/tools`). To pick up later changes, re-pack and reinstall:

```bash
dotnet pack src/CodeIndex/CodeIndex.csproj -c Release -o ./nupkg
dotnet tool uninstall --global CodeIndex
dotnet tool install --global --add-source ./nupkg CodeIndex --version 0.0.0-dev
```

**Option B — run the built DLL directly** (no global install; easiest to iterate on — just `dotnet build` again
after a change). Point your MCP client at `dotnet <path>/CodeIndex.dll`; see the next section.

> A tagged release publishes to NuGet.org via the `Release` GitHub Actions workflow; once that's live,
> `dotnet tool install -g CodeIndex` becomes the one-liner. Until then, use the source build above.

## MCP configuration

CodeIndex is an MCP **stdio** server: your client launches the process and talks to it over stdin/stdout.
Configure it the way any MCP client expects.

**Claude Code** — drop a `.mcp.json` at your repo root (project-scoped and committable, so your whole team gets
it automatically).

> **Eager vs. deferred tool loading (a trade-off, not a default to flip).** Since Claude Code v2.1.121, MCP tool
> schemas are *deferred* behind `ToolSearch`: the agent pays a one-time `ToolSearch` round-trip the first time it
> reaches for a CodeIndex tool, but a session that never navigates code carries none of the ~20 schemas. Adding
> `"alwaysLoad": true` to the `codeindex` entry loads all schemas up front — it removes that first-use round-trip
> but pays the full schema budget on *every* session, and exposing 20 tools eagerly can nudge the model to
> over-call. Keep the deferred default unless you navigate code in most sessions. The client-wide alternative is
> the `ENABLE_TOOL_SEARCH` env var — `false` disables deferral for *all* MCP servers (not just this one),
> `auto:N` defers only once tool schemas exceed N% of the context window. `alwaysLoad` requires Claude Code
> v2.1.121+ (older clients silently ignore it).

Option A — global tool:

```json
{
  "mcpServers": {
    "codeindex": {
      "command": "codeindex",
      "args": []
    }
  }
}
```

Option B — built DLL (use absolute, forward-slash paths — they work on Windows too):

```json
{
  "mcpServers": {
    "codeindex": {
      "command": "dotnet",
      "args": [
        "C:/path/to/CodeIndex/src/CodeIndex/bin/Release/net10.0/CodeIndex.dll",
        "--root", "C:/path/to/your-repo"
      ]
    }
  }
}
```

You can also register it from the CLI instead of hand-editing the file: `claude mcp add codeindex -- codeindex`
(global tool), or `claude mcp add codeindex -- dotnet <path>/CodeIndex.dll --root <repo>` (built DLL).

**Repo root.** The server indexes one repository. It resolves the root in this order: `--root <path>` (or `-r`)
→ the `CODEINDEX_ROOT` (or `REPO_ROOT`) environment variable → walking up from the working directory to the
nearest `.git`. With the global-tool setup, launching from inside your repo is usually enough; with the DLL
setup, pass `--root` explicitly as shown.

**Other clients** (Cursor, Copilot, Windsurf, …) use the same `command` / `args` shape in their own MCP config
file — reuse either block above.

## Use it from your AI assistant

So your coding agent actually *reaches for* these tools instead of grepping, add a short note to your
`CLAUDE.md` / `AGENTS.md` (or the equivalent rules file for your client). Paste this in and adapt as needed:

```markdown
## Code Navigation (CodeIndex MCP)

This repo has a CodeIndex MCP server. For any question about code — a symbol,
type, method, file, or where something is used — PREFER its tools over raw
grep / file-reading. They resolve symbols accurately and return token-lean
results.

Close each need in ONE call — every extra tool call re-sends the whole
conversation, so a call costs more than the size of its result:
- Understand a type or method -> `explain_symbol` (identity, members,
  inheritance, source, references in one response). Do NOT hand-run
  `search_symbol` -> `get_type_members` -> `get_symbol_source` -> `find_references`.
- About to change a symbol -> `prepare_change` (definition + call sites +
  callers + implementors/overrides).
- Read several symbols' source -> `get_context_bundle` with all names at once.
- New to the repo -> `get_onboarding` once (cached projects + key symbols).
- Who calls / implements X -> `call_hierarchy` / `get_class_hierarchy`.
- Text / regex (strings, config, comments) -> `search_text`.

When a dossier (`explain_symbol` / `prepare_change`) answers your question, act
on it directly — it already lists the members, call sites, and file paths, so
don't re-grep to double-check them. Only follow up when the dossier says to (a
truncation note, or a section it didn't cover), using the call it names.

Fall back to plain grep / file reads only when the target isn't indexed
(non-code files) or CodeIndex returns nothing.
```

See the [tools list](../README.md#the-20-tools) in the README for the complete set.

## Index configuration (optional)

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
│   └── Mcp/              The 20 MCP tool implementations
├── tests/CodeIndex.Tests/            xUnit unit tests over an in-memory file system
└── benchmarks/CodeIndex.Benchmarks/  BenchmarkDotNet latency benchmarks + the context-cost report
```

## Dependencies

| Package | Purpose |
|---------|---------|
| ModelContextProtocol | Official C# MCP SDK |
| Microsoft.Extensions.Hosting | DI and host lifecycle |
| Microsoft.CodeAnalysis.CSharp | Roslyn — C# syntax parsing |
| TreeSitter.DotNet | tree-sitter — TS/TSX/SCSS parsing (bundled native grammars) |
| MessagePack | Binary cache serialization |
