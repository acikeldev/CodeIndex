using System.Collections.Concurrent;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Aider-style repo map: ranks the most important symbols in the index via personalized PageRank over a
/// file→file MENTION graph, then renders a token-budgeted, elided overview for fast agent orientation.
///
/// The mention graph is the crux: CodeIndex stores DEFINITIONS only, so we derive a reference signal by scanning
/// each (non-generated) file's text once and matching identifier tokens against the set of KNOWN defined symbol
/// names — an edge X→Y means file X references (in CODE, not in a comment or string literal) a symbol defined in
/// file Y. Matching only known names (not arbitrary identifiers), skipping comments/strings, plus IDF weighting, a
/// min-length filter and a ubiquity cutoff keeps common names (Id, Name, …) and prose mentions from turning the
/// graph into noise. Built lazily on the immutable snapshot (like the project dependency graph),
/// so it costs nothing until repo_map is first called and rebuilds only when the snapshot changes.
/// </summary>
internal sealed class RepoMap
{
    // A definition site: which file defines a symbol, its display, and an importance weight (types over members).
    private readonly record struct Def(int FileIndex, string Name, string Kind, string Signature, int StartLine, int LineCount, double Weight, bool IsType);

    private readonly IReadOnlyList<SourceFileIndex> _files;   // non-generated files only (graph nodes)
    private readonly List<Def>[] _defsByFile;                 // per node: its definitions
    private readonly Dictionary<int, double>[] _outEdges;     // per node: targetNode -> weight
    private readonly Dictionary<string, int> _fileNameToNode; // filename/path -> node index (focus resolution)
    private readonly Dictionary<string, List<int>> _nameToNodes; // symbol name -> nodes defining it (focus resolution)

    private RepoMap(
        IReadOnlyList<SourceFileIndex> files,
        List<Def>[] defsByFile,
        Dictionary<int, double>[] outEdges,
        Dictionary<string, int> fileNameToNode,
        Dictionary<string, List<int>> nameToNodes)
    {
        _files = files;
        _defsByFile = defsByFile;
        _outEdges = outEdges;
        _fileNameToNode = fileNameToNode;
        _nameToNodes = nameToNodes;
    }

    private const int MinNameLength = 3;          // shorter names (Id, X, io) are too noisy to be graph signal
    private const double UbiquityCutoff = 0.10;   // a name mentioned in >10% of files carries ~no locating signal
    private const double Damping = 0.85;
    private const int MaxIterations = 40;
    private const double ConvergenceL1 = 1e-6;

    /// <summary>Build the mention graph from a snapshot's files (generated files excluded — repo_map is about
    /// hand-written code for orientation). Reads every non-generated file once (parallel); a few seconds cold,
    /// cached by the snapshot's Lazy.</summary>
    public static RepoMap Build(IReadOnlyList<SourceFileIndex> allFiles, IFileSystem fileSystem)
    {
        List<SourceFileIndex> files = allFiles
            .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
            .ToList();
        int n = files.Count;

        // name -> nodes that DEFINE it. Two maps: `nameToNodes` (ALL symbols) backs focus resolution; the mention
        // GRAPH is built only from TYPE names (`typeNameToNodes`) — method/property names like Get/Save/Handle are
        // hopelessly common and would connect everything to everything.
        Dictionary<string, List<int>> nameToNodes = new(StringComparer.Ordinal);
        Dictionary<string, List<int>> typeNameToNodes = new(StringComparer.Ordinal);
        List<Def>[] defsByFile = new List<Def>[n];
        Dictionary<string, int> fileNameToNode = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            SourceFileIndex f = files[i];
            fileNameToNode[f.FileName] = i;         // last-wins on duplicate filenames — acceptable for focus hints
            fileNameToNode[f.SourceFilePath] = i;
            List<Def> defs = new(f.Types.Count);
            foreach (TypeInfo t in f.Types)
            {
                AddDef(nameToNodes, i, t.Name);
                AddDef(typeNameToNodes, i, t.Name);
                defs.Add(new Def(i, t.Name, t.Kind.ToString(), $"{t.TypeKeyword} {t.Name}", t.StartLine, t.LineCount, TypeWeight(t.Kind), IsType: true));
                foreach (MemberInfo m in t.Members)
                {
                    AddDef(nameToNodes, i, m.Name);
                    defs.Add(new Def(i, m.Name, m.Kind.ToString(), m.Signature, m.StartLine, m.LineCount, MemberWeight(m.Kind), IsType: false));
                }
            }
            defsByFile[i] = defs;
        }

