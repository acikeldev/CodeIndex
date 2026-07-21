# Benchmarking CodeIndex honestly

CodeIndex ships two different measurements. They answer different questions, and it's important not to confuse
them — one is a **per-operation** number, the other is a **whole-session** number.

| | What it measures | Where | Headline |
|---|---|---|---|
| **Context-cost** | Tokens ingested to answer one code-navigation *operation*, CodeIndex vs grep+read | `benchmarks/CodeIndex.Benchmarks/ContextCost` | ~90% fewer tokens on the navigation slice |
| **Session A/B** | Total `$` / tokens / wall-clock to complete one real *task*, with vs without CodeIndex | this doc (you run it) | The realistic, smaller, session-level saving |

## 1. Context-cost (per-operation) — and why it isn't the whole story

The [context-cost benchmark](../README.md#the-numbers) shows a ~90% token reduction across 10 navigation tasks.
That number is **real and reproducible**, but it is deliberately narrow: it isolates the code-navigation slice of
work and measures nothing else.

A real agent session is not 8 back-to-back lookups. It also spends tokens on reasoning, editing files, running
builds/tests and reading their output, and writing its own explanations — none of which CodeIndex changes. It also
re-sends context each turn (with prompt caching absorbing much of the repeat cost). So:

> **Do not expect a 90% session-level saving.** 90% is the reduction on the navigation *portion*. The
> whole-session saving is smaller and depends entirely on how navigation-heavy the task is.

Rough intuition for where a task lands:

- **Navigation-heavy** (onboarding to an unfamiliar repo, code review, "where/how is X used", impact analysis) —
  the biggest session-level wins.
- **Balanced** (implement a feature touching a few files) — moderate.
- **Navigation-light** (edit one file you already know, mechanical refactor) — little to none.

## 2. Session A/B (whole-task) — the number that actually matters

This is the end-to-end comparison: run the **same task** twice — once with CodeIndex available, once without —
and read the real cost off your MCP client's status line. Because the status line reports true `input` / `output`
/ `cache` tokens, wall-clock, and `$`, this is the gold-standard measurement. Only you (in an interactive session)
can capture it — no benchmark script can.

### Protocol

**Repo (public, fixed):**

```bash
git clone https://github.com/acikeldev/CodeIndex.git
cd CodeIndex
dotnet build CodeIndex.slnx -c Release
```

**The task (fixed, read-only so both runs start from identical state):** paste this exact prompt in each session:

> Investigate how CodeIndex stays fresh and correct under edits and concurrency. Answer each with at least one
> `File.cs:line` citation: (1) how a file change or branch switch is detected and what triggers a rebuild;
> (2) full vs delta rebuild — what decides which runs and what gets reparsed on a delta; (3) how the C# and
> TS/SCSS segments are kept separate and composed into one published snapshot; (4) how the snapshot is swapped so
> readers never see a torn index; (5) what serializes concurrent C# and TS rebuilds and any lock-ordering/deadlock
> risk; (6) how non-blocking startup works. Then stop.

A read-only investigation is used on purpose: an editing task would mutate the repo between the two runs, so the
second arm wouldn't start from the same state. Swap in your own task if you want — just keep it identical across
both arms and reset the repo (`git clean -fd && git checkout .`) between runs if it writes anything.

**Arm A — with CodeIndex:** configure the MCP server ([docs/INSTALL.md](INSTALL.md)) and add the `CLAUDE.md`
snippet so the agent prefers it. Start a **fresh** session, paste the prompt, let it finish.

**Arm B — without CodeIndex:** disable the server (`claude mcp remove codeindex`, or delete it from `.mcp.json`)
and remove the `CLAUDE.md` snippet so grep/read is all the agent has. Start a **fresh** session, paste the
**same** prompt, let it finish.

Keep everything else identical between the two: same model, same effort/reasoning setting, same machine. Read the
status line after each run and fill in:

| Metric (from the status line) | Arm A — with CodeIndex | Arm B — without | Δ |
|---|---:|---:|---:|
| input tokens | | | |
| output tokens | | | |
| cache read | | | |
| wall-clock | | | |
| **cost ($)** | | | |

The `$` and wall-clock deltas are the honest, session-level answer for *your* task and model.

## 3. Live measured example on this repo

Two isolated agents (same model, same harness, no shared context) each solved the exact task above against the
CodeIndex checkout. Both produced correct, equivalently-cited answers to all six questions. Harness-measured:

| Metric | With CodeIndex | Without (grep + read) | Δ |
|---|---:|---:|---:|
| **Total tokens** (harness usage) | 72,545 | 71,839 | +1.0% |
| **Wall-clock** | 111 s | 104 s | +6.7% |
| Tool calls | 17 | 6 | +11 |
| Tool output pulled into context | ~70,500 chars (~17.6k tok) | ~99,300 chars (~24.8k tok) | **−29%** |
| Cost @ Opus 4.8 rates | \~equal (within ~1%) | \~equal | ~0 |

**Read this honestly: at the session level, this task was a wash.** CodeIndex cut the raw navigation payload
~29% (targeted `get_symbol_source` slices instead of whole-file reads), but it spent that saving back on **more
round-trips** — 17 tool calls vs 6. Each tool call is another turn whose (growing) context is re-sent, so the
many-small-queries style roughly cancelled the smaller per-query payload. Total tokens and wall-clock landed
within a few percent — with CodeIndex marginally *higher* on both.

Why this task favoured the grep arm — and when it wouldn't:

- The relevant code is concentrated in **4 files** (`CodeIndexStore`, `RepositoryWatcher`, `IndexCache`,
  `Program`). A competent grep agent found them with **one** ripgrep and read them whole — that's
  round-trip-efficient, and whole-file reads mean it rarely had to go back for "just a bit more."
- The session-level win only materialises when payload reduction **outweighs** the added round-trips — i.e. when
  the relevant code is **large files you need small slices of**, is **scattered across many files** (so the grep
  arm reads a lot of irrelevant bulk), or the task needs repeated breadth queries (`find_references` /
  `get_class_hierarchy` across the whole repo) that grep can only answer by reading many files.
- On a **concentrated, few-file** task like this one, expect **rough parity**.

**Since this run, the round-trip count is exactly what we attacked.** The `explain_symbol` / `prepare_change`
dossiers collapse the `search_symbol` → `get_type_members` → `get_symbol_source` → `find_references` chain (much
of those 17 calls) into one; a usage playbook injected via MCP `ServerInstructions` steers agents to them; and
single-match results pre-fetch their likely next hop. This measurement predates those features, so re-run the
protocol to see the current gap — the honest expectation is the concentrated-file case moves from "grep slightly
ahead" toward parity, with the decisive wins still on the large-file / scattered-reference tasks in §2.

Caveats: single run (n=1 — real variance between runs); the tool-output char counts are each agent's own
approximate tally; `Total tokens` and `Wall-clock` are the harness's own measurements. Cost isn't split into
input/output here, but since both arms are within ~1% on total tokens, their dollar cost is within ~1% too. For
your own task and model, run the two-session protocol above and read the status line.

## 4. Balanced session A/B (re-run across a mixed task set)

§3 was a single, concentrated-file task, and it invited a re-run once the round-trip count had been attacked. Here
is that re-run, widened to a **balanced** set so no single task type dominates: a literal/config lookup (grep's
home turf), a transitive class-hierarchy question, a production-vs-test usage question, a multi-step mixed task,
and a composite session that combines them. Each cell is **hybrid (CodeIndex + grep)** vs **grep-only**, same
model, cache-cold alternation, 2 runs/cell (n=2 — directional, not a significance test).

| Task type | Hybrid | Grep-only | Δ cost | Δ turns |
|---|---:|---:|---:|---:|
| Literal / config lookup | $0.285 | $0.283 | **tie** | 9 vs 8 |
| Transitive class hierarchy | $0.226 | $0.377 | **−40%** | 3 vs 9 |
| Production/test usage facet | $0.186 | $0.245 | **−24%** | 3 vs 4 |
| Multi-step mixed | $0.549 | $0.708 | **−22%** | 21 vs 24.5 |
| Composite session | $0.729 | $0.938 | **−22%** | 17.5 vs 24.5 |

**Reading it honestly:**

- **The literal task is a genuine tie.** With plain text routed to native grep, the hybrid stops reaching for an
  indexed text search where a single ripgrep is leaner — so it neither wins nor loses grep's home turf.
- **The structural tasks are the real wins, and they're mechanistic, not luck.** A transitive hierarchy is one
  `get_class_hierarchy transitive=true` call versus one grep *per level*; a usage facet is one `find_references`
  call versus grep-plus-manual-classification. Fewer round-trips → less re-sent context → lower cost. This is the
  part that survives even an adversarial pairing (best grep run vs worst hybrid run) at ~−35%.
- **The composite-session cell is the noisiest — read it as directional, not precise.** On an independent re-run
  its grep-only baseline swung widely and the same combined task set landed anywhere from roughly **−4% to −22%**,
  because one long composite session yields few independent samples. The trustworthy figures are the per-task-type
  rows and the multi-step mixed task; the single composite aggregate is the least reproducible number here.
- **Worst / best / typical.** Structural-heavy sessions land near **−40%**; literal-heavy sessions near **−5%**
  (breakeven; pure-literal ties); a **balanced** mix is **~−22%** — both the midpoint of that range and the
  measured balanced-set median.

Caveats: n=2/cell here (directional) — **superseded by §5's n=10 run below**; wall-clock is the noisiest metric and
can go *negative* on tiny tasks because of the server's fixed start-up cost; raw token totals are dominated by cheap
prompt-cache reads — so **cost and turn count are the trustworthy signals**, not total-token deltas.

## 5. High-n trackable re-run (n=10, 95% CIs, per-tool transcripts)

The v3 / §4 runs were n=2 and could not say *why* a cell behaved as it did. This run fixes both: the server is the
current release, **n=10 per cell**, six cells (the five above plus a transitive call-trace task that exercises
`trace_calls`), captured with `--output-format stream-json` so every run records its full tool-call sequence.
120/120 runs, 0 errors. CI = bootstrap 95% on the median-cost delta; "resolved" = the CI excludes 0.

| Cell | A$ (hybrid) | B$ (grep) | Δ cost | 95% CI | A/B turns | verdict |
|---|---:|---:|---:|---:|---:|---|
| Transitive hierarchy | 0.224 | 0.406 | **−45%** | [−52%, −38%] | 3 / 8.5 | hybrid win (resolved) |
| Composite session | 0.795 | 1.060 | **−25%** | [−35%, −15%] | 23.5 / 31.5 | hybrid win (resolved) |
| Prod/test facet | 0.214 | 0.270 | **−21%** | [−40%, −5%] | 3 / 4 | hybrid win (resolved) |
| Multi-step (SMS flow) | 0.707 | 0.642 | +10% | [−9%, +28%] | 28.5 / 17.5 | **tie** (CI spans 0) |
| Literal / config | 0.349 | 0.287 | +22% | [+1%, +60%] | 9 / 7.5 | grep win (resolved) |
| Transitive call-trace | 0.659 | 0.481 | +37% | [+13%, +55%] | 21.5 / 10 | grep win (resolved) |

Equal-weight atomic basket (the four single-question cells): cost **−7%**, turns **+16%**.

**What n=10 settles that n=2 couldn't:**

- **The structural wins are real and reproducible.** The hierarchy and facet cells are razor-tight — hybrid cost
  lands in 0.20–0.23 across all ten runs — so the win is mechanistic, not a lucky draw.
- **The composite flips from v3's "wash" to a resolved −25% win.** v3's ambiguity was small-sample noise, as suspected.
- **The honest worst case is a _loss_, not a tie.** Pure-literal (+22%) and locate-then-read-heavy call-trace (+37%)
  are resolved losses (CIs exclude 0). The earlier "worst ~−5% tie" framing was too kind.

**Why — from the transcripts (what the tool counts + call order reveal):**

- **The winning pattern is two calls.** Hierarchy / facet sessions are `load-schema → one resolved tool → stop`: one
  `get_class_hierarchy transitive` or one `find_references` answers the whole question, where grep needs a search per
  level or per classification.
- **The losing cells over-read.** SMS / composite / call-trace rack up `get_symbol_source` (90 / 64 / 74 calls across
  ten runs) plus `search_symbol`. In the call-trace transcripts the agent invokes `trace_calls` **once, correctly**,
  then re-derives each node by hand (`… → trace_calls → explain_symbol → search_symbol → get_symbol_source×4 →
  Read×3`) instead of trusting the tree's `[File.cs:line]` annotations. A **steering / over-read** issue, not a defect.
- **A `ToolSearch` schema-load tax hits every hybrid session** (1–2 turns up front to load the deferred MCP schemas).
  On the 2-call structural cells that is *half* the session; on a short literal task it is enough to tip hybrid to a
  loss — grep pays nothing to "load ripgrep."

### Can steering close the losses? A second n=10 run (v6) says no.

The obvious hypothesis was: *steer the agent to trust composite output and it will stop over-reading.* We tested it.
A follow-up n=10 run used a server with (a) `trace_calls` inlining each node's **signature** next to its file:line
plus an explicit "this is authoritative — do NOT open these to verify" note, and (b) playbook steering to trust
composite output, route a pure-literal lookup entirely to grep, and prefer `trace_calls` over hand-reconstruction.

**It did not move the losing cells.** Literal +22% → **+31%**, call-trace +37% → **+39%** (both still resolved
losses); the structural wins held (transitive −37%, facet −9%, composite −21%). The transcripts show why:
`… → trace_calls → search_symbol → get_symbol_source ×6 → Read ×3` — the agent read the note, called `trace_calls`
once, and **over-read anyway**. `get_symbol_source` counts barely moved (call-trace 74→72). Two corrections fell out
too: the `ToolSearch` schema-load cost is **not** hybrid-only (the grep arm pays it for built-in deferred tools, so
it is not the literal-loss driver), and soft prose does not stop the model verifying by reading.

**We then built and probed the structural fix — and it ALSO failed.** A deep-research pass (SWE-agent NeurIPS'24,
AGENTS.md ETH'26) pointed at a one-call composite that **inlines the bodies** the agent would otherwise re-read, so
there is nothing left to fetch. We implemented it (`trace_calls includeBodies`: the full source of every traced node
inlined in the same response, plus a "do NOT open" note) and ran a cheap n=3 probe on the call-trace task before
spending on a full A/B. Result: **the agent over-read anyway.** In the two probe runs that called `trace_calls`, it
then issued **8 and 14 `get_symbol_source` calls after** receiving the inlined bodies; a third run bypassed
`trace_calls` entirely and used `search_symbol`+`Read`. Cost did not improve (it slightly worsened, from the larger
payload the agent re-read regardless). This answers the sharpest open question: **an inlined/structured body does NOT
read as "terminal/authoritative" to this agent — it verify-reads no matter what the response contains.**

**Honest conclusion.** Two independent attempts — soft steering (v6) and structural body-inlining (probe) — both
failed to stop the over-read on the read-heavy cells. The behavior is not information-driven (the data was already in
context) and is not reachable by anything the server puts in a tool result. So there is **no server-side fix** for
the call-trace / explain-flow loss; the only untested lever is client-side — not exposing the index (routing to grep)
for those task types so the agent physically cannot loop on it. `includeBodies` is kept as an opt-in (default off,
since default-on only added tokens). The worst-case cells remain a loss; the wins (structural, −21..−45%) are the
honest, durable value.

---

**Bottom line.** The context-cost benchmark proves CodeIndex makes each navigation *operation* dramatically cheaper
(~90%). The session A/B (n=10, §5) says what that's worth on a real *task*: **structural work is a resolved −21% to
−45% win, a realistic composite session −25%**, a multi-step "explain the flow" task a **tie**, and **pure-literal or
locate-then-read-heavy tasks a resolved loss (+22% to +37%)** — the last driven by the agent over-reading past a
resolved answer, not by the index. Always less than the per-operation figure — report both, never conflate them.
