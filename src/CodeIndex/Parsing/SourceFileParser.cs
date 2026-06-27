using CodeIndex.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MemberInfo = CodeIndex.Models.MemberInfo;
using SourceFileIndex = CodeIndex.Models.SourceFileIndex;
using SymbolKind = CodeIndex.Models.SymbolKind;
using TypeInfo = CodeIndex.Models.TypeInfo;

namespace CodeIndex.Parsing;

/// <summary>
/// Parses a single C# source file using Roslyn's syntax API and returns
/// a <see cref="SourceFileIndex"/> containing all discovered types and members.
/// </summary>
public sealed class SourceFileParser
{
    private static readonly string[] SkippedFileSuffixes =
        [".Designer.cs", ".designer.cs", ".g.cs", ".g.i.cs"];

    private readonly IFileSystem _fileSystem;

    public SourceFileParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Returns true if <paramref name="filePath"/> should be parsed.
    /// Auto-generated files are skipped.
    /// </summary>
    public static bool ShouldParse(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        return !SkippedFileSuffixes.Any(suffix =>
            fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses <paramref name="filePath"/> and returns its index entry,
    /// or null if the file should be skipped or cannot be read.
    /// </summary>
    public SourceFileIndex? Parse(string filePath)
    {
        if (!ShouldParse(filePath) || !_fileSystem.FileExists(filePath))
        {
            return null;
        }

        string source = _fileSystem.ReadAllText(filePath);
        DateTime indexedAt = _fileSystem.GetLastWriteTimeUtc(filePath);

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();

        string namespaceName = ExtractNamespace(root);
        List<TypeInfo> types = ExtractTypes(root, namespaceName, tree);

        return new SourceFileIndex
        {
            FileName = Path.GetFileName(filePath),
            FullPath = filePath,
            Namespace = namespaceName,
            Types = types,
            IndexedAtUtc = indexedAt,
        };
    }

    private static string ExtractNamespace(CompilationUnitSyntax root)
    {
        // File-scoped namespace: namespace Foo.Bar;
        FileScopedNamespaceDeclarationSyntax? fileScoped = root
            .DescendantNodes()
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        if (fileScoped is not null)
        {
            return fileScoped.Name.ToString();
        }

        // Block-scoped namespace: namespace Foo.Bar { ... }
        NamespaceDeclarationSyntax? blockScoped = root
            .DescendantNodes()
            .OfType<NamespaceDeclarationSyntax>()
            .FirstOrDefault();

        return blockScoped?.Name.ToString() ?? string.Empty;
    }

    private static List<TypeInfo> ExtractTypes(
        CompilationUnitSyntax root, string namespaceName, SyntaxTree tree)
    {
        List<TypeInfo> types = [];

        foreach (TypeDeclarationSyntax typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            SymbolKind kind = ClassifyType(typeDecl);
            List<string> baseTypes = ExtractBaseTypes(typeDecl);
            List<MemberInfo> members = ExtractMembers(typeDecl, tree);

            FileLinePositionSpan span = tree.GetLineSpan(typeDecl.Span);

            types.Add(new TypeInfo
            {
                Name = typeDecl.Identifier.Text,
                Namespace = namespaceName,
                Kind = kind,
                BaseTypes = baseTypes,
                Members = members,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
            });
        }

        foreach (EnumDeclarationSyntax enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            FileLinePositionSpan span = tree.GetLineSpan(enumDecl.Span);

            types.Add(new TypeInfo
            {
                Name = enumDecl.Identifier.Text,
                Namespace = namespaceName,
                Kind = SymbolKind.Enum,
                BaseTypes = [],
                Members = [],
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
            });
        }

        return types;
    }

    private static SymbolKind ClassifyType(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl is InterfaceDeclarationSyntax)
        {
            return SymbolKind.Interface;
        }

        if (typeDecl is StructDeclarationSyntax)
        {
            return SymbolKind.Struct;
        }

        if (typeDecl is RecordDeclarationSyntax)
        {
            return SymbolKind.Record;
        }

        // Class variants
        bool isStatic = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        bool isAbstract = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        bool isSealed = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword));

        if (isStatic)
        {
            return SymbolKind.StaticClass;
        }

        if (isAbstract)
        {
            return SymbolKind.AbstractClass;
        }

        if (isSealed)
        {
            return SymbolKind.SealedClass;
        }

        return SymbolKind.Class;
    }

    private static List<string> ExtractBaseTypes(TypeDeclarationSyntax typeDecl)
    {
        return typeDecl.BaseList?.Types
            .Select(t => t.Type.ToString())
            .ToList() ?? [];
    }

    private static List<MemberInfo> ExtractMembers(TypeDeclarationSyntax typeDecl, SyntaxTree tree)
    {
        List<MemberInfo> members = [];

        foreach (MemberDeclarationSyntax member in typeDecl.Members)
        {
            MemberInfo? info = member switch
            {
                MethodDeclarationSyntax m => CreateMethodMember(m, tree),
                ConstructorDeclarationSyntax c => CreateConstructorMember(c, tree),
                PropertyDeclarationSyntax p => CreatePropertyMember(p, tree),
                FieldDeclarationSyntax f => CreateFieldMember(f, tree),
                EventDeclarationSyntax e => CreateEventMember(e, tree),
                EventFieldDeclarationSyntax ef => CreateEventFieldMember(ef, tree),
                _ => null,
            };

            if (info is not null)
            {
                members.Add(info);
            }
        }

        return members;
    }

    private static MemberInfo CreateMethodMember(MethodDeclarationSyntax m, SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(m.Span);
        return new MemberInfo
        {
            Name = m.Identifier.Text,
            Kind = SymbolKind.Method,
            Signature = m.ToString().Split('\n')[0].Trim(),
            ReturnType = m.ReturnType.ToString(),
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }

    private static MemberInfo CreateConstructorMember(ConstructorDeclarationSyntax c, SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(c.Span);
        return new MemberInfo
        {
            Name = c.Identifier.Text,
            Kind = SymbolKind.Constructor,
            Signature = c.ToString().Split('\n')[0].Trim(),
            ReturnType = null,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }

    private static MemberInfo CreatePropertyMember(PropertyDeclarationSyntax p, SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(p.Span);
        return new MemberInfo
        {
            Name = p.Identifier.Text,
            Kind = SymbolKind.Property,
            Signature = p.ToString().Split('\n')[0].Trim(),
            ReturnType = p.Type.ToString(),
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }

    private static MemberInfo CreateFieldMember(FieldDeclarationSyntax f, SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(f.Span);
        string name = f.Declaration.Variables.First().Identifier.Text;
        return new MemberInfo
        {
            Name = name,
            Kind = SymbolKind.Field,
            Signature = f.ToString().Trim(),
            ReturnType = f.Declaration.Type.ToString(),
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }

    private static MemberInfo CreateEventMember(EventDeclarationSyntax e, SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(e.Span);
        return new MemberInfo
        {
            Name = e.Identifier.Text,
            Kind = SymbolKind.Event,
            Signature = e.ToString().Split('\n')[0].Trim(),
            ReturnType = e.Type.ToString(),
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }

    private static MemberInfo CreateEventFieldMember(EventFieldDeclarationSyntax ef, SyntaxTree tree)
    {
        FileLinePositionSpan span = tree.GetLineSpan(ef.Span);
        string name = ef.Declaration.Variables.First().Identifier.Text;
        return new MemberInfo
        {
            Name = name,
            Kind = SymbolKind.Event,
            Signature = ef.ToString().Trim(),
            ReturnType = ef.Declaration.Type.ToString(),
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }
}
