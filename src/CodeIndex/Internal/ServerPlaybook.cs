namespace CodeIndex.Internal;

/// <summary>
/// The server-instructions playbook sent to the MCP client on the handshake (via
/// <c>McpServerOptions.ServerInstructions</c>). The client injects it into the model's system prompt, so it rides
/// every turn at prefix-cache rates — the cheapest place to steer tool usage. Its whole job is to stop the two
/// expensive defaults: grep-then-read-whole-files, and the search → members → source → refs micro-chain. It names
/// the one-call composites (explain_symbol / prepare_change) and the batch reads that close an information need in
/// a single round-trip, because the client re-bills the entire conversation every turn — turns, not bytes, cost.
/// </summary>
internal static class ServerPlaybook
{
    public const string Instructions =
        "CodeIndex is a local code index. For any question about code — a symbol, type, method, file, or where\n"
        + "something is used — prefer these tools over grep / reading whole files: they resolve names accurately\n"
        + "and return small, pre-shaped answers.\n\n"
        + "Close each information need in ONE call instead of a chain. Every extra tool call re-sends the whole\n"
        + "conversation, so a call costs far more than the size of its result:\n"
        + "- Understand a type or method -> explain_symbol (a type: identity, members, inheritance, source, references;\n"
        + "  a method: source, references, callers — in one response). Do NOT hand-run search_symbol ->\n"
        + "  get_type_members -> get_symbol_source -> find_references.\n"
        + "- About to change a symbol -> prepare_change (definition + every call site + callers + implementors/overrides).\n"
        + "- Read several symbols' source -> get_context_bundle with all names at once, not one get_symbol_source each.\n"
        + "- New to the repo -> get_onboarding once (projects + key symbols, cached) for orientation; when you learn\n"
        + "  a durable fact (an entry point, a gotcha), remember '<fact>' so the next session gets it for free.\n"
        + "- Starting an unfamiliar task -> get_task_context '<task>' (task-focused symbols + a dossier).\n"
        + "- Who calls / implements X -> call_hierarchy or get_class_hierarchy (resolved, not a text guess;\n"
        + "  get_class_hierarchy transitive=true walks the whole subtree in one call).\n"
        + "- Trace a call chain / 'explain the flow' of a method (downstream or upstream, several levels) -> trace_calls\n"
        + "  in ONE call (direction=callees/callers). Do NOT reconstruct it by hand with call_hierarchy, nor with\n"
        + "  repeated search_symbol + get_symbol_source per node — that hand-chain is exactly what trace_calls replaces,\n"
        + "  and its output already gives each node's file:line + signature.\n"
        + "- Read one member's body -> get_symbol_source member='Name' (range resolved from the index, no line\n"
        + "  numbers needed) instead of reading the whole file.\n\n"
        + "TRUST the composite output — don't re-derive it. explain_symbol / prepare_change / trace_calls already\n"
        + "resolved the members, call sites, file paths (and for trace_calls, each node's definition site AND\n"
        + "signature), so do NOT re-run grep / glob or re-open those files with get_symbol_source / Read to\n"
        + "'double-check' or to name a node — the annotations are authoritative. Read a body ONLY when the task\n"
        + "needs the actual implementation, via get_symbol_source member='Name'. Follow up otherwise only when the\n"
        + "result says so — a truncation note ('Response budget reached' / '... N more lines') or a section it\n"
        + "didn't cover.\n\n"
        + "For plain TEXT — strings, error messages, config keys, TODOs, SQL — use the native grep / file tools:\n"
        + "you use them fluently and they need no schema load. For a pure literal / config / string lookup a single\n"
        + "ripgrep IS the whole job — do NOT load or call any CodeIndex tool for it. CodeIndex is for SYMBOLS and\n"
        + "STRUCTURE. Reach for search_text only when you want each text hit tagged with its enclosing Type.member +\n"
        + "prod/test/generated (which grep can't give). And fall back to grep whenever a CodeIndex tool returns nothing.";
}
