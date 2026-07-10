using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>Phase 2: TS/SCSS workspace discovery — tsconfig/package boundaries, synthetic scss projects, pruning,
/// case-insensitive timestamps, and alias harvesting. Drives the real scanner through the in-memory file system so
/// no disk is touched.</summary>
public class TypeScriptWorkspaceScannerTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    private string Add(string relPath, string content)
    {
        string full = Path.Combine(Root, relPath.Replace('/', Path.DirectorySeparatorChar));
        _fs.AddFile(full, content);
        return full;
    }

    private TypeScriptWorkspaceScanner.TsScanResult Scan(IReadOnlyList<string>? extraExcludes = null) =>
        new TypeScriptWorkspaceScanner(_fs).Scan(Root, extraExcludes);

    [Fact]
    public void Scan_DiscoversTsconfigAndPackageBoundaries()
    {
        Add("packages/base/package.json", """{ "name": "myapp-base" }""");
        Add("packages/base/tsconfig.json", "{ }");
        string store = Add("packages/base/src/Store.ts", "export class TsStore { }");
        string styles = Add("packages/base/src/Styles.scss", ".toolbar { color: red; }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.Projects.Should().Contain(p => p.Name == "myapp-base");
        r.FileToProject[store].Should().Be("myapp-base");
        r.FileToProject[styles].Should().Be("myapp-base");
    }

    [Fact]
    public void Scan_SyntheticScssProject_ForOrphanScss()
    {
        string orphan = Add("standalone/lonely.scss", "%reset { margin: 0; }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[orphan].Should().Be("scss:standalone");
        r.Projects.Should().Contain(p => p.Name == "scss:standalone");
    }

    [Fact]
    public void Scan_SyntheticTsProject_ForOrphanTs()
    {
        // The "ts:" prefix branch mirrors the "scss:" one for an orphan .ts with no tsconfig/package ancestor.
        string orphan = Add("standalone/lonely.ts", "export const a = 1;");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[orphan].Should().Be("ts:standalone");
        r.Projects.Should().Contain(p => p.Name == "ts:standalone");
    }

    [Fact]
    public void Scan_PrunesNodeModulesDistNext()
    {
        Add("node_modules/pkg/index.ts", "export const x = 1;");
        Add("dist/bundle.ts", "export const y = 2;");
        Add("app/.next/page.ts", "export const z = 3;");
        string real = Add("app/real.ts", "export class Real { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.Timestamps.Keys.Should().Contain(real);
        r.Timestamps.Keys.Should().NotContain(k => k.Contains("node_modules"));
        r.Timestamps.Keys.Should().NotContain(k => k.Replace('\\', '/').Contains("/dist/"));
        r.Timestamps.Keys.Should().NotContain(k => k.Replace('\\', '/').Contains("/.next/"));
    }

    [Fact]
    public void Scan_ExtraExcludes_PruneCustomDir_AndIgnoreBlankEntries()
    {
        Add("secret/hidden.ts", "export const s = 1;");
        string real = Add("app/real.ts", "export class Real { }");

        // A blank/whitespace exclude is ignored; "secret" is added to the prune set on top of the defaults.
        TypeScriptWorkspaceScanner.TsScanResult r = Scan(new[] { "secret", "   " });

        r.Timestamps.Keys.Should().Contain(real);
        r.Timestamps.Keys.Should().NotContain(k => k.Replace('\\', '/').Contains("/secret/"));
    }

    [Fact]
    public void Scan_Timestamps_AreOrdinalIgnoreCase()
    {
        string p = Add("app/File.ts", "export class F { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        // A differently-cased key must resolve to the same entry (so a re-scan under different path casing is a
        // no-op in the delta, not churn).
        r.Timestamps.ContainsKey(p.ToUpperInvariant()).Should().BeTrue();
        r.FileToProject.ContainsKey(p.ToUpperInvariant()).Should().BeTrue();
    }

    [Fact]
    public void Scan_HarvestsAliasesAndPackageNames()
    {
        Add("packages/base/package.json", """{ "name": "myapp-base" }""");
        Add("packages/base/tsconfig.json", """
            {
              "compilerOptions": {
                "baseUrl": ".",
                "paths": { "myapp-base/*": ["src/*"] }
              }
            }
            """);
        Add("packages/base/src/A.ts", "export class A { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.Aliases.Packages.ContainsKey("myapp-base").Should().BeTrue();
        r.Aliases.Paths.Keys.Should().Contain("myapp-base/*");
        r.Aliases.Paths["myapp-base/*"].Should().Contain("src/*");
    }

    [Fact]
    public void Scan_CollidingLeafDirNames_StayDistinctProjects()
    {
        // Two package-less tsconfig boundary dirs both named "__tests__" must NOT collapse into one project
        // (which would drop files + be walk-order dependent). They stay distinct via a repo-relative disambiguator.
        Add("packages/base/tsconfig.json", "{ }");
        Add("packages/base/__tests__/tsconfig.json", "{ }");
        string t1 = Add("packages/base/__tests__/a.spec.ts", "export const a = 1;");
        Add("packages/web/tsconfig.json", "{ }");
        Add("packages/web/__tests__/tsconfig.json", "{ }");
        string t2 = Add("packages/web/__tests__/b.spec.ts", "export const b = 2;");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[t1].Should().NotBe(r.FileToProject[t2]);
        r.Projects.Count(p => p.Name.StartsWith("__tests__", StringComparison.Ordinal)).Should().Be(2);
        // Every project a file maps to must actually exist in the project list.
        r.Projects.Should().Contain(p => p.Name == r.FileToProject[t1]);
        r.Projects.Should().Contain(p => p.Name == r.FileToProject[t2]);
    }

    [Fact]
    public void Scan_IgnoresPackageLockAndParsesJsonc()
    {
        // package-lock.json must not become a project boundary; tsconfig with comments/trailing commas must parse.
        Add("app/package-lock.json", """{ "name": "should-not-be-used" }""");
        Add("app/tsconfig.json", """
            {
              // a comment
              "compilerOptions": { "paths": { "x": ["y"], }, },
            }
            """);
        string f = Add("app/A.ts", "export class A { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[f].Should().Be("app"); // boundary from tsconfig dir name (no package name)
        r.Projects.Should().NotContain(p => p.Name == "should-not-be-used");
        r.Aliases.Paths.Keys.Should().Contain("x");
    }

    [Fact]
    public void Scan_PackageOnlyBoundary_UsesPackageNameAndAlias()
    {
        // A directory with only package.json (no tsconfig) is still a boundary; its "name" is both the project
        // name and a harvested package alias.
        Add("libs/core/package.json", """{ "name": "myapp-core" }""");
        string f = Add("libs/core/lib/Thing.ts", "export class Thing { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[f].Should().Be("myapp-core");
        r.Aliases.Packages["myapp-core"].Should().Be(Path.Combine(Root, "libs", "core"));
    }

    [Fact]
    public void Scan_BlankPackageName_FallsBackToDirName()
    {
        // A whitespace/empty "name" is treated as absent → the boundary keeps the directory name.
        Add("libs/widget/package.json", """{ "name": "   " }""");
        string f = Add("libs/widget/index.ts", "export const w = 1;");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[f].Should().Be("widget");
        r.Aliases.Packages.Should().NotContainKey("   ");
    }

    [Fact]
    public void Scan_MalformedJson_IsToleratedAndBoundaryStillCounts()
    {
        // Unreadable/invalid package.json and tsconfig.json must not throw: the dir stays a boundary (dir name),
        // and no aliases are harvested from the broken tsconfig.
        Add("app/package.json", "this is not json {");
        Add("app/tsconfig.json", "{ not valid ]");
        string f = Add("app/A.ts", "export class A { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.FileToProject[f].Should().Be("app");
        r.Aliases.Paths.Should().BeEmpty();
        r.Aliases.Packages.Should().BeEmpty();
    }

    [Fact]
    public void Scan_TsconfigPaths_NonArrayTarget_YieldsEmptyTargetList()
    {
        // A paths value that is not a string array still registers the alias key with an empty target list.
        Add("app/tsconfig.json", """
            {
              "compilerOptions": { "paths": { "@bad": "not-an-array", "@mixed": ["ok", 5] } }
            }
            """);
        Add("app/A.ts", "export class A { }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.Aliases.Paths.Keys.Should().Contain("@bad");
        r.Aliases.Paths["@bad"].Should().BeEmpty();
        r.Aliases.Paths["@mixed"].Should().ContainSingle().Which.Should().Be("ok"); // non-string element skipped
    }

    [Fact]
    public void Scan_TsconfigBaseJson_HarvestedForAliases_ButNotAProjectBoundary()
    {
        // A tsconfig*.json that is NOT exactly "tsconfig.json" is an alias source but does not mark a boundary.
        Add("app/tsconfig.base.json", """
            { "compilerOptions": { "paths": { "@app/*": ["src/*"] } } }
            """);
        string orphan = Add("app/only.scss", ".x { color: blue; }");

        TypeScriptWorkspaceScanner.TsScanResult r = Scan();

        r.Aliases.Paths.Keys.Should().Contain("@app/*");
        // No tsconfig.json / package.json boundary → the .scss falls back to a synthetic project.
        r.FileToProject[orphan].Should().Be("scss:app");
    }
}
