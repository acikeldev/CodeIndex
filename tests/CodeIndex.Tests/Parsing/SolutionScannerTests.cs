using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>Covers the single-pruned-walk scanner: project discovery semantics, free mtimes, pruning,
/// deepest-ancestor assignment, the designer.cs rule, and deterministic ordering. The real-disk temp-dir
/// fixture of the original is replaced by the shared in-memory file system; assertions are unchanged.</summary>
public class SolutionScannerTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    // The original fixture wrote to a real temp dir, so the OS normalized separators before the walk saw them.
    // The in-memory FS stores keys verbatim, so normalize here (GetFullPath) to reproduce that on-disk behavior.
    private void Write(string relPath, string content) =>
        _fs.AddFile(Path.GetFullPath(Path.Combine(Root, relPath)), content);

    private const string MinimalCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private void WriteSlnx(string relPath, params string[] projectRelPaths)
    {
        string projects = string.Join("\n", projectRelPaths.Select(p => $"  <Project Path=\"{p}\" />"));
        Write(relPath, $"<Solution>\n{projects}\n</Solution>\n");
    }

    private SolutionScanner.ScanResult Scan() => new SolutionScanner(_fs).Scan(Root);

    [Fact]
    public void Scan_ReturnsOnlySolutionReferencedProjects()
    {
        WriteSlnx("App.slnx", "ProjA/ProjA.csproj");
        Write("ProjA/ProjA.csproj", MinimalCsproj);
        Write("ProjA/A.cs", "namespace A; public class A { }");
        Write("ProjB/ProjB.csproj", MinimalCsproj); // exists but unreferenced
        Write("ProjB/B.cs", "namespace B; public class B { }");

        SolutionScanner.ScanResult r = Scan();

        r.Projects.Should().ContainSingle();
        r.Projects[0].Name.Should().Be("ProjA");
        r.Timestamps.Keys.Should().NotContain(k => k.EndsWith("B.cs"));
        r.Timestamps.Keys.Should().Contain(k => k.EndsWith("A.cs"));
    }

    [Fact]
    public void Scan_CapturesTimestampsForIndexedFilesOnly()
    {
        WriteSlnx("App.slnx", "P/P.csproj");
        Write("P/P.csproj", MinimalCsproj);
        Write("P/One.cs", "namespace P; public class One { }");

        SolutionScanner.ScanResult r = Scan();

        string key = r.Timestamps.Keys.Should().ContainSingle().Which;
        key.Should().EndWith("One.cs");
        r.Timestamps[key].Should().Be(_fs.GetLastWriteTimeUtc(key).Ticks);
    }

    [Fact]
    public void Scan_PrunesExcludedDirectories()
    {
        WriteSlnx("App.slnx", "P/P.csproj");
        Write("P/P.csproj", MinimalCsproj);
        Write("P/Real.cs", "namespace P; public class Real { }");
        foreach (string excluded in new[] { "node_modules", "bin", "obj", ".git", ".vs", ".codeindex" })
        {
            Write($"P/{excluded}/Junk.cs", "namespace P; public class Junk { }");
        }

        SolutionScanner.ScanResult r = Scan();

        r.Timestamps.Keys.Should().Contain(k => k.EndsWith("Real.cs"));
        r.Timestamps.Keys.Should().NotContain(k => k.EndsWith("Junk.cs"));
    }

    [Fact]
    public void Scan_AssignsFileToDeepestCsprojDir()
    {
        WriteSlnx("App.slnx", "P/P.csproj", "P/Sub/Sub.csproj");
        Write("P/P.csproj", MinimalCsproj);
        Write("P/Top.cs", "namespace P; public class Top { }");
        Write("P/Sub/Sub.csproj", MinimalCsproj);
        Write("P/Sub/Inner.cs", "namespace P.Sub; public class Inner { }");

        SolutionScanner.ScanResult r = Scan();

        SolutionScanner.ProjectInfo p = r.Projects.Should().ContainSingle(x => x.Name == "P").Which;
        SolutionScanner.ProjectInfo sub = r.Projects.Should().ContainSingle(x => x.Name == "Sub").Which;
        p.CsFiles.Should().Contain(f => f.EndsWith("Top.cs"));
        p.CsFiles.Should().NotContain(f => f.EndsWith("Inner.cs"));
        sub.CsFiles.Should().Contain(f => f.EndsWith("Inner.cs"));
    }

    [Fact]
    public void Scan_ExcludesFilesUnderNonSolutionSubCsproj()
    {
        WriteSlnx("App.slnx", "P/P.csproj"); // Sub is NOT referenced
        Write("P/P.csproj", MinimalCsproj);
        Write("P/Top.cs", "namespace P; public class Top { }");
        Write("P/Sub/Sub.csproj", MinimalCsproj);
        Write("P/Sub/Inner.cs", "namespace P.Sub; public class Inner { }");

        SolutionScanner.ScanResult r = Scan();

        SolutionScanner.ProjectInfo p = r.Projects.Should().ContainSingle().Which;
        p.Name.Should().Be("P");
        p.CsFiles.Should().Contain(f => f.EndsWith("Top.cs"));
        r.Timestamps.Keys.Should().NotContain(k => k.EndsWith("Inner.cs"));
    }

    [Fact]
    public void Scan_DesignerCsRequiresSiblingDbml()
    {
        WriteSlnx("App.slnx", "P/P.csproj");
        Write("P/P.csproj", MinimalCsproj);
        Write("P/Foo.Designer.cs", "namespace P; public partial class Foo { }");
        Write("P/Foo.dbml", "<Database/>");
        Write("P/Bar.Designer.cs", "namespace P; public partial class Bar { }"); // no sibling dbml
        Write("P/Baz.cs", "namespace P; public class Baz { }");

        SolutionScanner.ScanResult r = Scan();

        r.Timestamps.Keys.Should().Contain(k => k.EndsWith("Foo.Designer.cs"));
        r.Timestamps.Keys.Should().NotContain(k => k.EndsWith("Bar.Designer.cs"));
        r.Timestamps.Keys.Should().Contain(k => k.EndsWith("Baz.cs"));
    }

    [Fact]
    public void Scan_SkipsSolutionRefToMissingCsproj()
    {
        WriteSlnx("App.slnx", "Ghost/Ghost.csproj"); // does not exist on disk
        Write("Real/Real.csproj", MinimalCsproj);

        SolutionScanner.ScanResult r = Scan(); // must not throw
        r.Projects.Should().BeEmpty(); // Ghost not real; Real not referenced
    }

    [Fact]
    public void Scan_ClassicSlnAndSlnxBothDiscovered()
    {
        // Classic .sln with the Project(GUID) = "Name", "path" form.
        Write("Classic.sln",
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"ProjA\", \"ProjA\\ProjA.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
            "EndProject\n");
        Write("ProjA/ProjA.csproj", MinimalCsproj);
        Write("ProjA/A.cs", "namespace A; public class A { }");
        WriteSlnx("Modern.slnx", "ProjB/ProjB.csproj");
        Write("ProjB/ProjB.csproj", MinimalCsproj);
        Write("ProjB/B.cs", "namespace B; public class B { }");

        SolutionScanner.ScanResult r = Scan();

        r.Projects.Should().Contain(p => p.Name == "ProjA");
        r.Projects.Should().Contain(p => p.Name == "ProjB");
    }

    [Fact]
    public void Scan_DeterministicOrdering()
    {
        WriteSlnx("App.slnx", "Zeta/Zeta.csproj", "Alpha/Alpha.csproj");
        Write("Zeta/Zeta.csproj", MinimalCsproj);
        Write("Zeta/Bbb.cs", "namespace Z; public class Bbb { }");
        Write("Zeta/Aaa.cs", "namespace Z; public class Aaa { }");
        Write("Alpha/Alpha.csproj", MinimalCsproj);
        Write("Alpha/A.cs", "namespace Al; public class A { }");

        SolutionScanner.ScanResult r = Scan();

        r.Projects.Select(p => p.Name).ToList().Should().Equal("Alpha", "Zeta");
        SolutionScanner.ProjectInfo zeta = r.Projects.Single(p => p.Name == "Zeta");
        string.Compare(zeta.CsFiles[0], zeta.CsFiles[1], StringComparison.OrdinalIgnoreCase).Should().BeNegative();
    }

    // Loose-projects handling: a discovered .csproj that NO solution references is still indexed when looseProjects
    // is enabled (not reachable through the default Scan overload used above).
    [Fact]
    public void Scan_LooseProjects_IndexesUnreferencedCsproj()
    {
        Write("P/P.csproj", MinimalCsproj); // no solution references it
        Write("P/Loose.cs", "namespace P; public class Loose { }");

        SolutionScanner.ScanResult withoutLoose = new SolutionScanner(_fs).Scan(Root);
        SolutionScanner.ScanResult withLoose = new SolutionScanner(_fs).Scan(Root, looseProjects: true);

        withoutLoose.Projects.Should().BeEmpty();
        withLoose.Projects.Should().ContainSingle();
        withLoose.Projects[0].Name.Should().Be("P");
        withLoose.Projects[0].CsFiles.Should().Contain(f => f.EndsWith("Loose.cs"));
    }

    // Config exclude names are pruned in addition to the built-in set.
    [Fact]
    public void Scan_ExtraExcludes_ArePruned()
    {
        WriteSlnx("App.slnx", "P/P.csproj");
        Write("P/P.csproj", MinimalCsproj);
        Write("P/Real.cs", "namespace P; public class Real { }");
        Write("P/vendor/Vendored.cs", "namespace P; public class Vendored { }");

        SolutionScanner.ScanResult r = new SolutionScanner(_fs).Scan(Root, new[] { "vendor" });

        r.Timestamps.Keys.Should().Contain(k => k.EndsWith("Real.cs"));
        r.Timestamps.Keys.Should().NotContain(k => k.EndsWith("Vendored.cs"));
    }

    // A .cs with no ancestor .csproj at all belongs to no project and is not indexed.
    [Fact]
    public void Scan_FileWithNoAncestorCsproj_IsNotIndexed()
    {
        WriteSlnx("App.slnx", "P/P.csproj");
        Write("P/P.csproj", MinimalCsproj);
        Write("P/Owned.cs", "namespace P; public class Owned { }");
        Write("Orphan.cs", "namespace O; public class Orphan { }"); // sits at repo root, no csproj ancestor

        SolutionScanner.ScanResult r = Scan();

        r.Timestamps.Keys.Should().Contain(k => k.EndsWith("Owned.cs"));
        r.Timestamps.Keys.Should().NotContain(k => k.EndsWith("Orphan.cs"));
    }
}
