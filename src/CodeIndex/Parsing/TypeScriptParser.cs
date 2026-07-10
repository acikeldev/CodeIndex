using CodeIndex.Abstractions;
using CodeIndex.Models;
using TreeSitter;
using TypeInfo = CodeIndex.Models.TypeInfo;
// tree-sitter's grammar handle is also named `Language`, which now collides with the CodeIndex.Models.Language
// enum imported above. Alias the tree-sitter one; the enum is referenced fully-qualified below.
using TSLanguage = TreeSitter.Language;

namespace CodeIndex.Parsing;

/// <summary>
/// Parses TypeScript / TSX into the same <see cref="SourceFileIndex"/> shape the C# parser emits, so every existing
/// MCP tool works over TS symbols unchanged. Uses tree-sitter (bundled grammars, error-recovering); syntax-only,
/// like the C# side. Top-level classes/interfaces/enums/type-aliases/functions/consts become types; class and
/// interface members become members; `namespace X { }` scopes contained types via TypeInfo.Namespace.
///
/// Stateless and thread-safe apart from the injected <see cref="IFileSystem"/> reference, so the store can call
/// <see cref="Parse"/> concurrently from PLINQ. A fresh <see cref="Parser"/> is built per call (Parser is not
/// thread-safe); the native grammar handles are static, immutable, and shared.
/// </summary>
public sealed class TypeScriptParser
{
    // tree-sitter Language objects are immutable and shareable across threads/parsers; load each native grammar once.
    private static readonly Lazy<TSLanguage> TsLang = new(() => new TSLanguage("tree-sitter-typescript", "tree_sitter_typescript"));
    private static readonly Lazy<TSLanguage> TsxLang = new(() => new TSLanguage("tree-sitter-tsx", "tree_sitter_tsx"));

    private readonly IFileSystem _fileSystem;

    public TypeScriptParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public SourceFileIndex? Parse(string filePath, string projectName)
    {
        string source;
        try
        {
            source = _fileSystem.ReadAllText(filePath);
        }
        catch
        {
            return null;
        }

        TSLanguage lang = filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? TsxLang.Value : TsLang.Value;
        using Parser parser = new(lang);
        using Tree? tree = parser.Parse(source);
        if (tree is null)
        {
            return null;
        }

        List<TypeInfo> types = new();
        WalkContainer(tree.RootNode, currentNamespace: null, types);

        return new SourceFileIndex
        {
            FileName = Path.GetFileName(filePath),
            SourceFilePath = filePath,
            ProjectName = projectName,
            Namespace = null,
            Types = types,
            Language = CodeIndex.Models.Language.TypeScript,
        };
    }

    private static void WalkContainer(Node container, string? currentNamespace, List<TypeInfo> types)
    {
        foreach (Node child in container.NamedChildren)
        {
            Node decl = child.Type == "export_statement" ? InnerDeclaration(child) ?? child : child;
            HandleDeclaration(decl, currentNamespace, types);
        }
    }

