using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class ResolveBareNameTool
{
    [McpServerTool(Name = "resolve_bare_name")]
    [Description("Resolve what a BARE (unqualified) type name binds to in a given file, using its usings + aliases — the fully-qualified type, or whether it is AMBIGUOUS (same short name in two imported namespaces). Syntax+index based (no semantic model). Also lists matching types NOT imported here, so you can spot a same-named type in another namespace.")]
    public static string ResolveBareName(
        ICodeIndexStore index,
        [Description("File the name is written in — filename or full path")] string file,
        [Description("Bare/unqualified identifier to resolve")] string identifier)
    {
        BareNameResolution r = index.ResolveBareName(file, identifier);

        if (!r.FileFound)
        {
            return $"File '{file}' is not indexed — cannot resolve. Try the exact file name (e.g. 'MyType.cs') or a full path."
                + NameSuggester.DidYouMean(file, index.FileNames());
        }

        StringBuilder sb = new();
        string where = $"'{identifier}' in {file}" + (r.FileNamespace is not null ? $" (namespace {r.FileNamespace})" : string.Empty);

        if (r.AliasTarget is not null)
        {
            sb.AppendLine($"RESOLVED (alias): bare {where} binds to  {r.AliasTarget}");
            sb.Append("A `using {alias} = ...;` directive makes this unambiguous.".Replace("{alias}", identifier));
            return sb.ToString();
        }

        if (r.InScope.Count == 0)
        {
            sb.AppendLine($"UNRESOLVED: bare {where} matches no imported type (not in the file's namespace or any `using`).");
        }
        else if (r.InScope.Count == 1)
        {
            BareNameResolutionLine(sb, $"RESOLVED: bare {where} binds to", r.InScope[0]);
        }
        else
        {
            sb.AppendLine($"AMBIGUOUS: bare {where} could bind to {r.InScope.Count} IMPORTED types — C# would raise CS0104 " +
                "(or silently pick by using order). Fully-qualify it. Candidates:");
            foreach (BareNameCandidate c in r.InScope)
            {
                BareNameResolutionLine(sb, "  -", c);
            }
        }

        if (r.OutOfScope.Count > 0)
        {
            sb.AppendLine($"Also defined but NOT imported here ({r.OutOfScope.Count}) — a `using` for one of these would change/ambiguate the binding:");
            foreach (BareNameCandidate c in r.OutOfScope)
            {
                sb.AppendLine($"  · {c.FullyQualified}   [{Path.GetFileName(c.SourceFilePath)}]");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void BareNameResolutionLine(StringBuilder sb, string prefix, BareNameCandidate c)
        => sb.AppendLine($"{prefix}  {c.FullyQualified}   [{Path.GetFileName(c.SourceFilePath)}]  {c.Via}");
}
