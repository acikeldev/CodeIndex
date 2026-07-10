using CodeIndex.Abstractions;
using TreeSitter;
using TSLanguage = TreeSitter.Language;

namespace CodeIndex.Internal;

/// <summary>
/// Syntactic import/reference analysis for a single TypeScript/TSX file (tree-sitter), for check_dangling_references:
/// extracts the file's import BINDINGS, its local DECLARATIONS, and its USED identifiers. The store combines these
/// with the symbol index to report (1) UNUSED imports (imported but never referenced — reliable) and (2) DANGLING
/// references (a used PascalCase identifier that is neither imported nor declared here — flagged ONLY when it is a
/// real project symbol defined in another indexed TS file, which keeps it high-precision + actionable). Catches the
/// post-merge build-break where an import was dropped but a use of it remained.
/// </summary>
internal static class TsReferenceCheck
{
    private static readonly Lazy<TSLanguage> TsLang = new(() => new TSLanguage("tree-sitter-typescript", "tree_sitter_typescript"));
    private static readonly Lazy<TSLanguage> TsxLang = new(() => new TSLanguage("tree-sitter-tsx", "tree_sitter_tsx"));

    internal readonly record struct ImportBinding(string Name, string Module, int Line);

    internal sealed class Result
    {
        public List<ImportBinding> Imports { get; } = [];
        public HashSet<string> Declared { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Used { get; } = new(StringComparer.Ordinal); // name -> first-use line (1-based)
    }

