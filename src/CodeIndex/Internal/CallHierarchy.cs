using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Heuristic C# call hierarchy over Roslyn syntax trees (no semantic model). Each invocation is bound to its
/// declaring type by <see cref="CallResolver"/> (receiver type from declared locals/parameters/fields/properties,
/// base-type walk); calls whose receiver type cannot be determined are kept but marked unresolved ('?') and never
/// presented as fact, and calls that bind outside the index (framework/library) are dropped from 'callers'.
/// A method may be named "Type.Method" to scope it to one type. Its value over find_references: <b>callers</b>
/// keeps only real INVOCATIONS (not declarations/comments/strings) and reports the ENCLOSING member (the actual
/// caller), and <b>callees</b> lists what a method invokes. Parses on demand (parallel; generated files excluded;
/// project-scopable). C# only — TS is in <see cref="TsCallHierarchy"/>.
/// </summary>
internal static class CallHierarchy
{
    private const string UnresolvedLegend = "'?' = receiver type not determined syntactically: a name match, not a confirmed call.";

    /// <summary>Who invokes <paramref name="method"/> — grouped by file, each hit labelled with its enclosing member.</summary>
    public static string Callers(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string? project, int max, int perFileCap)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "call_hierarchy: provide a method name.";
        }

        (string? typeFilter, string name) = Split(method);
        List<SourceFileIndex> candidates = Candidates(files, project);
        CallResolver resolver = new(files);
        bool indexed = resolver.IsIndexedMethod(name);
        List<string> roots = typeFilter is null ? [] : RootKeys(resolver, name, typeFilter);
        if (typeFilter is not null && roots.Count == 0)
        {
            return $"call_hierarchy(callers): no C# method '{method}' is indexed.";
        }

        IReadOnlyList<string> definers = resolver.Definers(name);
        bool labelTargets = typeFilter is null && definers.Count > 1;
        FileReader reader = new(fileSystem);
        ConcurrentBag<CallerHit> bag = new();
        Parallel.ForEach(candidates, f =>
        {
            if (ParseFile(reader, f) is not (CompilationUnitSyntax root, string[] lines))
            {
                return;
            }

            CallResolver.FileScope scope = new(f);
            foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (CallResolver.InvokedName(inv) != name)
                {
                    continue;
                }

                bool possible = false;
                string target = string.Empty;
                if (indexed)
                {
                    CallResolution r = resolver.Resolve(inv, scope);
                    if (r.Kind == CallResolutionKind.External)
                    {
                        continue;
                    }

                    possible = r.Kind == CallResolutionKind.Unresolved;
                    if (typeFilter is not null && !Reaches(resolver, roots, r.Targets))
                    {
                        continue;
                    }

                    if (labelTargets && !possible)
                    {
                        target = " → " + string.Join(" | ", r.Targets.Select(t => $"{resolver.TypeName(t)}.{name}"));
                    }
                }

                int line0 = inv.GetLocation().GetLineSpan().StartLinePosition.Line;
                string snippet = line0 >= 0 && line0 < lines.Length ? Output.ClipLine(lines[line0].Trim(), max: 140) : inv.ToString();
                bag.Add(new CallerHit(f.ProjectName, f.SourceFilePath, f.FileName, line0 + 1, EnclosingMember(inv) + target, snippet, possible));
            }
        });

        // Handlers wired in XAML (Click="Save_Click") have the markup as their only caller.
        foreach (XamlWiring w in XamlWirings(resolver, files, project, name))
        {
            if (typeFilter is not null && !Reaches(resolver, roots, w.Targets))
            {
                continue;
            }

            FileReader.ReadResult read = reader.ReadFileLines(w.File.SourceFilePath);
            string snippet = read.Success && w.Hook.Line - 1 < read.Lines!.Length ? Output.ClipLine(read.Lines[w.Hook.Line - 1].Trim(), max: 140) : string.Empty;
            string target = labelTargets ? " → " + string.Join(" | ", w.Targets.Select(t => $"{resolver.TypeName(t)}.{name}")) : string.Empty;
            bag.Add(new CallerHit(w.File.ProjectName, w.File.SourceFilePath, w.File.FileName, w.Hook.Line, $"(XAML) {w.Hook.Element}.{w.Hook.Attribute}{target}", snippet, false));
        }

        string note = labelTargets
            ? $"{definers.Count} types declare '{name}' ({string.Join(", ", definers.Take(4).Select(resolver.TypeName))}{(definers.Count > 4 ? ", …" : string.Empty)}) — each resolved call is labelled with its target; pass Type.{name} to keep one."
            : string.Empty;
        return RenderCallers(method, bag, project, max, perFileCap, note);
    }

    /// <summary>What <paramref name="method"/> invokes — distinct callees with counts, from the method's body,
    /// each labelled with the type it binds to. Only the file(s) DEFINING the method are parsed.</summary>
    public static string Callees(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string? project, int max)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "call_hierarchy: provide a method name.";
        }

        (string? typeFilter, string name) = Split(method);
        List<SourceFileIndex> definerFiles = Candidates(files, project)
            .Where(f => f.Types.Any(t => (typeFilter is null || t.Name == typeFilter) && t.Members.Any(m => m.Name == name && m.Kind is Models.SymbolKind.Method)))
            .ToList();

        if (definerFiles.Count == 0)
        {
            return $"call_hierarchy(callees): no C# method named '{method}' is indexed{Scope(project)}.";
        }

        CallResolver resolver = new(files);
        FileReader reader = new(fileSystem);
        ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
        ConcurrentBag<string> declFiles = new();
        int unresolved = 0;
        Parallel.ForEach(definerFiles, f =>
        {
            if (ParseFile(reader, f) is not (CompilationUnitSyntax root, _))
            {
                return;
            }

            CallResolver.FileScope scope = new(f);
            bool found = false;
            foreach (MethodDeclarationSyntax decl in Declarations(root, name, typeFilter))
            {
                found = true;
                foreach (InvocationExpressionSyntax inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    CallResolution r = resolver.Resolve(inv, scope);
                    if (r.Name.Length == 0 || r.Name == name)
                    {
                        continue; // unnamed (delegate) invocation, or self-recursion noise in the summary
                    }

                    string label = r.Kind switch
                    {
                        CallResolutionKind.Resolved => string.Join(" | ", r.Targets.Select(t => $"{resolver.TypeName(t)}.{r.Name}")),
                        CallResolutionKind.Unresolved => $"{r.Name} (?)",
                        _ => r.Name,
                    };
                    if (r.Kind == CallResolutionKind.Unresolved)
                    {
                        Interlocked.Increment(ref unresolved);
                    }

                    counts.AddOrUpdate(label, 1, (_, v) => v + 1);
                }
            }

            if (found)
            {
                declFiles.Add($"{f.FileName} ({f.ProjectName})");
            }
        });

        StringBuilder sb = new();
        sb.AppendLine($"{method} calls ({counts.Count} distinct callees), defined in: {string.Join(", ", declFiles.Distinct().OrderBy(x => x, StringComparer.Ordinal))}");
        if (unresolved > 0)
        {
            sb.AppendLine(UnresolvedLegend);
        }

        sb.AppendLine();
        if (counts.IsEmpty)
        {
            sb.AppendLine("  (no invocations found in the method body)");
            return sb.ToString();
        }

        foreach (KeyValuePair<string, int> kv in counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(max))
        {
            sb.AppendLine($"  {kv.Key} (×{kv.Value})");
        }

        if (counts.Count > max)
        {
            sb.AppendLine($"  … {counts.Count - max} more (raise max).");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Transitive call trace in ONE call: follows the callee chain (downstream execution flow) or the caller chain
    /// (upstream impact) up to <paramref name="maxDepth"/> levels, so an agent needn't hand-run call_hierarchy per
    /// node — each such hop is a turn that re-sends the whole transcript. Nodes are "Type.Method", bound through
    /// <see cref="CallResolver"/>; a call through an interface/abstract member descends into its implementations.
    /// Unresolved (name-only) and external nodes are shown as leaves and never descended, so the tree never follows
    /// a guessed edge. 'callees' parses only the methods on the traced paths (memoized); 'callers' inverts the
    /// graph with one parse pass over the (project-scoped) files, so scope it on large repos. Cycles and repeats
    /// are printed once as "(↑ already traced)"; the walk is bounded by depth and maxNodes.
    /// </summary>
    public static string Trace(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string method, string direction, string? project, int maxDepth, int maxNodes, bool includeBodies = true)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "trace_calls: provide a method name.";
        }

        bool callees = !string.Equals((direction ?? "callees").Trim(), "callers", StringComparison.OrdinalIgnoreCase);
        (string? typeFilter, string name) = Split(method);
        List<SourceFileIndex> candidates = Candidates(files, project);
        HashSet<string> inScope = new(candidates.Select(f => f.SourceFilePath), StringComparer.OrdinalIgnoreCase);
        CallResolver resolver = new(files);
        FileReader reader = new(fileSystem);

        List<string> roots = (typeFilter is null ? resolver.Definers(name).ToList() : RootKeys(resolver, name, typeFilter))
            .Where(k => resolver.Site(k, name) is { } s && inScope.Contains(s.File.SourceFilePath))
            .ToList();
        if (roots.Count == 0)
        {
            return callees
                ? $"trace_calls(callees): no C# method named '{method}' is indexed{Scope(project)}."
                : $"trace_calls(callers): no invocations of '{method}' found{Scope(project)}.";
        }

        Func<TraceNode, List<TraceNode>> childrenOf = callees
            ? CalleeWalker(resolver, reader)
            : CallerWalker(resolver, reader, candidates, XamlWirings(resolver, files, project, null).ToList());

        StringBuilder sb = new();
        sb.AppendLine($"trace_calls: {(callees ? "callees" : "callers")} of {method} (depth ≤{maxDepth}, ≤{maxNodes} nodes){Scope(project)}");
        sb.AppendLine(includeBodies
            ? "Each resolved node shows [file:line] + signature; the FULL BODY of every traced method is inlined under '## Bodies' below. You already have the source — do NOT open these files with get_symbol_source / Read."
            : "Each resolved node shows its [file:line] and signature — this is authoritative; do NOT open these files to verify. Call get_symbol_source(member='Name') only for a body you must actually read.");
        sb.AppendLine("Nodes marked '?' were matched by NAME only (receiver type not determined) and are not followed — verify those if they matter.");
        if (roots.Count > 1)
        {
            sb.AppendLine($"{roots.Count} types declare '{name}' — each is traced below; pass Type.{name} to trace one.");
        }

        sb.AppendLine();
        HashSet<string> visited = new(StringComparer.Ordinal);
        List<TraceNode> emittedDefined = new();
        int nodeCount = 0;
        bool truncated = false;

        void Walk(TraceNode node, int depth)
        {
            if (depth > maxDepth)
            {
                return;
            }

            List<TraceNode> children = childrenOf(node);
            if (children.Count == 0)
            {
                return;
            }

            // Budget spent before we could show this node's children — flag it (a leaf never reaches here).
            if (nodeCount >= maxNodes)
            {
                truncated = true;
                return;
            }

            string indent = new(' ', depth * 2);
            foreach (TraceNode child in children)
            {
                if (nodeCount >= maxNodes)
                {
                    truncated = true;
                    return;
                }

                string text = Describe(child, callees);
                if (!visited.Add(child.Id))
                {
                    sb.AppendLine($"{indent}{text} (↑ already traced)");
                    nodeCount++;
                    continue;
                }

                sb.AppendLine($"{indent}{text}");
                nodeCount++;
                if (child.Site is null)
                {
                    continue;
                }

                emittedDefined.Add(child);
                // Outside the project scope: shown (with its location) but not descended.
                if (child.Kind == NodeKind.Defined && inScope.Contains(child.Site.File.SourceFilePath))
                {
                    Walk(child, depth + 1);
                }
            }
        }

        for (int i = 0; i < roots.Count; i++)
        {
            TraceNode root = TraceNode.Defined(resolver, roots[i], name);
            if (i > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine(Describe(root, callees));
            visited.Add(root.Id);
            emittedDefined.Add(root);
            Walk(root, 1);
        }

        if (truncated)
        {
            sb.AppendLine($"\n… trace truncated at {nodeCount} nodes (lower depth, scope with project=, or raise maxNodes).");
        }

        if (includeBodies && emittedDefined.Count > 0)
        {
            AppendBodies(sb, reader, emittedDefined);
        }

        return sb.ToString();
    }

    // ── Trace graph ──────────────────────────────────────────────────────────────────────────────────────

    private enum NodeKind
    {
        Defined,
        NonMethodCaller,
        Unresolved,
        External,
    }

    /// <summary>A trace node. <see cref="Id"/> is unique per binding ("Ns.Type.Method", "?Name", "!Name").</summary>
    private sealed record TraceNode(NodeKind Kind, string Id, string Label, CallResolver.MethodSite? Site, string? Note)
    {
        public static TraceNode Defined(CallResolver resolver, string typeKey, string method, string? note = null) =>
            new(NodeKind.Defined, $"{typeKey}.{method}", $"{resolver.TypeName(typeKey)}.{method}", resolver.Site(typeKey, method), note);
    }

    private static string Describe(TraceNode node, bool callees)
    {
        string note = node.Note is null ? string.Empty : $"  ({node.Note})";
        if (node.Site is not null)
        {
            string sig = string.IsNullOrEmpty(node.Site.Member.Signature) ? string.Empty : $"  {node.Site.Member.Signature}";
            return $"{node.Label}  [{node.Site.File.FileName}:{node.Site.Member.StartLine}]{sig}{note}";
        }

        return node.Kind == NodeKind.External && callees ? $"{node.Label}  (external / not indexed)" : $"{node.Label}{note}";
    }

    private static string UnresolvedNote(CallResolver resolver, IReadOnlyList<string> candidates, string name)
    {
        string list = string.Join(", ", candidates.Take(3).Select(c => $"{resolver.TypeName(c)}.{name}"));
        return $"? receiver type not determined — {candidates.Count} same-named: {list}{(candidates.Count > 3 ? ", …" : string.Empty)}";
    }

    private static Func<TraceNode, List<TraceNode>> CalleeWalker(CallResolver resolver, FileReader reader)
    {
        Dictionary<string, List<TraceNode>> cache = new(StringComparer.Ordinal);
        return node =>
        {
            if (node.Site is null)
            {
                return [];
            }

            if (cache.TryGetValue(node.Id, out List<TraceNode>? cached))
            {
                return cached;
            }

            string typeKey = node.Site.TypeKey;
            string method = node.Site.Member.Name;
            Dictionary<string, TraceNode> children = new(StringComparer.Ordinal);
            bool hasBody = false;
            foreach (SourceFileIndex f in resolver.DeclaringFiles(typeKey).Where(f => f.Language == Language.CSharp))
            {
                if (ParseFile(reader, f) is not (CompilationUnitSyntax root, _))
                {
                    continue;
                }

                CallResolver.FileScope scope = new(f);
                foreach (MethodDeclarationSyntax decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(d => d.Identifier.Text == method && CallResolver.EnclosingTypeKey(d) == typeKey))
                {
                    hasBody |= decl.Body is not null || decl.ExpressionBody is not null;
                    foreach (InvocationExpressionSyntax inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        CallResolution r = resolver.Resolve(inv, scope);
                        if (r.Name.Length == 0)
                        {
                            continue;
                        }

                        foreach (TraceNode child in Bind(resolver, r))
                        {
                            children.TryAdd(child.Id, child);
                        }
                    }
                }
            }

            // Interface / abstract member: the reachable bodies are its implementations and overrides.
            if (!hasBody)
            {
                foreach (string impl in resolver.Overriders(typeKey, method))
                {
                    TraceNode child = TraceNode.Defined(resolver, impl, method, $"implements {resolver.TypeName(typeKey)}.{method}");
                    children.TryAdd(child.Id, child);
                }
            }

            List<TraceNode> result = children.Values.OrderBy(c => c.Label, StringComparer.Ordinal).ToList();
            cache[node.Id] = result;
            return result;
        };
    }

    private static IEnumerable<TraceNode> Bind(CallResolver resolver, CallResolution r)
    {
        switch (r.Kind)
        {
            case CallResolutionKind.Resolved:
                foreach (string t in r.Targets)
                {
                    yield return TraceNode.Defined(resolver, t, r.Name);
                }

                break;
            case CallResolutionKind.Unresolved:
                yield return new TraceNode(NodeKind.Unresolved, "?" + r.Name, r.Name, null, UnresolvedNote(resolver, r.Targets, r.Name));
                break;
            default:
                yield return new TraceNode(NodeKind.External, "!" + r.Name, r.Name, null, null);
                break;
        }
    }

    private readonly record struct CallEdge(CallResolution Resolution, string? CallerType, string CallerMember);

    private static Func<TraceNode, List<TraceNode>> CallerWalker(CallResolver resolver, FileReader reader, List<SourceFileIndex> candidates, List<XamlWiring> wirings)
    {
        Dictionary<string, List<XamlWiring>> wiringsByName = wirings
            .GroupBy(w => w.Hook.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Inverse graph — one parse pass: invoked method name -> (binding, enclosing caller).
        ConcurrentBag<CallEdge> edges = new();
        Parallel.ForEach(candidates, f =>
        {
            if (ParseFile(reader, f) is not (CompilationUnitSyntax root, _))
            {
                return;
            }

            CallResolver.FileScope scope = new(f);
            foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? name = CallResolver.InvokedName(inv);
                if (name is null || !resolver.IsIndexedMethod(name))
                {
                    continue;
                }

                CallResolution r = resolver.Resolve(inv, scope);
                if (r.Kind != CallResolutionKind.External)
                {
                    edges.Add(new CallEdge(r, CallResolver.EnclosingTypeKey(inv), EnclosingMember(inv)));
                }
            }
        });

        Dictionary<string, List<CallEdge>> byName = edges
            .GroupBy(e => e.Resolution.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return node =>
        {
            if (node.Site is null)
            {
                return [];
            }

            string typeKey = node.Site.TypeKey;
            Dictionary<string, TraceNode> callers = new(StringComparer.Ordinal);
            foreach (XamlWiring w in wiringsByName.GetValueOrDefault(node.Site.Member.Name) ?? [])
            {
                if (w.Targets.Any(t => resolver.IsOrDerivesFrom(typeKey, t)))
                {
                    TraceNode xaml = new(NodeKind.NonMethodCaller, $"xaml:{w.File.SourceFilePath}:{w.Hook.Line}",
                        $"{w.File.FileName}:{w.Hook.Line}  <{w.Hook.Element} {w.Hook.Attribute}=\"{w.Hook.Member}\">", null, "XAML wiring");
                    callers.TryAdd(xaml.Id, xaml);
                }
            }

            foreach (CallEdge e in byName.GetValueOrDefault(node.Site.Member.Name) ?? [])
            {
                string? via = e.Resolution.Targets.FirstOrDefault(t => resolver.IsOrDerivesFrom(typeKey, t));
                if (via is null)
                {
                    continue;
                }

                string label = e.CallerType is null ? e.CallerMember : $"{resolver.TypeName(e.CallerType)}.{e.CallerMember}";
                string? viaNote = via == typeKey ? null : $"via {resolver.TypeName(via)}.{e.Resolution.Name}";
                TraceNode caller;
                if (e.Resolution.Kind == CallResolutionKind.Unresolved)
                {
                    caller = new TraceNode(NodeKind.Unresolved, $"?{e.CallerType}.{e.CallerMember}", label, null, "? receiver type not determined — name match only");
                }
                else if (e.CallerType is not null && resolver.Site(e.CallerType, e.CallerMember) is not null)
                {
                    caller = TraceNode.Defined(resolver, e.CallerType, e.CallerMember, viaNote);
                }
                else
                {
                    caller = new TraceNode(NodeKind.NonMethodCaller, $"{e.CallerType}.{e.CallerMember}", label, null, viaNote);
                }

                callers.TryAdd(caller.Id, caller);
            }

            return callers.Values.OrderBy(c => c.Kind == NodeKind.Unresolved).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();
        };
    }

    // Bodies: inline the FULL source of every traced (defined) node in this SAME response, so there is nothing left
    // to open. Bounded: per-node line cap + an overall response-token budget; when the budget is hit the remaining
    // nodes stay as [file:line] pointers in the tree above. Deduped (each node inlined once, in first-seen order).
    private static void AppendBodies(StringBuilder sb, FileReader reader, List<TraceNode> nodes)
    {
        const int PerNodeMax = 40;
        int budget = Output.MaxResponseTokens - 1500;
        List<TraceNode> distinct = nodes.Where(n => n.Site is not null).DistinctBy(n => n.Id, StringComparer.Ordinal).ToList();
        sb.AppendLine();
        sb.AppendLine("## Bodies (full source of every traced node — inlined here; do NOT open these files)");
        int inlined = 0;
        foreach (TraceNode node in distinct)
        {
            if (Output.EstimateTokens(sb.ToString()) > budget)
            {
                break;
            }

            CallResolver.MethodSite site = node.Site!;
            FileReader.ReadResult read = reader.ReadFileLines(site.File.SourceFilePath);
            if (!read.Success)
            {
                continue;
            }

            string[] lines = read.Lines!;
            int s = Math.Max(0, site.Member.StartLine - 1);
            if (s >= lines.Length)
            {
                continue;
            }

            int want = site.Member.LineCount > 0 ? site.Member.LineCount : PerNodeMax;
            int shown = Math.Min(Math.Min(want, PerNodeMax), lines.Length - s);
            sb.AppendLine();
            sb.AppendLine($"### {node.Label}  {site.Member.Signature}  [{site.File.FileName}:{site.Member.StartLine}]");
            for (int i = s; i < s + shown; i++)
            {
                sb.AppendLine($"{i + 1,6}| {lines[i]}");
            }

            if (want > PerNodeMax)
            {
                sb.AppendLine($"       … {want - PerNodeMax} more line(s) — get_symbol_source(member='{site.Member.Name}') only if you truly need the rest.");
            }

            inlined++;
        }

        if (inlined < distinct.Count)
        {
            sb.AppendLine();
            sb.AppendLine($"_(inlined {inlined} of {distinct.Count} node bodies; the rest are above as [file:line] — response budget reached.)_");
        }
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────────────────────────

    private readonly record struct CallerHit(string Project, string Path, string FileName, int Line, string Caller, string Snippet, bool Possible);

    private static string RenderCallers(string method, ConcurrentBag<CallerHit> bag, string? project, int max, int perFileCap, string note)
    {
        int total = bag.Count;
        if (total == 0)
        {
            return $"call_hierarchy(callers): no invocations of '{method}' found{Scope(project)}. (Receiver-resolved where possible; calls that bind to framework/library types are not counted.)";
        }

        List<(string FileName, string Project, int Count, List<CallerHit> Hits)> byFile = bag
            .GroupBy(h => h.Path)
            .Select(g => (g.First().FileName, g.First().Project, Count: g.Count(), Hits: g.OrderBy(h => h.Line).ToList()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int possible = bag.Count(h => h.Possible);
        StringBuilder sb = new();
        sb.AppendLine($"{method}: {total} call sites in {byFile.Count} files{Scope(project)}"
            + (possible > 0 ? $" ({total - possible} resolved, {possible} unresolved '?')" : string.Empty));
        if (possible > 0)
        {
            sb.AppendLine(UnresolvedLegend);
        }

        if (note.Length > 0)
        {
            sb.AppendLine(note);
        }

        sb.AppendLine();

        int emitted = 0, filesShown = 0;
        foreach ((string fileName, string proj, int count, List<CallerHit> hits) in byFile)
        {
            if (emitted >= max)
            {
                break;
            }

            filesShown++;
            sb.AppendLine($"== {fileName} ({proj}) — {count} ==");
            int perFile = 0;
            foreach (CallerHit h in hits)
            {
                if (emitted >= max || perFile >= perFileCap)
                {
                    break;
                }

                sb.AppendLine($"  {h.Line}| {(h.Possible ? "? " : string.Empty)}{h.Caller}: {h.Snippet}");
                emitted++;
                perFile++;
            }
        }

        if (filesShown < byFile.Count || emitted < total)
        {
            sb.AppendLine($"\n… showing {emitted} of {total} call sites across {filesShown}/{byFile.Count} files (raise max, or scope with project=).");
        }

        return sb.ToString();
    }

    /// <summary>"Type.Method" → (Type, Method); a bare name → (null, name). Only the last two segments count, so a
    /// namespace-qualified "Ns.Type.Method" works too.</summary>
    internal static (string? Type, string Method) Split(string method)
    {
        string m = method.Trim();
        int dot = m.LastIndexOf('.');
        if (dot <= 0 || dot == m.Length - 1)
        {
            return (null, m);
        }

        string type = m[..dot];
        int prev = type.LastIndexOf('.');
        return (prev >= 0 ? type[(prev + 1)..] : type, m[(dot + 1)..]);
    }

    private static List<string> RootKeys(CallResolver resolver, string name, string typeFilter) =>
        resolver.Definers(name).Where(k => resolver.TypeName(k) == typeFilter).ToList();

    // A call bound to `targets` reaches a root when a root is (or derives from) one of them — a call through an
    // interface or base reaches the implementation/override.
    private static bool Reaches(CallResolver resolver, List<string> roots, IReadOnlyList<string> targets) =>
        roots.Any(root => targets.Any(t => resolver.IsOrDerivesFrom(root, t)));

    private readonly record struct XamlWiring(SourceFileIndex File, XamlHook Hook, List<string> Targets);

    // XAML attributes whose value names a method on the file's x:Class type (or its bases): event wirings.
    // `name` null = all of them. An identifier-valued attribute that names no such method (an enum value) is dropped.
    private static IEnumerable<XamlWiring> XamlWirings(CallResolver resolver, IReadOnlyList<SourceFileIndex> files, string? project, string? name)
    {
        foreach (SourceFileIndex f in files)
        {
            if (f.Language != Language.Xaml || f.XamlHooks is null || f.Types.Count == 0
                || (project is not null && !f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string typeKey = CallResolver.Key(f.Types[0].Namespace, f.Types[0].Name);
            foreach (XamlHook h in f.XamlHooks)
            {
                if (name is not null && h.Member != name)
                {
                    continue;
                }

                List<string> targets = resolver.Lookup(typeKey, h.Member);
                if (targets.Count > 0)
                {
                    yield return new XamlWiring(f, h, targets);
                }
            }
        }
    }

    private static List<SourceFileIndex> Candidates(IReadOnlyList<SourceFileIndex> files, string? project) => files
        .Where(f => f.Language == Language.CSharp)
        .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
        .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
        .ToList();

    private static IEnumerable<MethodDeclarationSyntax> Declarations(CompilationUnitSyntax root, string name, string? typeFilter) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == name
            && (typeFilter is null || m.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text == typeFilter));

    private static (CompilationUnitSyntax Root, string[] Lines)? ParseFile(FileReader reader, SourceFileIndex f)
    {
        FileReader.ReadResult read = reader.ReadFileLines(f.SourceFilePath);
        if (!read.Success)
        {
            return null;
        }

        try
        {
            CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(string.Join("\n", read.Lines!), path: f.SourceFilePath).GetCompilationUnitRoot();
            return (root, read.Lines!);
        }
        catch
        {
            return null;
        }
    }

    // Nearest enclosing named member — the actual caller.
    private static string EnclosingMember(SyntaxNode node)
    {
        foreach (SyntaxNode a in node.Ancestors())
        {
            switch (a)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case LocalFunctionStatementSyntax lf: return lf.Identifier.Text;
                case ConstructorDeclarationSyntax c: return $"{c.Identifier.Text}(ctor)";
                case PropertyDeclarationSyntax p: return $"{p.Identifier.Text}(property)";
                case AccessorDeclarationSyntax acc when acc.Parent?.Parent is PropertyDeclarationSyntax pp: return $"{pp.Identifier.Text}.{acc.Keyword.Text}";
            }
        }

        return "(file-level)";
    }

    private static string Scope(string? project) => project is null ? string.Empty : $" in project '{project}'";
}
