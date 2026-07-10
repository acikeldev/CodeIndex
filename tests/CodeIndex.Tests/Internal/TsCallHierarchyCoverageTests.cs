using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Extra branch coverage for the TS call-hierarchy engine: the defensive read-failure catches (via a throwing
/// <see cref="IFileSystem"/> substitute), the const-arrow definer path in FindDefinitions, the multi-file
/// caller ordering + outer max-break in RenderCallers, and the InvokedName default (non-identifier callee).
/// </summary>
public class TsCallHierarchyCoverageTests
{
    private const string Root = @"C:\repo\web";

    private static SourceFileIndex Parse(InMemoryFileSystem fs, string path, string project) =>
        new TypeScriptParser(fs).Parse(path, project)!;

    private static (InMemoryFileSystem Fs, SourceFileIndex Index) SvcFixture()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Code.ts";
        fs.AddFile(path, """
            export class Svc {
                Runner(): void { this.TsTarget(); }
                TsTarget(): void { TsLog(); }
            }
            function TsLog(): void { }
            """);
        return (fs, Parse(fs, path, "Web"));
    }

    // Line 35: Callers swallows UnauthorizedAccessException from the on-demand re-read.
    [Fact]
    public void Callers_UnauthorizedAccess_SkippedGracefully()
    {
        (_, SourceFileIndex index) = SvcFixture();
        IFileSystem throwing = Substitute.For<IFileSystem>();
        throwing.When(x => x.ReadAllText(Arg.Any<string>())).Do(_ => throw new UnauthorizedAccessException());

        string res = TsCallHierarchy.Callers(throwing, [index], "TsTarget", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("no invocations of 'TsTarget' found");
    }

    // Line 73: Callees swallows IOException from the definer re-read.
    [Fact]
    public void Callees_IOException_YieldsNoInvocations()
    {
        (_, SourceFileIndex index) = SvcFixture();
        IFileSystem throwing = Substitute.For<IFileSystem>();
        throwing.When(x => x.ReadAllText(Arg.Any<string>())).Do(_ => throw new IOException());

        string res = TsCallHierarchy.Callees(throwing, [index], "TsTarget", project: null, max: 40);

        res.Should().Contain("(no invocations found in the method body)");
    }

    // Line 74: Callees swallows UnauthorizedAccessException from the definer re-read.
    [Fact]
    public void Callees_UnauthorizedAccess_YieldsNoInvocations()
    {
        (_, SourceFileIndex index) = SvcFixture();
        IFileSystem throwing = Substitute.For<IFileSystem>();
        throwing.When(x => x.ReadAllText(Arg.Any<string>())).Do(_ => throw new UnauthorizedAccessException());

        string res = TsCallHierarchy.Callees(throwing, [index], "TsTarget", project: null, max: 40);

        res.Should().Contain("(no invocations found in the method body)");
    }

    // Lines 163-167: a const-arrow definer (variable_declarator whose value is an arrow_function) resolves
    // through the FindDefinitions variable-declarator branch, and its body's calls are reported.
    [Fact]
    public void Callees_ConstArrowDefiner_ReportsBodyCalls()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Arrow.ts";
        fs.AddFile(path, """
            export const Kick = (): void => { Helper(); };
            function Helper(): void { }
            """);
        SourceFileIndex index = Parse(fs, path, "Web");

        string res = TsCallHierarchy.Callees(fs, [index], "Kick", project: null, max: 40);

        res.Should().Contain("Helper");
    }

    // Line 235 (InvokedName default): a call whose function is a subscript_expression (arr[0]()) is neither an
    // identifier nor a member_expression, so it maps to null and is skipped; the plain call is still reported.
    [Fact]
    public void Callees_NonIdentifierCallee_IsSkipped()
    {
        InMemoryFileSystem fs = new();
        string path = Root + @"\Sub.ts";
        fs.AddFile(path, """
            const handlers = [(): void => { }];
            export function Sub(): void { handlers[0](); Regular(); }
            function Regular(): void { }
            """);
        SourceFileIndex index = Parse(fs, path, "Web");

        string res = TsCallHierarchy.Callees(fs, [index], "Sub", project: null, max: 40);

        res.Should().Contain("Regular");
        res.Should().NotContain("handlers");
    }

    // Lines 182-183 (byFile ordering key selectors, exercised with >1 group) and 193-194 (outer file-loop break
    // once emitted >= max on a later file): two files each invoke `t`; FileA has more hits so it sorts first,
    // and with max = FileA's hit count the loop breaks before FileB is emitted.
    [Fact]
    public void Callers_MultiFile_OrdersByCount_AndBreaksOnOuterMax()
    {
        InMemoryFileSystem fs = new();
        string pathA = Root + @"\FileA.ts";
        string pathB = Root + @"\FileB.ts";
        fs.AddFile(pathA, """
            export class A {
                Go(): void { t(); t(); }
            }
            function t(): void { }
            """);
        fs.AddFile(pathB, """
            export class B {
                Go(): void { t(); }
            }
            """);
        SourceFileIndex indexA = Parse(fs, pathA, "Web");
        SourceFileIndex indexB = Parse(fs, pathB, "Web");

        string res = TsCallHierarchy.Callers(fs, [indexA, indexB], "t", project: null, max: 2, perFileCap: 5);

        res.Should().Contain("3 call sites in 2 files");   // total across both files
        res.Should().Contain("FileA.ts");                  // higher-count file emitted first
        res.Should().Contain("showing 2 of 3");            // outer break fired before FileB
        res.Should().NotContain("== FileB.ts");            // FileB header never emitted (outer break at 193)
    }
}
