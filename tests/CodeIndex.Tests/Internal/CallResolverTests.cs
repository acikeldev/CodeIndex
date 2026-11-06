using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Receiver-type resolution behind call_hierarchy / trace_calls: an invocation binds to the type that declares
/// the method on the receiver's (syntactically determined) type, not to every same-named method in the repo.
/// Calls whose receiver type cannot be determined are marked '?' and never followed; calls that bind to a
/// framework/library type are not reported as callers of an indexed method.
/// </summary>
public class CallResolverTests
{
    private const string Root = @"C:\repo";

    private static List<SourceFileIndex> Index(InMemoryFileSystem fs, params (string Name, string Source)[] files)
    {
        List<SourceFileIndex> result = new();
        foreach ((string name, string source) in files)
        {
            fs.AddFile($@"{Root}\{name}", source);
            result.Add(new SourceFileParser(fs).Parse($@"{Root}\{name}", "Proj")!);
        }

        return result;
    }

    // Two screens each with their own Show(); the sale screen's button calls its own.
    private static (InMemoryFileSystem Fs, List<SourceFileIndex> Files) SameNamedMethods()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Sale.cs", """
                namespace N;
                class Sale
                {
                    void Button_Click() { Refresh(); }
                    void Refresh() { }
                }
                """),
            ("Old.cs", """
                namespace N;
                class Old
                {
                    public void Refresh() { }
                    void Load() { Refresh(); }
                }
                """));
        return (fs, files);
    }

    [Fact]
    public void Trace_UnqualifiedCall_BindsToOwnType_NotSameNamedElsewhere()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = SameNamedMethods();

        string res = CallHierarchy.Trace(fs, files, "Button_Click", "callees", null, 3, 40, includeBodies: false);

        res.Should().Contain("Sale.Refresh  [Sale.cs:");
        res.Should().NotContain("Old.Refresh");
    }

    [Fact]
    public void Callers_TypeQualified_KeepsOnlyThatTypesCallers()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = SameNamedMethods();

        string res = CallHierarchy.Callers(fs, files, "Old.Refresh", null, 40, 10);

        res.Should().Contain("Load");
        res.Should().NotContain("Button_Click");
        res.Should().Contain("1 call sites");
    }

    [Fact]
    public void Callers_Bare_LabelsEachResolvedTarget_AndNotesSameNamedTypes()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = SameNamedMethods();

        string res = CallHierarchy.Callers(fs, files, "Refresh", null, 40, 10);

        res.Should().Contain("Button_Click → Sale.Refresh");
        res.Should().Contain("Load → Old.Refresh");
        res.Should().Contain("2 types declare 'Refresh'");
    }

    [Fact]
    public void Trace_FieldTypedAsInterface_BindsToInterface_AndDescendsIntoImplementation()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Factory.cs", """
                namespace N;
                public interface IFactory { T Create<T>(); }
                public sealed class Factory : IFactory { public T Create<T>() { Build(); return default!; } void Build() { } }
                """),
            ("Report.cs", "namespace N;\nclass Report { public void Create<T>() { } }\n"),
            ("Screen.cs", """
                namespace N;
                class Screen
                {
                    private readonly IFactory _factory;
                    void Open() { _factory.Create<Screen>(); }
                }
                """));

        string res = CallHierarchy.Trace(fs, files, "Open", "callees", null, 3, 40, includeBodies: false);

        res.Should().Contain("IFactory.Create");
        res.Should().Contain("Factory.Create  [Factory.cs:");
        res.Should().Contain("implements IFactory.Create");
        res.Should().Contain("Factory.Build");   // reached through the implementation
        res.Should().NotContain("Report.Create");
    }

    [Fact]
    public void GenericMethodReturningT_TypesTheReceiverByTypeArgument()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Services.cs", "namespace N;\npublic static class Services { public static T Get<T>() => default!; }\n"),
            ("Store.cs", """
                namespace N;
                public interface IStore { void Save(); }
                public class Cache { public void Save() { } }
                """),
            ("Use.cs", "namespace N;\nclass Use { void Run() { Services.Get<IStore>().Save(); } }\n"));

        string res = CallHierarchy.Callees(fs, files, "Run", null, 40);

        res.Should().Contain("IStore.Save");
        res.Should().Contain("Services.Get");
        res.Should().NotContain("Cache.Save");
    }

    [Fact]
    public void LocalsParametersCastsAndAwait_AreTyped()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Model.cs", """
                namespace N;
                using System.Threading.Tasks;
                public class Order { public void Ship() { } }
                public class Repo { public Task<Order> LoadAsync() => null!; }
                public class Other { public void Ship() { } }
                """),
            ("Flow.cs", """
                namespace N;
                class Flow
                {
                    async void Run(Repo repo, object o)
                    {
                        var order = new Order();
                        order.Ship();
                        ((Order)o).Ship();
                        (o as Order)?.Ship();
                        (await repo.LoadAsync()).Ship();
                        Order typed = null;
                        typed.Ship();
                    }
                }
                """));

        string res = CallHierarchy.Callers(fs, files, "Order.Ship", null, 40, 10);

        res.Should().Contain("5 call sites");
        res.Should().NotContain("unresolved");
    }

    [Fact]
    public void FrameworkReceiver_IsNotACallerOfSameNamedIndexedMethod()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Bag.cs", "namespace N;\npublic class Bag { public void Add(int x) { } }\n"),
            ("Use.cs", """
                namespace N;
                using System.Collections.Generic;
                class Use
                {
                    void Run(Bag bag)
                    {
                        List<int> list = new();
                        list.Add(1);
                        bag.Add(2);
                        string.Format("{0}", 1).Trim();
                    }
                }
                """));

        string res = CallHierarchy.Callers(fs, files, "Add", null, 40, 10);

        res.Should().Contain("1 call sites");
        res.Should().Contain("bag.Add(2)");
        res.Should().NotContain("list.Add");
    }

    [Fact]
    public void FrameworkBase_InheritedUnqualifiedCall_IsExternal()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Dialog.cs", "namespace N;\npublic class Dialog { public void Show() { } }\n"),
            ("Win.cs", "namespace N;\nclass MainWindow : Window { void Open() { Show(); } }\n"));

        string res = CallHierarchy.Trace(fs, files, "Open", "callees", null, 2, 40, includeBodies: false);

        res.Should().Contain("Show  (external / not indexed)");
        res.Should().NotContain("Dialog.Show");
    }

    [Fact]
    public void InheritedFromIndexedBase_AndBaseCall_BindToBase()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Base.cs", "namespace N;\npublic class Screen { protected void Log() { } public virtual void Close() { } }\n"),
            ("Pay.cs", "namespace N;\nclass Pay : Screen { public override void Close() { Log(); base.Close(); } }\n"));

        string res = CallHierarchy.Callees(fs, files, "Pay.Close", null, 40);

        res.Should().Contain("Screen.Log");
        res.Should().Contain("1 distinct callees"); // base.Close() is the same name → filtered as self-recursion noise
    }

    [Fact]
    public void UnknownReceiver_IsMarkedUnresolved_AndNotFollowed()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Two.cs", """
                namespace N;
                public class A { public void Save() { Deep(); } void Deep() { } }
                public class B { public void Save() { } }
                """),
            ("Use.cs", """
                namespace N;
                using System;
                class Use { void Run(Func<object> f) { Each(x => x.Save()); } void Each(Action<dynamic> a) { } }
                """));

        string trace = CallHierarchy.Trace(fs, files, "Run", "callees", null, 3, 40, includeBodies: false);
        trace.Should().Contain("Save  (? receiver type not determined — 2 same-named: A.Save, B.Save)");
        trace.Should().NotContain("Deep"); // the guessed edge is not descended

        string callers = CallHierarchy.Callers(fs, files, "A.Save", null, 40, 10);
        callers.Should().Contain("1 call sites in 1 files (0 resolved, 1 unresolved '?')");
        callers.Should().Contain("? Run");
    }

    [Fact]
    public void Callers_ThroughInterface_ReachImplementation_WithViaNote()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files = Index(fs,
            ("Repo.cs", """
                namespace N;
                public interface IRepo { void Write(); }
                public sealed class SqlRepo : IRepo { public void Write() { } }
                public sealed class Unrelated { public void Write() { } }
                """),
            ("Use.cs", """
                namespace N;
                class Use
                {
                    private IRepo _repo;
                    void Run() { _repo.Write(); }
                    void Other(Unrelated u) { u.Write(); }
                }
                """));

        string trace = CallHierarchy.Trace(fs, files, "SqlRepo.Write", "callers", null, 2, 40, includeBodies: false);
        trace.Should().Contain("Use.Run");
        trace.Should().Contain("via IRepo.Write");
        trace.Should().NotContain("Use.Other");

        string callers = CallHierarchy.Callers(fs, files, "SqlRepo.Write", null, 40, 10);
        callers.Should().Contain("Run");
        callers.Should().NotContain("Other");
    }

    [Fact]
    public void Trace_AmbiguousRoot_TracesEachType()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = SameNamedMethods();

        string res = CallHierarchy.Trace(fs, files, "Refresh", "callers", null, 2, 40, includeBodies: false);

        res.Should().Contain("2 types declare 'Refresh'");
        res.Should().Contain("Sale.Button_Click");
        res.Should().Contain("Old.Load");
    }

    [Fact]
    public void Trace_TypeQualifiedRoot_Unknown_ReportsMiss()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = SameNamedMethods();

        CallHierarchy.Trace(fs, files, "Nope.Refresh", "callees", null, 2, 40).Should().Contain("no C# method named 'Nope.Refresh'");
        CallHierarchy.Callers(fs, files, "Nope.Refresh", null, 40, 10).Should().Contain("no C# method 'Nope.Refresh' is indexed");
    }

    [Theory]
    [InlineData("Save", null, "Save")]
    [InlineData("Repo.Save", "Repo", "Save")]
    [InlineData("N.Sub.Repo.Save", "Repo", "Save")]
    [InlineData(".Save", null, ".Save")]
    [InlineData("Repo.", null, "Repo.")]
    public void Split_TypeAndMethod(string input, string? type, string method)
    {
        CallHierarchy.Split(input).Should().Be((type, method));
    }

    [Theory]
    [InlineData("global::System.Collections.Generic.List<int>", "List")]
    [InlineData("Order?", "Order")]
    [InlineData("Order[]", "Order")]
    [InlineData("(int, string)", null)]
    public void SimpleName_StripsQualifiersAndDecorations(string raw, string? expected)
    {
        CallResolver.SimpleName(raw).Should().Be(expected);
    }
}
