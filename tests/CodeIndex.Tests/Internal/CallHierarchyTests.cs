using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>call_hierarchy: heuristic C# callers (invocations labelled by enclosing member) and callees
/// (what a method invokes), with generated-file exclusion and project scoping. Exercises the analyzer
/// directly against an in-memory file system (the store/tool wrappers are covered in their own waves).</summary>
public class CallHierarchyTests
{
    private const string Root = @"C:\repo";

    private static SourceFileIndex Parse(InMemoryFileSystem fs, string path, string project = "Proj")
    {
        return new SourceFileParser(fs).Parse(path, project)!;
    }

    private static (InMemoryFileSystem Fs, List<SourceFileIndex> Files) BuildFixture()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Uses.cs", """
            namespace N;
            class A { void M1() { Target(); } }
            class B { void M2() { new T().Target(); } }
            """);
        fs.AddFile(Root + @"\Def.cs", """
            namespace N;
            class T { public void Target() { Log(); Save(); Save(); } void Log() { } void Save() { } }
            """);
        // Generated caller — must be excluded from 'callers'.
        fs.AddFile(Root + @"\Gen.g.cs", "namespace N;\nclass G { void GM() { Target(); } }\n");

        List<SourceFileIndex> files =
        [
            Parse(fs, Root + @"\Uses.cs"),
            Parse(fs, Root + @"\Def.cs"),
            Parse(fs, Root + @"\Gen.g.cs"),
        ];
        return (fs, files);
    }

    [Fact]
    public void Callers_ReportsEnclosingMembers_ExcludesGenerated()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callers(fs, files, "Target", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("M1");
        res.Should().Contain("M2");            // `new T().Target()` member-access invocation
        res.Should().Contain("Uses.cs");
        res.Should().NotContain("Gen.g.cs");   // generated caller excluded
    }

    [Fact]
    public void Callees_ListsInvokedMethodsWithCounts()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callees(fs, files, "Target", project: null, max: 40);

        res.Should().Contain("Log");
        res.Should().Contain("Save");
        res.Should().Contain("×2");            // Save() called twice
        res.Should().Contain("distinct callees");
        res.Should().Contain("Def.cs");
    }

    [Fact]
    public void Callees_UnknownMethod_Message()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callees(fs, files, "NoSuchMethod", project: null, max: 40);

        res.Should().Contain("no C# method named 'NoSuchMethod'");
    }

    [Fact]
    public void Callers_NoMatchInProject_Message()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callers(fs, files, "Target", project: "NoSuchProject", max: 40, perFileCap: 5);

        res.Should().Contain("no invocations of 'Target'");
    }

    [Fact]
    public void Callers_ProjectScope_KeepsMatchingProject()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callers(fs, files, "Target", project: "Proj", max: 40, perFileCap: 5);

        res.Should().Contain("M1");
        res.Should().Contain("in project 'Proj'");
    }

    [Fact]
    public void Callers_EmptyMethod_Message()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callers(fs, files, "   ", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("provide a method name");
    }

    [Fact]
    public void Callees_EmptyMethod_Message()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();

        string res = CallHierarchy.Callees(fs, files, "", project: null, max: 40);

        res.Should().Contain("provide a method name");
    }

    [Fact]
    public void Callees_NoInvocationsInBody_Message()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Empty.cs", """
            namespace N;
            class E { public void Empty() { } }
            """);
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Empty.cs")];

        string res = CallHierarchy.Callees(fs, files, "Empty", project: null, max: 40);

        res.Should().Contain("no invocations found in the method body");
    }

    [Fact]
    public void Callees_ExcludesSelfRecursion()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Rec.cs", """
            namespace N;
            class R { public void Recur() { Recur(); Helper(); } void Helper() { } }
            """);
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Rec.cs")];

        string res = CallHierarchy.Callees(fs, files, "Recur", project: null, max: 40);

        res.Should().Contain("Helper");
        res.Should().Contain("(1 distinct callees)"); // self-call filtered out of the summary
    }

    [Fact]
    public void Callees_TruncatesToMax()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Many.cs", """
            namespace N;
            class M { public void Many() { Aa(); Bb(); Cc(); } void Aa() { } void Bb() { } void Cc() { } }
            """);
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Many.cs")];

        string res = CallHierarchy.Callees(fs, files, "Many", project: null, max: 2);

        res.Should().Contain("… 1 more (raise max)");
    }

    [Fact]
    public void Callers_TruncatesWithPerFileCap_AndFooter()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Hot.cs", """
            namespace N;
            class H { void C() { Target(); Target(); Target(); } }
            """);
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Hot.cs")];

        string res = CallHierarchy.Callers(fs, files, "Target", project: null, max: 40, perFileCap: 2);

        res.Should().Contain("3 call sites");
        res.Should().Contain("showing 2 of 3 call sites");
    }

    [Fact]
    public void Callers_InvokedNameVariants_AllMatch()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Variants.cs", """
            namespace N;
            class V
            {
                T Field;
                void Ident() { Target(); }
                void Gen() { Target<int>(); }
                void Bind() { Field?.Target(); }
            }
            """);
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Variants.cs")];

        string res = CallHierarchy.Callers(fs, files, "Target", project: null, max: 40, perFileCap: 10);

        res.Should().Contain("Ident");   // IdentifierNameSyntax
        res.Should().Contain("Gen");     // GenericNameSyntax  (Target<int>())
        res.Should().Contain("Bind");    // MemberBindingExpressionSyntax (Field?.Target())
        res.Should().Contain("3 call sites");
    }

    [Fact]
    public void Callers_EnclosingMemberLabels()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Enclosing.cs", """
            namespace N;
            class C
            {
                public C() { Target(); }
                void Outer() { void Local() { Target(); } Local(); }
                int Prop { get { Target(); return 0; } }
                int Expr => Wrap();
            }
            """);
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Enclosing.cs")];

        string callers = CallHierarchy.Callers(fs, files, "Target", project: null, max: 40, perFileCap: 10);
        callers.Should().Contain("C(ctor)");   // ConstructorDeclarationSyntax
        callers.Should().Contain("Local");     // LocalFunctionStatementSyntax
        callers.Should().Contain("Prop.get");  // AccessorDeclarationSyntax under a property

        string exprBodied = CallHierarchy.Callers(fs, files, "Wrap", project: null, max: 40, perFileCap: 10);
        exprBodied.Should().Contain("Expr(property)"); // expression-bodied PropertyDeclarationSyntax
    }

    [Fact]
    public void Callers_FileLevelInvocation_Labelled()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(Root + @"\Top.cs", "Target();\n");
        List<SourceFileIndex> files = [Parse(fs, Root + @"\Top.cs")];

        string res = CallHierarchy.Callers(fs, files, "Target", project: null, max: 40, perFileCap: 10);

        res.Should().Contain("(file-level)");
    }

    [Fact]
    public void Callers_SkipsUnreadableFile()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();
        // Index still references Uses.cs, but the file is gone from disk — must be skipped, not throw.
        fs.DeleteFile(Root + @"\Uses.cs");

        string res = CallHierarchy.Callers(fs, files, "Target", project: null, max: 40, perFileCap: 5);

        res.Should().NotContain("M1");                  // Uses.cs could not be read
        res.Should().Contain("no invocations of 'Target'");
    }

    [Fact]
    public void Callees_SkipsUnreadableDefiner()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildFixture();
        fs.DeleteFile(Root + @"\Def.cs"); // definer file gone

        string res = CallHierarchy.Callees(fs, files, "Target", project: null, max: 40);

        // Definer still indexed, so no "not indexed" message, but body can't be read → no callees.
        res.Should().Contain("no invocations found in the method body");
    }
}
