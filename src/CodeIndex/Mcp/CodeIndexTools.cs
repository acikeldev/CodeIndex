using System.ComponentModel;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public sealed class CodeIndexTools
{
    private readonly ICodeIndexStore _store;

    public CodeIndexTools(ICodeIndexStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "list_projects")]
    [Description("List all indexed C# projects. Returns project name, directory, and .csproj path.")]
    public IReadOnlyList<object> ListProjects() =>
        _store.GetProjects()
              .Select(p => (object)new { p.Name, p.Directory, p.ProjectFilePath })
              .ToList();

    [McpServerTool(Name = "list_files")]
    [Description("List all indexed source files. Optionally filter by path fragment (case-insensitive substring of the full path).")]
    public IReadOnlyList<object> ListFiles(
        [Description("Optional path fragment to filter by (e.g. 'Services' or 'UserService.cs'). Omit or pass empty string for all files.")]
        string? pathFragment = null)
    {
        IReadOnlyList<SourceFileIndex> files = string.IsNullOrEmpty(pathFragment)
            ? _store.GetFiles()
            : _store.SearchFiles(pathFragment);

        return files
            .Select(f => (object)new { f.FileName, f.FullPath, f.Namespace, TypeCount = f.Types.Count })
            .ToList();
    }

    [McpServerTool(Name = "get_file_outline")]
    [Description("Return all types and their members declared in a specific source file identified by its full path.")]
    public object GetFileOutline(
        [Description("Absolute path to the .cs file (e.g. 'C:\\Repo\\src\\UserService.cs').")]
        string fullPath)
    {
        SourceFileIndex? file = _store.GetFiles()
            .FirstOrDefault(f => string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            return new { Error = $"File not found in index: {fullPath}" };
        }

        return new
        {
            file.FileName,
            file.FullPath,
            file.Namespace,
            Types = file.Types.Select(t => new
            {
                t.Name,
                Kind = t.Kind.ToString(),
                t.StartLine,
                t.EndLine,
                BaseTypes = t.BaseTypes,
                Members = t.Members.Select(m => new
                {
                    m.Name,
                    Kind = m.Kind.ToString(),
                    m.Signature,
                    m.ReturnType,
                    m.StartLine,
                    m.EndLine,
                }),
            }),
        };
    }

    [McpServerTool(Name = "search_symbol")]
    [Description("Search for types or members by name substring. Returns matching symbols with their location and kind.")]
    public object SearchSymbol(
        [Description("Name substring to search for (case-insensitive, e.g. 'UserService' or 'GetUser').")]
        string name,
        [Description("Symbol scope: 'types' to search only type declarations, 'members' to search only members, 'all' for both. Default is 'all'.")]
        string scope = "all",
        [Description("Optional kind filter for types: Class, StaticClass, AbstractClass, SealedClass, Interface, Enum, Struct, Record. Leave empty for all.")]
        string? typeKind = null,
        [Description("Optional kind filter for members: Method, Property, Field, Constructor, Event. Leave empty for all.")]
        string? memberKind = null)
    {
        SymbolKind? parsedTypeKind = ParseKind(typeKind);
        SymbolKind? parsedMemberKind = ParseKind(memberKind);

        List<object> results = [];

        if (scope is "all" or "types")
        {
            IReadOnlyList<TypeInfo> types = _store.SearchTypes(name, parsedTypeKind);
            results.AddRange(types.Select(t => (object)new
            {
                SymbolType = "Type",
                t.Name,
                t.Namespace,
                Kind = t.Kind.ToString(),
                t.StartLine,
                t.EndLine,
            }));
        }

        if (scope is "all" or "members")
        {
            IReadOnlyList<MemberInfo> members = _store.SearchMembers(name, parsedMemberKind);
            results.AddRange(members.Select(m => (object)new
            {
                SymbolType = "Member",
                m.Name,
                Kind = m.Kind.ToString(),
                m.Signature,
                m.ReturnType,
                m.StartLine,
                m.EndLine,
            }));
        }

        return new { Count = results.Count, Results = results };
    }

    [McpServerTool(Name = "search_text")]
    [Description("Search for files whose full path contains the given text fragment. Useful for finding files by directory or filename pattern.")]
    public object SearchText(
        [Description("Text fragment to search in file paths (case-insensitive).")]
        string fragment)
    {
        IReadOnlyList<SourceFileIndex> files = _store.SearchFiles(fragment);
        return new
        {
            Count = files.Count,
            Files = files.Select(f => new { f.FileName, f.FullPath, f.Namespace }),
        };
    }

    [McpServerTool(Name = "find_references")]
    [Description("Find all types and members that reference the given type name in their base types or signatures.")]
    public object FindReferences(
        [Description("Type name to find references to (e.g. 'IUserService').")]
        string typeName)
    {
        List<object> results = [];

        foreach (SourceFileIndex file in _store.GetFiles())
        {
            foreach (TypeInfo type in file.Types)
            {
                if (type.BaseTypes.Any(bt => bt.Contains(typeName, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new
                    {
                        ReferenceKind = "Inherits/Implements",
                        TypeName = type.Name,
                        type.Namespace,
                        file.FullPath,
                        type.StartLine,
                    });
                }

                foreach (MemberInfo member in type.Members)
                {
                    if (member.Signature.Contains(typeName, StringComparison.OrdinalIgnoreCase) ||
                        (member.ReturnType?.Contains(typeName, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        results.Add(new
                        {
                            ReferenceKind = "MemberSignature",
                            TypeName = type.Name,
                            MemberName = member.Name,
                            file.FullPath,
                            member.StartLine,
                        });
                    }
                }
            }
        }

        return new { TypeName = typeName, Count = results.Count, References = results };
    }

    [McpServerTool(Name = "get_type_members")]
    [Description("Get all members (methods, properties, fields, events, constructors) declared on a specific type.")]
    public object GetTypeMembers(
        [Description("Exact type name to look up (e.g. 'UserService').")]
        string typeName,
        [Description("Optional namespace to disambiguate types with the same name.")]
        string? namespaceName = null)
    {
        IReadOnlyList<TypeInfo> matches = _store.SearchTypes(typeName);
        IEnumerable<TypeInfo> filtered = matches
            .Where(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(namespaceName))
        {
            filtered = filtered.Where(t =>
                t.Namespace.Contains(namespaceName, StringComparison.OrdinalIgnoreCase));
        }

        TypeInfo? type = filtered.FirstOrDefault();

        if (type is null)
        {
            return new { Error = $"Type not found: {typeName}" };
        }

        return new
        {
            type.Name,
            type.Namespace,
            Kind = type.Kind.ToString(),
            Members = type.Members.Select(m => new
            {
                m.Name,
                Kind = m.Kind.ToString(),
                m.Signature,
                m.ReturnType,
                m.StartLine,
                m.EndLine,
            }),
        };
    }

    [McpServerTool(Name = "get_class_hierarchy")]
    [Description("For a given type, return its base types and all types in the index that directly inherit or implement it.")]
    public object GetClassHierarchy(
        [Description("Type name to inspect (e.g. 'UserService' or 'IUserService').")]
        string typeName)
    {
        TypeInfo? type = _store.SearchTypes(typeName)
            .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            return new { Error = $"Type not found: {typeName}" };
        }

        IReadOnlyList<TypeInfo> subtypes = _store.GetFiles()
            .SelectMany(f => f.Types)
            .Where(t => t.BaseTypes.Any(bt =>
                bt.Contains(typeName, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();

        return new
        {
            type.Name,
            type.Namespace,
            Kind = type.Kind.ToString(),
            BaseTypes = type.BaseTypes,
            DirectSubtypes = subtypes.Select(t => new
            {
                t.Name,
                t.Namespace,
                Kind = t.Kind.ToString(),
            }),
        };
    }

    [McpServerTool(Name = "get_symbol_source")]
    [Description("Return the source lines for a specific type or member given its file path and line range.")]
    public object GetSymbolSource(
        [Description("Absolute path to the .cs file.")]
        string fullPath,
        [Description("1-based start line (inclusive).")]
        int startLine,
        [Description("1-based end line (inclusive).")]
        int endLine)
    {
        SourceFileIndex? file = _store.GetFiles()
            .FirstOrDefault(f => string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            return new { Error = $"File not found in index: {fullPath}" };
        }

        return new
        {
            file.FullPath,
            StartLine = startLine,
            EndLine = endLine,
            Note = "Use the file path and line numbers with your file-reading tool to retrieve the actual source text.",
        };
    }

    [McpServerTool(Name = "get_context_bundle")]
    [Description("Return outlines for multiple types in one call. Useful for reading related types without multiple round-trips.")]
    public object GetContextBundle(
        [Description("Comma-separated list of type names to look up (e.g. 'UserService,IUserService,UserDto').")]
        string typeNames)
    {
        string[] names = typeNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<object> found = [];
        List<string> notFound = [];

        foreach (string name in names)
        {
            TypeInfo? type = _store.SearchTypes(name)
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            if (type is null)
            {
                notFound.Add(name);
                continue;
            }

            found.Add(new
            {
                type.Name,
                type.Namespace,
                Kind = type.Kind.ToString(),
                type.StartLine,
                type.EndLine,
                BaseTypes = type.BaseTypes,
                Members = type.Members.Select(m => new
                {
                    m.Name,
                    Kind = m.Kind.ToString(),
                    m.Signature,
                    m.ReturnType,
                    m.StartLine,
                    m.EndLine,
                }),
            });
        }

        return new { Found = found, NotFound = notFound };
    }

    [McpServerTool(Name = "get_project_dependencies")]
    [Description("List all projects in the index. Since static dependency graph requires compilation, this returns the project inventory as a starting point.")]
    public object GetProjectDependencies()
    {
        IReadOnlyList<ProjectIndex> projects = _store.GetProjects();
        return new
        {
            Count = projects.Count,
            Projects = projects.Select(p => new
            {
                p.Name,
                p.Directory,
                p.ProjectFilePath,
                FileCount = p.SourceFiles.Count,
            }),
        };
    }

    [McpServerTool(Name = "suggest_queries")]
    [Description("Suggest example tool calls based on what's in the index. Useful for orientation when starting to explore a codebase.")]
    public object SuggestQueries()
    {
        IReadOnlyList<ProjectIndex> projects = _store.GetProjects();
        IReadOnlyList<SourceFileIndex> files = _store.GetFiles();

        string[] sampleTypeNames = files
            .SelectMany(f => f.Types)
            .Select(t => t.Name)
            .Take(5)
            .ToArray();

        string[] sampleFileFragments = files
            .Select(f => Path.GetDirectoryName(f.FullPath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!.Split(Path.DirectorySeparatorChar).Last())
            .Where(seg => !string.IsNullOrEmpty(seg))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        return new
        {
            IndexStats = new
            {
                ProjectCount = projects.Count,
                FileCount = files.Count,
                TypeCount = files.Sum(f => f.Types.Count),
                MemberCount = files.Sum(f => f.Types.Sum(t => t.Members.Count)),
            },
            SuggestedCalls = new[]
            {
                new { Tool = "list_projects", Example = "list_projects()" },
                new { Tool = "search_symbol", Example = $"search_symbol(name: \"{(sampleTypeNames.Length > 0 ? sampleTypeNames[0] : "MyClass")}\")" },
                new { Tool = "search_text", Example = $"search_text(fragment: \"{(sampleFileFragments.Length > 0 ? sampleFileFragments[0] : "Services")}\")" },
                new { Tool = "get_file_outline", Example = $"get_file_outline(fullPath: \"{(files.Count > 0 ? files[0].FullPath : "C:\\\\Repo\\\\Foo.cs")}\")" },
            },
        };
    }

    private static SymbolKind? ParseKind(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Enum.TryParse<SymbolKind>(value, ignoreCase: true, out SymbolKind result)
            ? result
            : null;
    }
}
