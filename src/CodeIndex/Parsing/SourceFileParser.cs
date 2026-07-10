using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeIndex.Abstractions;
using CodeIndex.Models;

using TypeInfo = CodeIndex.Models.TypeInfo;

namespace CodeIndex.Parsing;

/// <summary>
/// Parses a single C# source file into a <see cref="SourceFileIndex"/> using Roslyn's syntax model.
/// Stateless and thread-safe apart from the injected <see cref="IFileSystem"/> reference, so the store
/// can call <see cref="Parse"/> concurrently from PLINQ.
/// </summary>
public sealed class SourceFileParser
{
    private readonly IFileSystem _fileSystem;

    public SourceFileParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public SourceFileIndex? Parse(string csFilePath, string projectName)
    {
        string sourceText;
        try
        {
            sourceText = _fileSystem.ReadAllText(csFilePath);
        }
        catch
        {
            return null;
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText, path: csFilePath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        BaseNamespaceDeclarationSyntax? ns = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        List<TypeInfo> types = new();
        ExtractTypes(root.Members, types, isNested: false, currentNamespace: null);

        // NOTE: type-less files (GlobalUsings.cs, AssemblyInfo.cs, top-level-statement Program.cs) are still
        // indexed with an empty type list — they carry usings the resolver needs and content the text/reference
        // scan tools must cover. Do not early-return on types.Count == 0.

        // Capture imports for resolve_bare_name: every using directive in the file (top-level, inside
        // namespaces, global). Aliases (`using X = A.B.C;`) win over plain usings when resolving a bare name.
        // `using static` is skipped (it imports members, not type names, so it doesn't affect bare-type binding).
        List<string> usings = new();
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        foreach (UsingDirectiveSyntax u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (!u.StaticKeyword.IsKind(SyntaxKind.None))
            {
                continue; // using static — not a type-name import
            }

            string? target = u.Name?.ToString();
            if (target is null)
            {
                continue;
            }

            if (u.Alias is not null)
            {
                aliases[u.Alias.Name.Identifier.Text] = target;
            }
            else if (!usings.Contains(target))
            {
                usings.Add(target);
            }
        }

        return new SourceFileIndex
        {
            FileName = Path.GetFileName(csFilePath),
            SourceFilePath = csFilePath,
            ProjectName = projectName,
            Namespace = ns?.Name.ToString(),
            Types = types,
            Usings = usings,
            UsingAliases = aliases
        };
    }

    private static void ExtractTypes(SyntaxList<MemberDeclarationSyntax> members, List<TypeInfo> types, bool isNested, string? currentNamespace)
    {
        foreach (MemberDeclarationSyntax member in members)
        {
            if (member is BaseNamespaceDeclarationSyntax nsDecl)
            {
                string childNs = nsDecl.Name.ToString();
                string combined = string.IsNullOrEmpty(currentNamespace) ? childNs : $"{currentNamespace}.{childNs}";
                ExtractTypes(nsDecl.Members, types, isNested, combined);
            }
            else if (member is EnumDeclarationSyntax enumDecl)
            {
                types.Add(ParseEnum(enumDecl, isNested, currentNamespace));
            }
            else if (member is TypeDeclarationSyntax typeDecl)
            {
                types.Add(ParseType(typeDecl, isNested, currentNamespace));
                // Recurse into the type body so NESTED types are indexed too (a class/struct/record can declare
                // types inside it). Nested types keep the same namespace as their container.
                ExtractTypes(typeDecl.Members, types, isNested: true, currentNamespace);
            }
        }
    }

    private static TypeInfo ParseType(TypeDeclarationSyntax type, bool isNested, string? currentNamespace)
    {
        string modifiers = GetTypeModifiers(type);
        string keyword = type.Keyword.Text;
        // A record can be `record`, `record class`, or `record struct`; surface the struct/class variant so the
        // kind is classified correctly and the displayed keyword reads naturally (e.g. "record struct").
        if (type is RecordDeclarationSyntax rec && !rec.ClassOrStructKeyword.IsKind(SyntaxKind.None))
        {
            keyword = $"record {rec.ClassOrStructKeyword.Text}";
        }

        // Keep each base/interface as its own list entry (generic args intact) — enables exact derived-type matching.
        List<string>? baseTypes = type.BaseList is not null
            ? type.BaseList.Types.Select(t => t.Type.ToString()).ToList()
            : null;

        (int startLine, int lineCount) = GetLineRange(type);

        List<MemberInfo> memberInfos = new();

        // Properties
        foreach (PropertyDeclarationSyntax prop in type.Members
            .OfType<PropertyDeclarationSyntax>()
            .OrderBy(p => p.Identifier.Text))
        {
            (int pStart, int pCount) = GetLineRange(prop);
            memberInfos.Add(new MemberInfo
            {
                Name = prop.Identifier.Text,
                Kind = Models.SymbolKind.Property,
                ReturnType = prop.Type.ToString(),
                Signature = $"{prop.Type} {prop.Identifier.Text}",
                StartLine = pStart,
                LineCount = pCount
            });
        }

        // Fields — one member per declared variable, so `int a, b;` yields two searchable members
        // (a comma-joined single name broke exact-name matching and was misleading in output).
        foreach (FieldDeclarationSyntax field in type.Members
            .OfType<FieldDeclarationSyntax>()
            .OrderBy(f => f.Declaration.Variables[0].Identifier.ValueText))
        {
            (int fStart, int fCount) = GetLineRange(field);
            string fieldType = field.Declaration.Type.ToString();
            foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
            {
                memberInfos.Add(new MemberInfo
                {
                    Name = v.Identifier.ValueText,
                    Kind = Models.SymbolKind.Field,
                    ReturnType = fieldType,
                    Signature = $"{fieldType} {v.Identifier.ValueText}",
                    StartLine = fStart,
                    LineCount = fCount
                });
            }
        }

        // Primary constructor (C# 12+: `class C(int x)`) — lives on TypeDeclarationSyntax.ParameterList
        if (type.ParameterList is not null)
        {
            (int pcStart, int pcCount) = GetLineRange(type.ParameterList);
            string paramTypes = string.Join(", ", type.ParameterList.Parameters.Select(p => p.Type?.ToString()));
            memberInfos.Add(new MemberInfo
            {
                Name = type.Identifier.Text,
                Kind = Models.SymbolKind.Constructor,
                ReturnType = type.Identifier.Text,
                Signature = $"{type.Identifier.Text}({paramTypes})",
                StartLine = pcStart,
                LineCount = pcCount
            });
        }

        // Explicit constructors
        foreach (ConstructorDeclarationSyntax ctor in type.Members
            .OfType<ConstructorDeclarationSyntax>()
            .OrderBy(c => string.Join(", ", c.ParameterList.Parameters.Select(p => p.Type?.ToString()))))
        {
            (int cStart, int cCount) = GetLineRange(ctor);
            string paramTypes = string.Join(", ", ctor.ParameterList.Parameters.Select(p => p.Type?.ToString()));
            memberInfos.Add(new MemberInfo
            {
                Name = ctor.Identifier.Text,
                Kind = Models.SymbolKind.Constructor,
                ReturnType = ctor.Identifier.Text,
                Signature = $"{ctor.Identifier.Text}({paramTypes})",
                StartLine = cStart,
                LineCount = cCount
            });
        }

        // Events — both forms: `event Foo Bar;` (EventFieldDeclaration) and `event Foo Bar { add; remove; }` (EventDeclaration)
        foreach (EventFieldDeclarationSyntax ev in type.Members
            .OfType<EventFieldDeclarationSyntax>()
            .OrderBy(e => e.Declaration.Variables[0].Identifier.ValueText))
        {
            (int eStart, int eCount) = GetLineRange(ev);
            string evType = ev.Declaration.Type.ToString();
            foreach (VariableDeclaratorSyntax v in ev.Declaration.Variables)
            {
                memberInfos.Add(new MemberInfo
                {
                    Name = v.Identifier.ValueText,
                    Kind = Models.SymbolKind.Event,
                    ReturnType = evType,
                    Signature = $"event {evType} {v.Identifier.ValueText}",
                    StartLine = eStart,
                    LineCount = eCount
                });
            }
        }

        foreach (EventDeclarationSyntax ev in type.Members
            .OfType<EventDeclarationSyntax>()
            .OrderBy(e => e.Identifier.Text))
        {
            (int eStart, int eCount) = GetLineRange(ev);
            memberInfos.Add(new MemberInfo
            {
                Name = ev.Identifier.Text,
                Kind = Models.SymbolKind.Event,
                ReturnType = ev.Type.ToString(),
                Signature = $"event {ev.Type} {ev.Identifier.Text}",
                StartLine = eStart,
                LineCount = eCount
            });
        }

        // Methods
        IOrderedEnumerable<IGrouping<string, MethodDeclarationSyntax>> methods = type.Members
            .OfType<MethodDeclarationSyntax>()
            .GroupBy(m => m.Identifier.Text)
            .OrderBy(g => g.Key);

        foreach (IGrouping<string, MethodDeclarationSyntax> group in methods)
        {
            List<MethodDeclarationSyntax> overloads = group
                .OrderBy(m => string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString())))
                .ToList();

            foreach (MethodDeclarationSyntax m in overloads)
            {
                (int mStart, int mCount) = GetLineRange(m);
                string paramTypes = string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString()));
                memberInfos.Add(new MemberInfo
                {
                    Name = m.Identifier.Text,
                    Kind = Models.SymbolKind.Method,
                    ReturnType = m.ReturnType.ToString(),
                    Signature = $"{m.ReturnType} {m.Identifier.Text}({paramTypes})",
                    StartLine = mStart,
                    LineCount = mCount
                });
            }
        }

        return new TypeInfo
        {
            Name = type.Identifier.Text,
            Kind = ClassifyType(modifiers, keyword),
            TypeKeyword = $"{modifiers}{keyword}",
            BaseTypes = baseTypes,
            StartLine = startLine,
            LineCount = lineCount,
            IsNested = isNested,
            Namespace = currentNamespace,
            Members = memberInfos
        };
    }

    private static TypeInfo ParseEnum(EnumDeclarationSyntax enumDecl, bool isNested, string? currentNamespace)
    {
        string modifiers = GetTypeModifiers(enumDecl);
        (int startLine, int lineCount) = GetLineRange(enumDecl);

        string? values = enumDecl.Members.Count > 0
            ? string.Join(", ", enumDecl.Members.Select(m => m.Identifier.Text))
            : null;

        return new TypeInfo
        {
            Name = enumDecl.Identifier.Text,
            Kind = Models.SymbolKind.Enum,
            TypeKeyword = $"{modifiers}enum",
            StartLine = startLine,
            LineCount = lineCount,
            IsNested = isNested,
            Namespace = currentNamespace,
            EnumValues = values
        };
    }

    private static (int StartLine, int LineCount) GetLineRange(SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        int start = span.StartLinePosition.Line + 1;
        int count = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
        return (start, count);
    }

    private static string GetTypeModifiers(MemberDeclarationSyntax type)
    {
        List<string> mods = new();
        foreach (SyntaxToken mod in type.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.StaticKeyword))
            {
                mods.Add("static");
            }
            else if (mod.IsKind(SyntaxKind.AbstractKeyword))
            {
                mods.Add("abstract");
            }
            else if (mod.IsKind(SyntaxKind.SealedKeyword))
            {
                mods.Add("sealed");
            }
        }

        return mods.Count > 0 ? string.Join(" ", mods) + " " : string.Empty;
    }

    private static Models.SymbolKind ClassifyType(string modifiers, string keyword)
    {
        if (keyword == "interface")
        {
            return Models.SymbolKind.Interface;
        }

        if (keyword == "enum")
        {
            return Models.SymbolKind.Enum;
        }

        if (keyword.StartsWith("record", StringComparison.Ordinal))
        {
            return keyword.Contains("struct", StringComparison.Ordinal) ? Models.SymbolKind.RecordStruct : Models.SymbolKind.Record;
        }

        if (keyword == "struct")
        {
            return Models.SymbolKind.Struct;
        }

        if (modifiers.Contains("static"))
        {
            return Models.SymbolKind.StaticClass;
        }

        if (modifiers.Contains("abstract"))
        {
            return Models.SymbolKind.AbstractClass;
        }

        if (modifiers.Contains("sealed"))
        {
            return Models.SymbolKind.SealedClass;
        }

        return Models.SymbolKind.Class;
    }
}
