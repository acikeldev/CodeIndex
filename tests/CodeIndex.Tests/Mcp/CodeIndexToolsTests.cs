using System.Text.Json;
using CodeIndex.Abstractions;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests.Mcp;

public sealed class CodeIndexToolsTests
{
    private readonly ICodeIndexStore _store = Substitute.For<ICodeIndexStore>();
    private readonly CodeIndexTools _tools;

    public CodeIndexToolsTests()
    {
        _tools = new CodeIndexTools(_store);
    }

    // ── list_projects ─────────────────────────────────────────────────────────

    [Fact]
    public void ListProjects_ReturnsAllProjects()
    {
        _store.GetProjects().Returns(
        [
            MakeProject("App", @"C:\Repo\App", @"C:\Repo\App\App.csproj"),
            MakeProject("Core", @"C:\Repo\Core", @"C:\Repo\Core\Core.csproj"),
        ]);

        IReadOnlyList<object> result = _tools.ListProjects();

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ListProjects_EmptyIndex_ReturnsEmpty()
    {
        _store.GetProjects().Returns([]);

        _tools.ListProjects().Should().BeEmpty();
    }

    // ── list_files ────────────────────────────────────────────────────────────

    [Fact]
    public void ListFiles_NoFragment_ReturnsAllFiles()
    {
        _store.GetFiles().Returns([MakeFile("Foo.cs", @"C:\Repo\Foo.cs", "App")]);

        IReadOnlyList<object> result = _tools.ListFiles();

        result.Should().ContainSingle();
        _store.Received(1).GetFiles();
        _store.DidNotReceive().SearchFiles(Arg.Any<string>());
    }

    [Fact]
    public void ListFiles_EmptyFragment_ReturnsAllFiles()
    {
        _store.GetFiles().Returns([]);

        _tools.ListFiles(string.Empty);

        _store.Received(1).GetFiles();
        _store.DidNotReceive().SearchFiles(Arg.Any<string>());
    }

    [Fact]
    public void ListFiles_WithFragment_DelegatesToSearchFiles()
    {
        _store.SearchFiles("Services").Returns([MakeFile("Svc.cs", @"C:\Repo\Services\Svc.cs", "App")]);

        IReadOnlyList<object> result = _tools.ListFiles("Services");

        result.Should().ContainSingle();
        _store.Received(1).SearchFiles("Services");
        _store.DidNotReceive().GetFiles();
    }

    // ── get_file_outline ──────────────────────────────────────────────────────

    [Fact]
    public void GetFileOutline_ExistingFile_ReturnsOutline()
    {
        TypeInfo type = MakeTypeWithMember("Foo", SymbolKind.Class, "Run", SymbolKind.Method);
        SourceFileIndex file = new()
        {
            FileName = "Foo.cs",
            FullPath = @"C:\Repo\Foo.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };
        _store.GetFiles().Returns([file]);

        JsonElement result = Serialize(_tools.GetFileOutline(@"C:\Repo\Foo.cs"));

        result.GetProperty("FileName").GetString().Should().Be("Foo.cs");
        result.GetProperty("Types")[0].GetProperty("Members").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void GetFileOutline_CaseInsensitivePath_Matches()
    {
        SourceFileIndex file = MakeFileWithType(@"C:\Repo\Foo.cs", "Foo", SymbolKind.Class);
        _store.GetFiles().Returns([file]);

        JsonElement result = Serialize(_tools.GetFileOutline(@"c:\repo\foo.cs"));

        result.GetProperty("FileName").GetString().Should().Be("Foo.cs");
    }

    [Fact]
    public void GetFileOutline_MissingFile_ReturnsError()
    {
        _store.GetFiles().Returns([]);

        JsonElement result = Serialize(_tools.GetFileOutline(@"C:\Missing.cs"));

        result.GetProperty("Error").GetString().Should().Contain("not found");
    }

    // ── search_symbol ─────────────────────────────────────────────────────────

    [Fact]
    public void SearchSymbol_DefaultScope_SearchesBothTypesAndMembers()
    {
        _store.SearchTypes("Foo", null).Returns([MakeType("Foo", SymbolKind.Class)]);
        _store.SearchMembers("Foo", null).Returns([MakeMember("Foo", SymbolKind.Method)]);

        JsonElement result = Serialize(_tools.SearchSymbol("Foo"));

        result.GetProperty("Count").GetInt32().Should().Be(2);
    }

    [Fact]
    public void SearchSymbol_TypesScope_OnlySearchesTypes()
    {
        _store.SearchTypes("Foo", null).Returns([MakeType("Foo", SymbolKind.Class)]);

        _tools.SearchSymbol("Foo", scope: "types");

        _store.Received(1).SearchTypes("Foo", null);
        _store.DidNotReceive().SearchMembers(Arg.Any<string>(), Arg.Any<SymbolKind?>());
    }

    [Fact]
    public void SearchSymbol_MembersScope_OnlySearchesMembers()
    {
        _store.SearchMembers("Get", null).Returns([]);

        _tools.SearchSymbol("Get", scope: "members");

        _store.DidNotReceive().SearchTypes(Arg.Any<string>(), Arg.Any<SymbolKind?>());
        _store.Received(1).SearchMembers("Get", null);
    }

    [Fact]
    public void SearchSymbol_WithTypeKind_PassesKindFilter()
    {
        _store.SearchTypes("Svc", SymbolKind.Interface).Returns([]);
        _store.SearchMembers("Svc", null).Returns([]);

        _tools.SearchSymbol("Svc", scope: "all", typeKind: "Interface");

        _store.Received(1).SearchTypes("Svc", SymbolKind.Interface);
    }

    [Fact]
    public void SearchSymbol_WithMemberKind_PassesKindFilter()
    {
        _store.SearchTypes("Run", null).Returns([]);
        _store.SearchMembers("Run", SymbolKind.Method).Returns([]);

        _tools.SearchSymbol("Run", scope: "all", memberKind: "Method");

        _store.Received(1).SearchMembers("Run", SymbolKind.Method);
    }

    [Fact]
    public void SearchSymbol_InvalidKind_TreatedAsNull()
    {
        _store.SearchTypes("X", null).Returns([]);
        _store.SearchMembers("X", null).Returns([]);

        _tools.SearchSymbol("X", typeKind: "NotAKind");

        _store.Received(1).SearchTypes("X", null);
    }

    // ── search_text ───────────────────────────────────────────────────────────

    [Fact]
    public void SearchText_DelegatesToSearchFiles()
    {
        _store.SearchFiles("Services").Returns([MakeFile("Svc.cs", @"C:\Repo\Services\Svc.cs", "App")]);

        JsonElement result = Serialize(_tools.SearchText("Services"));

        result.GetProperty("Count").GetInt32().Should().Be(1);
        _store.Received(1).SearchFiles("Services");
    }

    // ── find_references ───────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_InheritsType_Found()
    {
        TypeInfo type = new()
        {
            Name = "MyService",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = ["IUserService"],
            StartLine = 1,
            EndLine = 10,
        };
        SourceFileIndex file = new()
        {
            FileName = "MyService.cs",
            FullPath = @"C:\Repo\MyService.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };
        _store.GetFiles().Returns([file]);

        JsonElement result = Serialize(_tools.FindReferences("IUserService"));

        result.GetProperty("Count").GetInt32().Should().Be(1);
        result.GetProperty("References")[0].GetProperty("ReferenceKind").GetString()
            .Should().Be("Inherits/Implements");
    }

    [Fact]
    public void FindReferences_MemberSignatureMatch_Found()
    {
        MemberInfo member = new()
        {
            Name = "Process",
            Kind = SymbolKind.Method,
            Signature = "public void Process(IUserService svc)",
            ReturnType = "void",
            StartLine = 5,
            EndLine = 8,
        };
        TypeInfo type = new()
        {
            Name = "Handler",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = [],
            Members = [member],
            StartLine = 1,
            EndLine = 20,
        };
        SourceFileIndex file = new()
        {
            FileName = "Handler.cs",
            FullPath = @"C:\Repo\Handler.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };
        _store.GetFiles().Returns([file]);

        JsonElement result = Serialize(_tools.FindReferences("IUserService"));

        result.GetProperty("Count").GetInt32().Should().Be(1);
        result.GetProperty("References")[0].GetProperty("ReferenceKind").GetString()
            .Should().Be("MemberSignature");
    }

    [Fact]
    public void FindReferences_ReturnTypeMatch_Found()
    {
        // Signature deliberately does NOT contain the type name so the ReturnType branch is exercised.
        MemberInfo member = new()
        {
            Name = "GetService",
            Kind = SymbolKind.Method,
            Signature = "public object GetService()",
            ReturnType = "IUserService",
            StartLine = 5,
            EndLine = 7,
        };
        TypeInfo type = new()
        {
            Name = "Factory",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = [],
            Members = [member],
            StartLine = 1,
            EndLine = 15,
        };
        SourceFileIndex file = new()
        {
            FileName = "Factory.cs",
            FullPath = @"C:\Repo\Factory.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };
        _store.GetFiles().Returns([file]);

        JsonElement result = Serialize(_tools.FindReferences("IUserService"));

        result.GetProperty("Count").GetInt32().Should().Be(1);
    }

    [Fact]
    public void FindReferences_NullReturnType_DoesNotThrow()
    {
        MemberInfo member = new()
        {
            Name = "Run",
            Kind = SymbolKind.Method,
            Signature = "public void Run()",
            ReturnType = null,
            StartLine = 1,
            EndLine = 3,
        };
        TypeInfo type = new()
        {
            Name = "Worker",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = [],
            Members = [member],
            StartLine = 1,
            EndLine = 10,
        };
        SourceFileIndex file = new()
        {
            FileName = "Worker.cs",
            FullPath = @"C:\Repo\Worker.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };
        _store.GetFiles().Returns([file]);

        JsonElement result = Serialize(_tools.FindReferences("IUserService"));

        result.GetProperty("Count").GetInt32().Should().Be(0);
    }

    [Fact]
    public void FindReferences_NoMatch_ReturnsZeroCount()
    {
        _store.GetFiles().Returns([]);

        JsonElement result = Serialize(_tools.FindReferences("INothing"));

        result.GetProperty("Count").GetInt32().Should().Be(0);
    }

    // ── get_type_members ──────────────────────────────────────────────────────

    [Fact]
    public void GetTypeMembers_ExistingType_ReturnsMemberList()
    {
        TypeInfo type = MakeTypeWithMember("UserService", SymbolKind.Class, "GetUser", SymbolKind.Method);
        _store.SearchTypes("UserService").Returns([type]);

        JsonElement result = Serialize(_tools.GetTypeMembers("UserService"));

        result.GetProperty("Name").GetString().Should().Be("UserService");
    }

    [Fact]
    public void GetTypeMembers_WithNamespaceFilter_FiltersCorrectly()
    {
        TypeInfo type = new()
        {
            Name = "Svc",
            Namespace = "App.Services",
            Kind = SymbolKind.Class,
            BaseTypes = [],
            Members = [],
            StartLine = 1,
            EndLine = 5,
        };
        TypeInfo otherType = new()
        {
            Name = "Svc",
            Namespace = "App.Other",
            Kind = SymbolKind.Class,
            BaseTypes = [],
            Members = [],
            StartLine = 1,
            EndLine = 5,
        };
        _store.SearchTypes("Svc").Returns([type, otherType]);

        JsonElement result = Serialize(_tools.GetTypeMembers("Svc", namespaceName: "Services"));

        result.GetProperty("Name").GetString().Should().Be("Svc");
        result.GetProperty("Namespace").GetString().Should().Be("App.Services");
    }

    [Fact]
    public void GetTypeMembers_MissingType_ReturnsError()
    {
        _store.SearchTypes("Ghost").Returns([]);

        JsonElement result = Serialize(_tools.GetTypeMembers("Ghost"));

        result.GetProperty("Error").GetString().Should().Contain("not found");
    }

    // ── get_class_hierarchy ───────────────────────────────────────────────────

    [Fact]
    public void GetClassHierarchy_ExistingType_ReturnsBaseTypesAndSubtypes()
    {
        TypeInfo baseType = new()
        {
            Name = "IService",
            Namespace = "App",
            Kind = SymbolKind.Interface,
            BaseTypes = [],
            Members = [],
            StartLine = 1,
            EndLine = 5,
        };
        TypeInfo impl = new()
        {
            Name = "UserService",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = ["IService"],
            Members = [],
            StartLine = 1,
            EndLine = 10,
        };
        SourceFileIndex implFile = new()
        {
            FileName = "UserService.cs",
            FullPath = @"C:\Repo\UserService.cs",
            Namespace = "App",
            Types = [impl],
            IndexedAtUtc = DateTime.UtcNow,
        };
        _store.SearchTypes("IService").Returns([baseType]);
        _store.GetFiles().Returns([implFile]);

        JsonElement result = Serialize(_tools.GetClassHierarchy("IService"));

        result.GetProperty("Name").GetString().Should().Be("IService");
        result.GetProperty("DirectSubtypes").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void GetClassHierarchy_MissingType_ReturnsError()
    {
        _store.SearchTypes("Ghost").Returns([]);

        JsonElement result = Serialize(_tools.GetClassHierarchy("Ghost"));

        result.GetProperty("Error").GetString().Should().Contain("not found");
    }

    // ── get_symbol_source ─────────────────────────────────────────────────────

    [Fact]
    public void GetSymbolSource_ExistingFile_ReturnsMetadata()
    {
        _store.GetFiles().Returns([MakeFile("Foo.cs", @"C:\Repo\Foo.cs", "App")]);

        JsonElement result = Serialize(_tools.GetSymbolSource(@"C:\Repo\Foo.cs", 10, 20));

        result.GetProperty("StartLine").GetInt32().Should().Be(10);
        result.GetProperty("EndLine").GetInt32().Should().Be(20);
    }

    [Fact]
    public void GetSymbolSource_MissingFile_ReturnsError()
    {
        _store.GetFiles().Returns([]);

        JsonElement result = Serialize(_tools.GetSymbolSource(@"C:\Missing.cs", 1, 5));

        result.GetProperty("Error").GetString().Should().Contain("not found");
    }

    // ── get_context_bundle ────────────────────────────────────────────────────

    [Fact]
    public void GetContextBundle_AllFound_ReturnsAllTypes()
    {
        TypeInfo fooWithMember = MakeTypeWithMember("Foo", SymbolKind.Class, "Run", SymbolKind.Method);
        _store.SearchTypes("Foo").Returns([fooWithMember]);
        _store.SearchTypes("Bar").Returns([MakeType("Bar", SymbolKind.Interface)]);

        JsonElement result = Serialize(_tools.GetContextBundle("Foo, Bar"));

        result.GetProperty("Found").GetArrayLength().Should().Be(2);
        result.GetProperty("NotFound").GetArrayLength().Should().Be(0);
        // Verify member enumeration inside GetContextBundle is exercised
        result.GetProperty("Found")[0].GetProperty("Members").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void GetContextBundle_SomeNotFound_ReportsNotFound()
    {
        _store.SearchTypes("Foo").Returns([MakeType("Foo", SymbolKind.Class)]);
        _store.SearchTypes("Ghost").Returns([]);

        JsonElement result = Serialize(_tools.GetContextBundle("Foo, Ghost"));

        result.GetProperty("NotFound").GetArrayLength().Should().Be(1);
    }

    // ── get_project_dependencies ──────────────────────────────────────────────

    [Fact]
    public void GetProjectDependencies_ReturnsProjectInventory()
    {
        _store.GetProjects().Returns(
        [
            MakeProject("App", @"C:\Repo\App", @"C:\Repo\App\App.csproj"),
        ]);

        JsonElement result = Serialize(_tools.GetProjectDependencies());

        result.GetProperty("Count").GetInt32().Should().Be(1);
    }

    // ── suggest_queries ───────────────────────────────────────────────────────

    [Fact]
    public void SuggestQueries_PopulatedIndex_ReturnsSuggestionsAndStats()
    {
        _store.GetProjects().Returns([MakeProject("App", @"C:\Repo\App", @"C:\Repo\App\App.csproj")]);
        _store.GetFiles().Returns([MakeFileWithType(@"C:\Repo\App\Foo.cs", "Foo", SymbolKind.Class)]);

        JsonElement result = Serialize(_tools.SuggestQueries());

        result.GetProperty("IndexStats").GetProperty("ProjectCount").GetInt32().Should().Be(1);
        result.GetProperty("IndexStats").GetProperty("FileCount").GetInt32().Should().Be(1);
        result.GetProperty("IndexStats").GetProperty("TypeCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public void SuggestQueries_EmptyIndex_ReturnsDefaultSuggestions()
    {
        _store.GetProjects().Returns([]);
        _store.GetFiles().Returns([]);

        JsonElement result = Serialize(_tools.SuggestQueries());

        result.GetProperty("IndexStats").GetProperty("ProjectCount").GetInt32().Should().Be(0);
        result.GetProperty("SuggestedCalls").GetArrayLength().Should().Be(4);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonElement Serialize(object value)
    {
        string json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement;
    }

    private static ProjectIndex MakeProject(string name, string dir, string projPath) => new()
    {
        Name = name,
        Directory = dir,
        ProjectFilePath = projPath,
    };

    private static SourceFileIndex MakeFile(string name, string path, string ns) => new()
    {
        FileName = name,
        FullPath = path,
        Namespace = ns,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private static SourceFileIndex MakeFileWithType(string path, string typeName, SymbolKind kind)
    {
        TypeInfo type = MakeType(typeName, kind);
        return new SourceFileIndex
        {
            FileName = Path.GetFileName(path),
            FullPath = path,
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };
    }

    private static TypeInfo MakeType(string name, SymbolKind kind) => new()
    {
        Name = name,
        Namespace = "App",
        Kind = kind,
        BaseTypes = [],
        Members = [],
        StartLine = 1,
        EndLine = 10,
    };

    private static TypeInfo MakeTypeWithMember(
        string typeName, SymbolKind typeKind, string memberName, SymbolKind memberKind)
    {
        MemberInfo member = MakeMember(memberName, memberKind);
        return new TypeInfo
        {
            Name = typeName,
            Namespace = "App",
            Kind = typeKind,
            BaseTypes = [],
            Members = [member],
            StartLine = 1,
            EndLine = 20,
        };
    }

    private static MemberInfo MakeMember(string name, SymbolKind kind) => new()
    {
        Name = name,
        Kind = kind,
        Signature = $"public void {name}()",
        ReturnType = "void",
        StartLine = 5,
        EndLine = 8,
    };
}