        // Document frequency per name (how many files mention it) → IDF + ubiquity filter. Computed from the same
        // parallel scan below; here we hold the mention accumulator.
        Dictionary<string, int>[] mentionsByNode = new Dictionary<string, int>[n];
        ConcurrentDictionary<string, int> docFreq = new(StringComparer.Ordinal);
        // Only PascalCase type names are edge targets: real C#/TS types are PascalCase, whereas the noisy collisions
        // are lowercase/camelCase (generated SOAP DTOs `class customer`/`order` colliding with the everyday variables
        // `customer`/`order`). This one filter removes the biggest garbage-edge source seen on the real repo.
        HashSet<string> known = new(typeNameToNodes.Keys.Where(k => char.IsUpper(k[0])), StringComparer.Ordinal);

        FileReader reader = new(fileSystem);
        Parallel.For(0, n, i =>
        {
            FileReader.ReadResult read = reader.ReadFileLines(files[i].SourceFilePath);
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            if (read.Success)
            {
                foreach (string line in read.Lines!)
                {
                    CountIdentifierMentions(line, known, counts);
                }
            }
            mentionsByNode[i] = counts;
            foreach (string name in counts.Keys)
            {
                docFreq.AddOrUpdate(name, 1, (_, v) => v + 1);
            }
        });

