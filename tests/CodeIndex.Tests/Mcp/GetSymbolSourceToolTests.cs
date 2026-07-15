using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-level tests for <see cref="GetSymbolSourceTool"/>: line-window formatting, filename resolution,
/// the path-containment refusal, missing-file handling, and the past-end-of-file guard.
/// </summary>
public sealed class GetSymbolSourceToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public GetSymbolSourceToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\P\P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\P\A.cs", "namespace N;\npublic class A\n{\n    public void M() { }\n}");
        _fs.AddFile(@"C:\repo\P\Services.cs", """
            namespace N;
            public class CatalogService
            {
                public void Dispose() { }
                public int GetItems(int page) { return page; }
            }
            public class Widget
            {
                public void Dispose() { }
            }
            """);
        _fs.AddFile(@"C:\repo\P\Overloaded.cs",
            "namespace N;\npublic class Overloaded\n{\n    public void Handle() { }\n    public void Handle(int x) { }\n}");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void ReadsRequestedWindow_WithLineNumberGutter()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\A.cs", 2, 2);

        result.Should().Contain("    2| public class A");
        result.Should().Contain("    3| {");
        result.Should().NotContain("namespace N;");
        result.Should().NotContain("public void M()");
    }

    [Fact]
    public void ResolvesBareFilename_ToIndexedPath()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, "A.cs", 1, 1);

        result.Should().Contain("    1| namespace N;");
    }

    [Fact]
    public void ClampsCount_ToEndOfFile()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\A.cs", 4, 100);

        result.Should().Contain("    4|     public void M() { }");
        result.Should().Contain("    5| }");
    }

    [Fact]
    public void RefusesPath_OutsideRepoAndNotIndexed()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\Users\me\.aws\credentials", 1, 5);

        result.Should().StartWith("Refused:");
        result.Should().Contain("outside the repository");
    }

    [Fact]
    public void ReportsNotFound_WhenWithinRepoButMissing()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\Ghost.cs", 1, 5);

        result.Should().StartWith("Source file not found:");
        result.Should().Contain("Ghost.cs");
    }

    [Fact]
    public void ReportsBeyondFileLength_WhenStartPastEnd()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\A.cs", 100, 5);

        result.Should().Contain("Start line 100 is beyond file length (5 lines).");
    }

    [Fact]
    public void Member_UniqueMember_ReturnsHeaderAndBodyOnly()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "GetItems");

        // Header names the enclosing type + signature + project-relative path; body is just the member.
        result.Should().Contain("# CatalogService.");
        result.Should().Contain("[Services.cs:");
        result.Should().Contain("public int GetItems(int page)");
        result.Should().NotContain("public class CatalogService");
        result.Should().NotContain("Dispose");
    }

    [Fact]
    public void Member_Ambiguous_ReturnsListingNoSource()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "Dispose");

        result.Should().StartWith("AMBIGUOUS: 2 members named 'Dispose'");
        result.Should().Contain("CatalogService.");
        result.Should().Contain("Widget.");
        // A listing, not source.
        result.Should().NotContain("    | ");
    }

    [Fact]
    public void Member_DisambiguatedByType_ReturnsSingle()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "Dispose", type: "Widget");

        result.Should().StartWith("# Widget.");
        result.Should().NotContain("AMBIGUOUS");
        result.Should().Contain("public void Dispose()");
    }

    [Fact]
    public void Member_NotFound_ReturnsMessageNoSource()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "Ghost");

        result.Should().Contain("Member 'Ghost'");
        result.Should().Contain("not found in index.");
    }

    [Fact]
    public void Member_TypeNameFallback_ReturnsWholeSmallType()
    {
        CodeIndexStore store = Build();

        // 'A' is not a member name; it falls back to the type and renders the whole (small) type body.
        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "A");

        result.Should().StartWith("# class A [");
        result.Should().Contain("public class A");
        result.Should().Contain("public void M()");
    }

    [Fact]
    public void NoMemberAndNoLineWindow_ReturnsUsageGuidance()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs);

        result.Should().Contain("Provide member=");
    }

    [Fact]
    public void Member_Overloads_AmbiguousUntilStartLineSelects()
    {
        CodeIndexStore store = Build();

        string ambiguous = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "Handle");
        ambiguous.Should().StartWith("AMBIGUOUS: 2 members named 'Handle'");

        // Re-calling with member= plus the startLine of one overload selects it (the advertised escape hatch).
        string picked = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "Handle", startLine: 5);
        picked.Should().StartWith("# Overloaded.");
        picked.Should().Contain("void Handle(int)");
        picked.Should().NotContain("AMBIGUOUS");
    }

    [Fact]
    public void Member_ScopedToFile_DoesNotFallBackToUnrelatedType()
    {
        CodeIndexStore store = Build();

        // 'A' is a type in A.cs, but scoped to Services.cs where it doesn't exist: must NOT return A's body.
        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "A", file: "Services.cs");

        result.Should().Contain("not found");
        result.Should().NotContain("public class A");
    }

    [Fact]
    public void Member_TypeFallback_LargeType_RefusedWithGuidance()
    {
        System.Text.StringBuilder src = new();
        src.AppendLine("namespace N;");
        src.AppendLine("public class Big");
        src.AppendLine("{");
        for (int i = 0; i < 320; i++)
        {
            src.AppendLine($"    private int _f{i};");
        }

        src.AppendLine("}");
        _fs.AddFile(@"C:\repo\P\Big.cs", src.ToString());
        CodeIndexStore store = Build();

        // 'Big' matches no member, falls back to the type, but the type is >300 lines: refuse and steer to outline.
        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, member: "Big");

        result.Should().Contain("too large to dump");
        result.Should().Contain("get_file_outline");
        result.Should().NotContain("private int _f0;");
    }
}
