using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>
/// End-to-end store tests: build a small fixture repo through the real pipeline (scan → parallel parse → versioned
/// cache) over the in-memory file system and exercise the store's query surface. Tool-formatting behaviors
/// (ambiguity display, unknown-kind errors, text search) are covered with the MCP tools wave.
/// </summary>
public sealed class StoreIntegrationTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public StoreIntegrationTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
    }

    private void AddFile(string name, string content) =>
        _fs.AddFile(Path.Combine(@"C:\repo\Proj", name), content);

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Build_WritesVersionedCacheFile()
    {
        AddFile("A.cs", "namespace N; public class A { public void M() { } }");
        Build();

        string expected = Path.Combine(@"C:\repo\.codeindex", $"index.v{IndexCache.SchemaVersion}.cache");
        _fs.FileExists(expected).Should().BeTrue($"expected versioned cache at {expected}");
    }

    [Fact]
    public void SecondBuild_LoadsFromCache_NoChange()
    {
        AddFile("A.cs", "namespace N; public class A { public void M() { } }");
        Build();
        CodeIndexStore store2 = Build();

        store2.ProjectCount.Should().Be(1);
        store2.TypeCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void SearchSymbol_FindsNestedType()
    {
        AddFile("Outer.cs", "namespace N; public class Outer { public class Inner { } }");
        CodeIndexStore store = Build();

        store.SearchSymbol("Inner", null, null).Should().Contain(r => r.Name == "Inner");
    }

    [Fact]
    public void GetDerivedTypes_ExactMatch_NoSubstringFalsePositive()
    {
        AddFile("Types.cs", """
            namespace N;
            public interface IEntity { }
            public interface IEntityRepository { }
            public class Repo : IEntityRepository { }
            """);
        CodeIndexStore store = Build();

        // Querying the SHORTER interface name must NOT return the class that implements the longer one.
        store.GetDerivedTypes("IEntity").Should().BeEmpty();
        store.GetDerivedTypes("IEntityRepository").Should().Contain(d => d.Type.Name == "Repo");
    }

    [Fact]
    public void SearchSymbol_StructAndRecordKindFilters()
    {
        AddFile("K.cs", """
            namespace N;
            public struct St { }
            public record Rec(int X);
            public class Cls { }
            """);
        CodeIndexStore store = Build();

        store.SearchSymbol("St", "struct", null).Should().Contain(r => r.Name == "St");
        store.SearchSymbol("Rec", "record", null).Should().Contain(r => r.Name == "Rec");
        store.SearchSymbol("Cls", "struct", null).Should().NotContain(r => r.Name == "Cls");
    }

    [Fact]
    public void TypeLessFile_IsIndexed()
    {
        AddFile("GlobalUsings.cs", "global using System;\n");
        CodeIndexStore store = Build();

        // A file with no type declarations is still indexed (carries usings + content the text/reference tools scan).
        store.GetFileOutline("GlobalUsings.cs").Should().NotBeNull();
        store.AllSourceFiles.Should().Contain(f => f.FileName == "GlobalUsings.cs");
    }
}