        // Keep only names that are locating (long enough, not ubiquitous). IDF smoothed and always positive. The
        // ubiquity cap has an absolute FLOOR so it only bites in large repos — on a small repo n*cutoff would
        // collapse toward 1 and wrongly drop every shared name, flattening PageRank to uniform.
        int ubiquityMax = Math.Max(50, (int)(n * UbiquityCutoff));
        Dictionary<string, double> idf = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> kv in docFreq)
        {
            if (kv.Key.Length < MinNameLength || kv.Value > ubiquityMax)
            {
                continue;
            }
            idf[kv.Key] = Math.Log((n + 1.0) / (kv.Value + 1.0)) + 1.0;
        }

        // Edges: file X mentioning name S (defined in nodes D, excluding X) contributes weight
        // sqrt(mentions(X,S)) * idf(S) / |D| to each edge X→d. The sqrt DAMPENS repetition (Aider does the same):
        // a file that names a type 50× isn't 50× more related to it than one that names it once — the relationship
        // exists, its strength shouldn't scale linearly with how chatty the referring file is. This keeps one
        // loop-heavy file from dominating the graph while breadth (many distinct referrers) still accrues fully.
        Dictionary<int, double>[] outEdges = new Dictionary<int, double>[n];
        for (int i = 0; i < n; i++)
        {
            Dictionary<int, double> edges = new();
            foreach (KeyValuePair<string, int> mention in mentionsByNode[i])
            {
                if (!idf.TryGetValue(mention.Key, out double w) || !typeNameToNodes.TryGetValue(mention.Key, out List<int>? definers))
                {
                    continue;
                }
                double share = Math.Sqrt(mention.Value) * w / definers.Count;
                foreach (int d in definers)
                {
                    if (d == i)
                    {
                        continue; // no self-edge
                    }
                    edges[d] = edges.GetValueOrDefault(d) + share;
                }
            }
            outEdges[i] = edges;
        }

        return new RepoMap(files, defsByFile, outEdges, fileNameToNode, nameToNodes);
    }

    /// <summary>A ranked symbol row for rendering.</summary>
    public readonly record struct RankedSymbol(string FileName, string ProjectName, string SourceFilePath, string Kind, string Signature, int StartLine, int LineCount, double Score);

    /// <summary>Rank symbols by personalized PageRank. <paramref name="focus"/> tokens (file or symbol names)
    /// concentrate the teleport vector for task-relevant ranking; empty focus → uniform (global importance).
    /// <paramref name="projectFilter"/> keeps only symbols from that project in the OUTPUT (the whole graph still
    /// informs ranking).</summary>
    public List<RankedSymbol> Rank(IReadOnlyList<string> focus, string? projectFilter)
    {
        int n = _files.Count;
        if (n == 0)
        {
            return [];
        }

        double[] teleport = BuildTeleport(focus, n, out bool personalized);
        // When focus is set, weaken damping so ~half the rank flows from the focus teleport each step — otherwise
        // (at 0.85) a dense global cluster drowns out the focus neighborhood and personalization barely moves.
        double[] rank = PageRank(teleport, personalized ? 0.5 : Damping);

        // Distribute each file's rank across its definitions (weighted by symbol importance), then flatten + sort.
        List<RankedSymbol> ranked = new();
        for (int i = 0; i < n; i++)
        {
            List<Def> defs = _defsByFile[i];
            if (defs.Count == 0)
            {
                continue;
            }
            double totalW = 0;
            foreach (Def d in defs)
            {
                totalW += d.Weight;
            }
            if (totalW <= 0)
            {
                continue;
            }

            SourceFileIndex f = _files[i];
            if (projectFilter is not null && !f.ProjectName.Equals(projectFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Def d in defs)
            {
                ranked.Add(new RankedSymbol(f.FileName, f.ProjectName, f.SourceFilePath, d.Kind, d.Signature, d.StartLine, d.LineCount, rank[i] * d.Weight / totalW));
            }
        }

        ranked.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0)
            {
                return c;
            }
            c = string.Compare(a.SourceFilePath, b.SourceFilePath, StringComparison.Ordinal);
            if (c != 0)
            {
                return c;
            }
            return a.StartLine.CompareTo(b.StartLine);
        });
        return ranked;
    }

    private double[] BuildTeleport(IReadOnlyList<string> focus, int n, out bool personalized)
    {
        double[] teleport = new double[n];
        HashSet<int> focusNodes = new();
        if (focus is { Count: > 0 })
        {
            foreach (string raw in focus)
            {
                string token = raw.Trim();
                if (token.Length == 0)
                {
                    continue;
                }
                if (_fileNameToNode.TryGetValue(token, out int fileNode))
                {
                    focusNodes.Add(fileNode);
                }
                if (_nameToNodes.TryGetValue(token, out List<int>? symNodes))
                {
                    foreach (int s in symNodes)
                    {
                        focusNodes.Add(s);
                    }
                }
            }
        }

        personalized = focusNodes.Count > 0;
        if (personalized)
        {
            double p = 1.0 / focusNodes.Count;
            foreach (int node in focusNodes)
            {
                teleport[node] = p;
            }
        }
        else
        {
            double p = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                teleport[i] = p;
            }
        }
        return teleport;
    }

    // Deterministic power iteration. Dangling nodes (no out-edges) redistribute their mass via the teleport vector.
    private double[] PageRank(double[] teleport, double damping)
    {
        int n = _files.Count;
        double[] rank = new double[n];
        Array.Copy(teleport, rank, n);

        // Pre-normalize out-edge weights per node.
        double[] outSum = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            foreach (double w in _outEdges[i].Values)
            {
                s += w;
            }
            outSum[i] = s;
        }

        double[] next = new double[n];
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            double danglingMass = 0;
            for (int i = 0; i < n; i++)
            {
                if (outSum[i] <= 0)
                {
                    danglingMass += rank[i];
                }
                next[i] = (1 - damping) * teleport[i];
            }

            // Damped dangling mass flows through the teleport distribution (keeps the vector stochastic).
            double danglingShare = damping * danglingMass;
            for (int i = 0; i < n; i++)
            {
                next[i] += danglingShare * teleport[i];
            }

            for (int i = 0; i < n; i++)
            {
                if (outSum[i] <= 0)
                {
                    continue;
                }
                double factor = damping * rank[i] / outSum[i];
                foreach (KeyValuePair<int, double> e in _outEdges[i])
                {
                    next[e.Key] += factor * e.Value;
                }
            }

            double delta = 0;
            for (int i = 0; i < n; i++)
            {
                delta += Math.Abs(next[i] - rank[i]);
                rank[i] = next[i];
            }
            if (delta < ConvergenceL1)
            {
                break;
            }
        }
        return rank;
    }

    // Reserve for the trailing footer line so the body binary-search leaves room for it under the budget.
    private const int FooterHeadroom = 40;

    /// <summary>Render the top-ranked symbols grouped by file, elided to signatures, filling up to
    /// <paramref name="tokenBudget"/> (estimated via <see cref="Output.EstimateTokens"/>).</summary>
    public static string Render(List<RankedSymbol> ranked, int tokenBudget)
    {
        if (ranked.Count == 0)
        {
            return "repo_map: no hand-written symbols indexed (empty index or all generated).";
        }

        int budget = Math.Clamp(tokenBudget, 50, Output.MaxResponseTokens);

        // Binary-search the largest top-K prefix whose rendering fits the budget. The token cost is monotonic in K,
        // so this packs the budget tighter than a greedy first-overflow break — and evaluates the estimate O(log n)
        // times instead of re-estimating the whole buffer on every symbol (Aider budgets its repo map the same way).
        int lo = 0, hi = ranked.Count;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Output.EstimateTokens(RenderPrefix(ranked, mid)) <= budget - FooterHeadroom)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        int shown = Math.Max(1, lo); // always show at least the top symbol, even on a tiny budget
        StringBuilder sb = new(RenderPrefix(ranked, shown));
        sb.AppendLine();
        sb.AppendLine(shown < ranked.Count
            ? $"… showing top {shown} of {ranked.Count} ranked symbols (raise tokenBudget, or pass focus=/project= to narrow)."
            : $"{shown} symbols.");
        return sb.ToString();
    }

    // The top <paramref name="count"/> ranked symbols grouped by file (signatures only). Deterministic and
    // idempotent so the budget binary-search can call it repeatedly.
    private static string RenderPrefix(List<RankedSymbol> ranked, int count)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Repo map — most important symbols (PageRank-ranked, token-budgeted)");
        sb.AppendLine();

        string? currentFile = null;
        for (int i = 0; i < count; i++)
        {
            RankedSymbol r = ranked[i];
            if (r.SourceFilePath != currentFile)
            {
                sb.Append($"\n## {r.FileName} ({r.ProjectName})\n");
                currentFile = r.SourceFilePath;
            }

            sb.Append($"  {r.Kind} {Output.ClipLine(r.Signature, max: 160)} [{r.StartLine}]\n");
        }

        return sb.ToString();
    }

    private static void AddDef(Dictionary<string, List<int>> nameToNodes, int node, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }
        if (!nameToNodes.TryGetValue(name, out List<int>? list))
        {
            list = [];
            nameToNodes[name] = list;
        }
        if (list.Count == 0 || list[^1] != node) // dedupe consecutive (a file defining the same name twice)
        {
            list.Add(node);
        }
    }

    private static double TypeWeight(SymbolKind kind) => kind switch
    {
        SymbolKind.Interface => 2.5,   // interfaces are high-value orientation anchors
        SymbolKind.Class or SymbolKind.AbstractClass or SymbolKind.SealedClass or SymbolKind.StaticClass => 2.0,
        SymbolKind.Record or SymbolKind.Struct or SymbolKind.RecordStruct => 1.8,
        SymbolKind.Enum => 1.5,
        _ => 1.5,
    };

    private static double MemberWeight(SymbolKind kind) => kind switch
    {
        SymbolKind.Method or SymbolKind.Constructor => 1.0,
        SymbolKind.Property => 0.6,
        _ => 0.4,
    };

    // Extract identifier tokens from a line and tally those that are known defined symbol names — but only where
    // they are CODE, not text. A type named in a `//` comment or inside a "string literal" is not a reference and
    // must not create a graph edge (that was the biggest false-edge source: type names in comments and in SQL/log
    // strings). Manual char scan (faster than regex at this volume); an identifier is [A-Za-z_][A-Za-z0-9_]*.
    // Conservative: interpolation holes inside $"…{X}…" are treated as string (X under-counted, not over-counted),
    // and verbatim/raw strings aren't fully modelled — acceptable for a ranking heuristic, same caveat as
    // find_references' string/comment classifier.
    private static void CountIdentifierMentions(string line, HashSet<string> known, Dictionary<string, int> counts)
    {
        int i = 0, len = line.Length;
        bool inString = false;
        while (i < len)
        {
            char c = line[i];

            if (inString)
            {
                if (c == '\\')
                {
                    i += 2;   // skip the escaped char (\" \\ etc.)
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            // A '//' outside a string starts a line comment — the rest of the line is not code.
            if (c == '/' && i + 1 < len && line[i + 1] == '/')
            {
                return;
            }

            if (c == '"')
            {
                inString = true;
                i++;
                continue;
            }

            // Skip a char literal so a '"' inside it can't open a phantom string.
            if (c == '\'')
            {
                i++;
                while (i < len && line[i] != '\'')
                {
                    if (line[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                i++;   // past the closing '
                continue;
            }

            if (c == '_' || char.IsLetter(c))
            {
                int start = i;
                i++;
                while (i < len && (line[i] == '_' || char.IsLetterOrDigit(line[i])))
                {
                    i++;
                }

                string token = line[start..i];
                if (token.Length >= MinNameLength && known.Contains(token))
                {
                    counts[token] = counts.GetValueOrDefault(token) + 1;
                }

                continue;
            }

            i++;
        }
    }
}
