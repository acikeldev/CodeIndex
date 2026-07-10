using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// call_hierarchy TS engine (tree-sitter): callers labelled by enclosing member, callees from the method body
/// with counts, plus the guard/empty/truncation/scope branches. These drive the analyzer directly over an
/// in-memory file system; the store/tool-level tests land with the store wave.
/// </summary>
public class TsCallHierarchyTests
{
    private const string Root = @"C:\repo\web";

    private static SourceFileIndex Parse(InMemoryFileSystem fs, string path, string project) =>
        new TypeScriptParser(fs).Parse(path, project)!;

    // TsTarget defined + called from two enclosing members (a class method and a const arrow); its own body
    // calls TsLog + TsSave (twice).
    private static (InMemoryFileSystem Fs, SourceFileIndex Index) Fixture()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Code.ts";
        fs.AddFile(path, """
            export class Svc {
                Runner(): void { this.TsTarget(); }
                TsTarget(): void { TsLog(); TsSave(); TsSave(); }
            }
            export const Kick = (): void => { new Svc().TsTarget(); };
            function TsLog(): void { }
            function TsSave(): void { }
            """);
        return (fs, Parse(fs, path, "Web"));
    }

    [Fact]
    public void Callers_LabelsEnclosingMembers()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = Fixture();

        string res = TsCallHierarchy.Callers(fs, [index], "TsTarget", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("Runner");    // this.TsTarget() inside Runner()
        res.Should().Contain("Kick");      // new Svc().TsTarget() inside the const-arrow Kick
        res.Should().Contain("Code.ts");
    }

    [Fact]
    public void Callees_ListsBodyCallsWithCounts()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = Fixture();

        string res = TsCallHierarchy.Callees(fs, [index], "TsTarget", project: null, max: 40);

        res.Should().Contain("TsLog");
        res.Should().Contain("TsSave");
        res.Should().Contain("×2");        // TsSave() called twice
    }

    [Fact]
    public void Callers_BlankMethod_ReturnsPrompt()
    {
        InMemoryFileSystem fs = new();

        string res = TsCallHierarchy.Callers(fs, [], "   ", project: null, max: 40, perFileCap: 5);

        res.Should().Be("call_hierarchy(TS): provide a method name.");
    }

    [Fact]
    public void Callees_BlankMethod_ReturnsPrompt()
    {
        InMemoryFileSystem fs = new();

        string res = TsCallHierarchy.Callees(fs, [], "", project: null, max: 40);

        res.Should().Be("call_hierarchy(TS): provide a method name.");
    }

    [Fact]
    public void Callers_NoInvocations_ReturnsNone()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = Fixture();

        string res = TsCallHierarchy.Callers(fs, [index], "NeverCalled", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("no invocations of 'NeverCalled' found");
        res.Should().Contain("name-based, TS/TSX only");
    }

    [Fact]
    public void Callees_NoDefiner_ReturnsNotIndexed()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = Fixture();

        string res = TsCallHierarchy.Callees(fs, [index], "Missing", project: null, max: 40);

        res.Should().Contain("no TS method named 'Missing' is indexed");
    }

    [Fact]
    public void Callees_DefinerWithEmptyBody_ReturnsNoInvocations()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Empty.ts";
        fs.AddFile(path, """
            export function DoesNothing(): void { }
            """);
        SourceFileIndex index = Parse(fs, path, "Web");

        string res = TsCallHierarchy.Callees(fs, [index], "DoesNothing", project: null, max: 40);

        res.Should().Contain("(no invocations found in the method body)");
    }

    [Fact]
    public void Callees_Truncates_WhenDistinctCalleesExceedMax()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Many.ts";
        fs.AddFile(path, """
            export function Fan(): void { A(); B(); C(); }
            function A(): void { }
            function B(): void { }
            function C(): void { }
            """);
        SourceFileIndex index = Parse(fs, path, "Web");

        string res = TsCallHierarchy.Callees(fs, [index], "Fan", project: null, max: 1);

        res.Should().Contain("2 more (raise max)");
    }

    [Fact]
    public void Callers_Truncates_WhenMaxExceeded()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = MultiCallFixture();

        string res = TsCallHierarchy.Callers(fs, [index], "t", project: null, max: 1, perFileCap: 5);

        res.Should().Contain("showing 1 of 3");
    }

    [Fact]
    public void Callers_PerFileCap_LimitsHitsPerFile()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = MultiCallFixture();

        string res = TsCallHierarchy.Callers(fs, [index], "t", project: null, max: 40, perFileCap: 2);

        res.Should().Contain("showing 2 of 3");
    }

    [Fact]
    public void Callers_ProjectScope_FiltersAndAnnotatesHeader()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = Fixture();

        string res = TsCallHierarchy.Callers(fs, [index], "TsTarget", project: "Web", max: 40, perFileCap: 5);
        res.Should().Contain("in project 'Web'");

        string filtered = TsCallHierarchy.Callers(fs, [index], "TsTarget", project: "Other", max: 40, perFileCap: 5);
        filtered.Should().Contain("no invocations of 'TsTarget' found in project 'Other'");
    }

    [Fact]
    public void Callers_ReadFailure_SkippedGracefully()
    {
        (InMemoryFileSystem fs, SourceFileIndex index) = Fixture();
        fs.DeleteFile(index.SourceFilePath); // parse succeeded; now the on-demand re-read fails

        string res = TsCallHierarchy.Callers(fs, [index], "TsTarget", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("no invocations of 'TsTarget' found");
    }

    private static (InMemoryFileSystem Fs, SourceFileIndex Index) MultiCallFixture()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Multi.ts";
        fs.AddFile(path, """
            export class Multi {
                A(): void { t(); t(); t(); }
            }
            function t(): void { }
            """);
        return (fs, Parse(fs, path, "Web"));
    }
}