    public static Result? Analyze(IFileSystem fileSystem, string sourceFilePath)
    {
        string source;
        try
        {
            source = fileSystem.ReadAllText(sourceFilePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        TSLanguage lang = sourceFilePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? TsxLang.Value : TsLang.Value;
        using Parser parser = new(lang);
        using Tree? tree = parser.Parse(source);
        if (tree is null)
        {
            return null;
        }

        Result r = new();
        Walk(tree.RootNode, r);
        return r;
    }

    private static void Walk(Node node, Result r)
    {
        switch (node.Type)
        {
            case "import_statement":
                CollectImport(node, r);
                return; // don't descend — an import's own identifiers are bindings, not "uses"

            case "import_alias":
            {
                // TS `import X = A.B.C;` — the FIRST identifier (X) is the binding; the RHS (A.B.C) is a real use
                // of its root namespace (e.g. `Models`), which must be collected or the `import { Models }` looks unused.
                int aliasLine = (int)node.StartPosition.Row + 1;
                bool lhsTaken = false;
                foreach (Node c in node.NamedChildren)
                {
                    if (!lhsTaken && c.Type == "identifier")
                    {
                        AddImport(c, "(alias)", aliasLine, r); // register the binding
                        lhsTaken = true;
                        continue;                              // ...but don't treat the binding itself as a use
                    }
                    Walk(c, r);                                // recurse the RHS → its root identifier counts as used
                }
                return;
            }

            case "object_pattern":
            case "array_pattern":
                // Destructuring binds (`const { Foo, Bar } = props`, `({ X }) => …`) introduce local names —
                // collect them as declared (and do NOT descend, so the bound identifiers aren't counted as uses).
                CollectBindings(node, r);
                return;

            case "nested_type_identifier":
            {
                // Qualified TYPE reference `A.B.C` — only the ROOT (A, e.g. the `Models` namespace import) is a real
                // reference; the trailing type_identifier is a member of it (like the property side of `a.b`), so it
                // must NOT be collected as a bare use. This is the type-position twin of member_expression.
                Node? root = node.NamedChildren.FirstOrDefault();
                if (root is not null)
                {
                    Walk(root, r);
                }
                return;
            }

            case "class_declaration":
            case "abstract_class_declaration":
            case "interface_declaration":
            case "enum_declaration":
            case "function_declaration":
            case "generator_function_declaration":
            case "type_alias_declaration":
            case "internal_module":
            case "module":
            case "variable_declarator":
            case "type_parameter":
            case "required_parameter":
            case "optional_parameter":
                AddDeclared(node, r);
                break;
        }

        // A used reference: value identifiers + type references + object shorthand. property_identifier (the `.x`
        // side of member access, object keys, method names) is deliberately NOT collected — it's not a binding ref.
        if (node.Type is "identifier" or "type_identifier" or "shorthand_property_identifier")
        {
            string? name = SafeText(node);
            if (!string.IsNullOrEmpty(name) && !r.Used.ContainsKey(name))
            {
                r.Used[name] = (int)node.StartPosition.Row + 1;
            }
        }

        foreach (Node child in node.NamedChildren)
        {
            Walk(child, r);
        }
    }

    private static void CollectImport(Node importStmt, Result r)
    {
        int line = (int)importStmt.StartPosition.Row + 1;
        string module = importStmt.NamedChildren.FirstOrDefault(c => c.Type == "string") is { } s
            ? StripQuotes(SafeText(s) ?? string.Empty)
            : string.Empty;

        Node? clause = importStmt.NamedChildren.FirstOrDefault(c => c.Type == "import_clause");
        if (clause is null)
        {
            return; // side-effect import: `import 'x';` — no bindings
        }

        foreach (Node c in clause.NamedChildren)
        {
            switch (c.Type)
            {
                case "identifier": // default import: import D from 'x'
                    AddImport(c, module, line, r);
                    break;
                case "namespace_import": // import * as NS from 'x'
                    Node? ns = c.NamedChildren.FirstOrDefault(x => x.Type == "identifier");
                    if (ns is not null)
                    {
                        AddImport(ns, module, line, r);
                    }
                    break;
                case "named_imports": // import { A, B as C } from 'x'
                    foreach (Node spec in c.NamedChildren.Where(x => x.Type == "import_specifier"))
                    {
                        // The binding introduced is the alias if present, else the imported name.
                        Node? bind = Field(spec, "alias") ?? Field(spec, "name")
                            ?? spec.NamedChildren.LastOrDefault(x => x.Type is "identifier" or "type_identifier");
                        if (bind is not null)
                        {
                            AddImport(bind, module, line, r);
                        }
                    }
                    break;
            }
        }
    }

    private static void AddImport(Node nameNode, string module, int line, Result r)
    {
        string? name = SafeText(nameNode);
        if (!string.IsNullOrEmpty(name))
        {
            r.Imports.Add(new ImportBinding(name, module, line));
        }
    }

    // Collect binding NAMES from a destructuring pattern, without descending into default-value expressions
    // (a default's own identifiers are uses, not binds — and missing a rare pattern-default use is the safe direction).
    private static void CollectBindings(Node node, Result r)
    {
        switch (node.Type)
        {
            case "identifier":
            case "type_identifier":
            case "shorthand_property_identifier_pattern":
                string? t = SafeText(node);
                if (!string.IsNullOrEmpty(t))
                {
                    r.Declared.Add(t);
                }
                break;
            case "object_pattern":
            case "array_pattern":
            case "rest_pattern":
                foreach (Node c in node.NamedChildren)
                {
                    CollectBindings(c, r);
                }
                break;
            case "pair_pattern":
                Node? v = Field(node, "value");
                if (v is not null)
                {
                    CollectBindings(v, r);
                }
                break;
            case "object_assignment_pattern":
            case "assignment_pattern":
                Node? left = node.NamedChildren.FirstOrDefault();
                if (left is not null)
                {
                    CollectBindings(left, r); // the bind side only, not the default
                }
                break;
        }
    }

    private static void AddDeclared(Node decl, Result r)
    {
        Node? name = Field(decl, "name") ?? Field(decl, "pattern");
        if (name is not null && name.Type is "identifier" or "type_identifier" or "property_identifier")
        {
            string? text = SafeText(name);
            if (!string.IsNullOrEmpty(text))
            {
                r.Declared.Add(text);
            }
        }
    }

    private static Node? Field(Node n, string field)
    {
        try
        {
            return n.GetChildForField(field);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? SafeText(Node n)
    {
        try
        {
            return n.Text;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string StripQuotes(string s) =>
        s.Length >= 2 && (s[0] is '"' or '\'' or '`') ? s[1..^1] : s;
}
