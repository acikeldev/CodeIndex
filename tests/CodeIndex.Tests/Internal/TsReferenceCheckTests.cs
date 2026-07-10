using CodeIndex.Internal;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Direct tests for <see cref="TsReferenceCheck.Analyze"/> — the syntactic import/declaration/use extractor behind
/// check_dangling_references. The company's CheckDanglingReferencesTests all exercise the analyzer indirectly through
/// CodeIndexStore/CheckDanglingReferencesTool, so those land with the store/tools waves; these cover the analyzer's
/// own tree-sitter logic (import bindings, destructuring binds, qualified references, declarations, first-use lines).
/// </summary>
public class TsReferenceCheckTests
{
    private const string Root = @"C:\repo\web";

    private static TsReferenceCheck.Result Analyze(string fileName, string source)
    {
        InMemoryFileSystem fs = new();
        string path = $@"{Root}\{fileName}";
        fs.AddFile(path, source);
        TsReferenceCheck.Result? result = TsReferenceCheck.Analyze(fs, path);
        result.Should().NotBeNull();
        return result!;
    }

    [Fact]
    public void UnreadableFile_ReturnsNull()
    {
        InMemoryFileSystem fs = new(); // nothing added → ReadAllText throws FileNotFoundException (an IOException)
        TsReferenceCheck.Result? result = TsReferenceCheck.Analyze(fs, $@"{Root}\Missing.ts");
        result.Should().BeNull();
    }

    [Fact]
    public void CollectsImportBindings_DefaultNamespaceNamedAndAlias_StripsModuleQuotes()
    {
        TsReferenceCheck.Result r = Analyze("Imports.ts", """
            import D from 'modA';
            import * as NS from 'modB';
            import { A, B as C } from './modC';
            import './side-effect';
            """);

        r.Imports.Should().Contain(i => i.Name == "D" && i.Module == "modA" && i.Line == 1);
        r.Imports.Should().Contain(i => i.Name == "NS" && i.Module == "modB" && i.Line == 2);
        r.Imports.Should().Contain(i => i.Name == "A" && i.Module == "./modC" && i.Line == 3);
        r.Imports.Should().Contain(i => i.Name == "C" && i.Module == "./modC" && i.Line == 3); // alias wins over "B"
        r.Imports.Should().NotContain(i => i.Name == "B"); // the pre-alias name is not the binding
        r.Imports.Should().HaveCount(4); // side-effect import contributes no binding
    }

    [Fact]
    public void ImportAlias_RegistersBinding_AndCountsRootOfRhsAsUsed()
    {
        // `import X = Foo.Bar;` — X is the binding; Foo (root of the RHS) is a real use; Bar (a member) is not.
        TsReferenceCheck.Result r = Analyze("Alias.ts", "import X = Foo.Bar;");

        r.Imports.Should().Contain(i => i.Name == "X" && i.Module == "(alias)");
        r.Used.Should().ContainKey("Foo");
        r.Used.Should().NotContainKey("Bar"); // member of the qualified reference — not a bare use
        r.Used.Should().NotContainKey("X");    // the binding itself is not counted as a use
    }

    [Fact]
    public void DestructuringPatterns_CollectBindNames_WithoutTreatingDefaultsAsUses()
    {
        TsReferenceCheck.Result r = Analyze("Destructure.ts", """
            const { Foo, Bar: baz } = props;
            const [ x, y ] = arr;
            const { a, ...rest } = obj;
            const { z = def } = obj2;
            """);

        // Every bound name (shorthand, renamed pair value, array elements, rest, assignment-pattern left).
        r.Declared.Should().Contain(new[] { "Foo", "baz", "x", "y", "a", "rest", "z" });

        // The right-hand sides are real uses.
        r.Used.Should().ContainKeys("props", "arr", "obj", "obj2");

        // Bound names are not counted as uses, and a pattern default's identifier is deliberately not descended into.
        r.Used.Should().NotContainKey("Foo");
        r.Used.Should().NotContainKey("def");
    }

    [Fact]
    public void CollectsDeclarations_AcrossTypesFunctionsNamespacesParametersAndTypeParameters()
    {
        TsReferenceCheck.Result r = Analyze("Decls.ts", """
            class MyClass<T> { }
            interface IShape { }
            enum Color { Red, Green }
            type Alias = string;
            function doIt(req: number, opt?: string) { }
            namespace NsA { }
            """);

        r.Declared.Should().Contain(new[]
        {
            "MyClass", "T", "IShape", "Color", "Alias", "doIt", "req", "opt", "NsA",
        });
    }

    [Fact]
    public void QualifiedTypeReference_KeepsRootOnly_AndMemberAccessPropertyIsNotAUse()
    {
        TsReferenceCheck.Result r = Analyze("Refs.ts", """
            let v: Ns.Inner;
            class C extends Base { }
            foo.bar();
            """);

        r.Used.Should().ContainKey("Ns");   // root of the qualified type reference
        r.Used.Should().ContainKey("Base"); // extends clause reference
        r.Used.Should().ContainKey("foo");  // object side of member access

        r.Used.Should().NotContainKey("Inner"); // trailing member of nested_type_identifier
        r.Used.Should().NotContainKey("bar");   // property_identifier side of member access
    }

    [Fact]
    public void UsedIdentifier_RecordsFirstUseLine()
    {
        TsReferenceCheck.Result r = Analyze("FirstUse.ts", """
            const a = 1;
            const b = x;
            const c = x;
            """);

        r.Used.Should().ContainKey("x");
        r.Used["x"].Should().Be(2); // first occurrence line (1-based), not the later re-use
    }

    [Fact]
    public void TsxFile_UsesTsxGrammar_ForJsxComponentUse()
    {
        // Exercises the .tsx grammar branch; the JSX element name is collected as a use of the imported component.
        TsReferenceCheck.Result r = Analyze("App.tsx", """
            import { Widget } from './widget';
            export function App() {
                return <Widget />;
            }
            """);

        r.Imports.Should().Contain(i => i.Name == "Widget" && i.Module == "./widget");
        r.Used.Should().ContainKey("Widget");
    }
}