    private static void HandleDeclaration(Node decl, string? ns, List<TypeInfo> types)
    {
        switch (decl.Type)
        {
            case "internal_module":
            case "module":
            {
                string? name = NameText(decl);
                string childNs = string.IsNullOrEmpty(name) ? ns ?? string.Empty
                    : string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                Node? body = FindChild(decl, "statement_block");
                if (body is not null)
                {
                    WalkContainer(body, childNs, types);
                }
                break;
            }
            case "class_declaration":
            case "abstract_class_declaration":
            {
                bool isAbstract = decl.Type == "abstract_class_declaration";
                types.Add(MakeType(decl, ns, isAbstract ? SymbolKind.AbstractClass : SymbolKind.Class,
                    isAbstract ? "abstract class" : "class", ClassMembers(FindChild(decl, "class_body"))));
                break;
            }
            case "interface_declaration":
                types.Add(MakeType(decl, ns, SymbolKind.Interface, "interface", InterfaceMembers(FindChild(decl, "interface_body"))));
                break;
            case "enum_declaration":
            {
                (int start, int count) = LineRange(decl);
                types.Add(new TypeInfo
                {
                    Name = NameText(decl) ?? "(anonymous)",
                    Kind = SymbolKind.Enum,
                    TypeKeyword = "enum",
                    StartLine = start,
                    LineCount = count,
                    Namespace = ns,
                    Members = [],
                    EnumValues = EnumValues(FindChild(decl, "enum_body")),
                });
                break;
            }
            case "type_alias_declaration":
                types.Add(MakeType(decl, ns, SymbolKind.TypeAlias, "type", []));
                break;
            case "function_declaration":
            case "generator_function_declaration":
                types.Add(MakeType(decl, ns, SymbolKind.Function, "function", []));
                break;
            case "lexical_declaration":       // const / let
            case "variable_declaration":      // var
            {
                foreach (Node v in decl.NamedChildren.Where(c => c.Type == "variable_declarator"))
                {
                    string? name = NameText(v);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }
                    (int start, int count) = LineRange(v);
                    types.Add(new TypeInfo
                    {
                        Name = name,
                        Kind = SymbolKind.Variable,
                        TypeKeyword = decl.Type == "variable_declaration" ? "var" : "const",
                        StartLine = start,
                        LineCount = count,
                        Namespace = ns,
                        Members = [],
                    });
                }
                break;
            }
        }
    }

    private static TypeInfo MakeType(Node decl, string? ns, SymbolKind kind, string keyword, List<MemberInfo> members)
    {
        string name = NameText(decl) ?? "(anonymous)";
        (int start, int count) = LineRange(decl);
        return new TypeInfo
        {
            Name = name,
            Kind = kind,
            TypeKeyword = keyword,
            StartLine = start,
            LineCount = count,
            Namespace = ns,
            Members = members,
        };
    }

    private static List<MemberInfo> ClassMembers(Node? body)
    {
        List<MemberInfo> members = new();
        if (body is null)
        {
            return members;
        }

        foreach (Node m in body.NamedChildren)
        {
            switch (m.Type)
            {
                case "method_definition":
                {
                    string name = NameText(m) ?? MemberIdentifier(m) ?? "(method)";
                    SymbolKind kind = name == "constructor" ? SymbolKind.Constructor : SymbolKind.Method;
                    members.Add(Member(name, kind, SignatureOf(m, name), m));
                    break;
                }
                case "public_field_definition":
                case "field_definition":
                {
                    string? name = NameText(m) ?? MemberIdentifier(m);
                    if (name is not null)
                    {
                        members.Add(Member(name, SymbolKind.Field, name + (FindChild(m, "type_annotation")?.Text ?? string.Empty), m));
                    }
                    break;
                }
            }
        }
        return members;
    }

    private static List<MemberInfo> InterfaceMembers(Node? body)
    {
        List<MemberInfo> members = new();
        if (body is null)
        {
            return members;
        }

        foreach (Node m in body.NamedChildren)
        {
            switch (m.Type)
            {
                case "method_signature":
                {
                    string name = NameText(m) ?? MemberIdentifier(m) ?? "(method)";
                    members.Add(Member(name, SymbolKind.Method, SignatureOf(m, name), m));
                    break;
                }
                case "property_signature":
                {
                    string? name = NameText(m) ?? MemberIdentifier(m);
                    if (name is not null)
                    {
                        members.Add(Member(name, SymbolKind.Property, name + (FindChild(m, "type_annotation")?.Text ?? string.Empty), m));
                    }
                    break;
                }
            }
        }
        return members;
    }

    private static MemberInfo Member(string name, SymbolKind kind, string signature, Node node)
    {
        (int start, int count) = LineRange(node);
        return new MemberInfo
        {
            Name = name,
            Kind = kind,
            ReturnType = FindChild(node, "type_annotation")?.Text?.TrimStart(':', ' ') ?? string.Empty,
            Signature = signature,
            StartLine = start,
            LineCount = count,
        };
    }

    // A one-line signature like "GetById(id: number): C" from the parameter list + return annotation.
    private static string SignatureOf(Node node, string name)
    {
        string parameters = FindChild(node, "formal_parameters")?.Text ?? "()";
        string ret = FindChild(node, "type_annotation")?.Text ?? string.Empty;
        return $"{name}{OneLine(parameters)}{OneLine(ret)}";
    }

    private static string? EnumValues(Node? body)
    {
        if (body is null)
        {
            return null;
        }
        List<string> names = new();
        foreach (Node m in body.NamedChildren)
        {
            if (m.Type == "enum_assignment")
            {
                string? n = NameText(m) ?? MemberIdentifier(m);
                if (n is not null)
                {
                    names.Add(n);
                }
            }
            else if (m.Type == "property_identifier")
            {
                names.Add(m.Text ?? string.Empty);
            }
        }
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static Node? InnerDeclaration(Node exportStatement)
    {
        foreach (Node c in exportStatement.NamedChildren)
        {
            if (c.Type is "class_declaration" or "abstract_class_declaration" or "interface_declaration"
                or "enum_declaration" or "type_alias_declaration" or "function_declaration"
                or "generator_function_declaration" or "lexical_declaration" or "variable_declaration"
                or "internal_module" or "module")
            {
                return c;
            }
        }
        return null;
    }

    private static string? NameText(Node n)
    {
        try
        {
            Node? name = n.GetChildForField("name");
            return name?.Text;
        }
        catch { return null; }
    }

    // Fallback: the first property_identifier/identifier child (for members where the "name" field isn't set).
    private static string? MemberIdentifier(Node n) =>
        n.NamedChildren.FirstOrDefault(c => c.Type is "property_identifier" or "identifier")?.Text;

    private static Node? FindChild(Node? n, string type) =>
        n?.NamedChildren.FirstOrDefault(c => c.Type == type);

    private static (int StartLine, int LineCount) LineRange(Node n)
    {
        int start = (int)n.StartPosition.Row + 1;
        int end = (int)n.EndPosition.Row + 1;
        return (start, Math.Max(1, end - start + 1));
    }

    private static string OneLine(string s)
    {
        int nl = s.IndexOf('\n');
        return nl < 0 ? s : s[..nl] + " …";
    }
}
