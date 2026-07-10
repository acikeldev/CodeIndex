using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;
using TypeInfo = CodeIndex.Models.TypeInfo;

namespace CodeIndex.Tests.Parsing;

/// <summary>Wave 3 (TS/SCSS Phase 1): tree-sitter TypeScript/TSX extraction into SourceFileIndex.</summary>
public class TypeScriptParserTests
{
    private static SourceFileIndex ParseTs(string source, string ext = ".ts")
    {
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\TsParse" + ext;
        fs.AddFile(path, source);
        SourceFileIndex? r = new TypeScriptParser(fs).Parse(path, "TestTsProj");
        r.Should().NotBeNull();
        return r!;
    }

    [Fact]
    public void ExtractsTopLevelDeclarations()
    {
        SourceFileIndex r = ParseTs("""
            export class Store { }
            export interface IThing { }
            export enum Color { Red, Green, Blue }
            export type Id = string | number;
            export function helper(x: number): void { }
            export const MAX = 100;
            """);

        Type(r, "Store").Kind.Should().Be(SymbolKind.Class);
        Type(r, "IThing").Kind.Should().Be(SymbolKind.Interface);
        Type(r, "Color").Kind.Should().Be(SymbolKind.Enum);
        Type(r, "Color").EnumValues.Should().Be("Red, Green, Blue");
        Type(r, "Id").Kind.Should().Be(SymbolKind.TypeAlias);
        Type(r, "helper").Kind.Should().Be(SymbolKind.Function);
        Type(r, "MAX").Kind.Should().Be(SymbolKind.Variable);
    }

    [Fact]
    public void ExtractsClassMembers()
    {
        SourceFileIndex r = ParseTs("""
            export class C {
                private count: number = 0;
                public readonly Name: string;
                constructor(x: number) { }
                GetById(id: number): C { return this; }
                get Total(): number { return 0; }
                private async Save(): Promise<void> { }
            }
            """);

        TypeInfo c = Type(r, "C");
        c.Members.Should().Contain(m => m.Name == "constructor" && m.Kind == SymbolKind.Constructor);
        c.Members.Should().Contain(m => m.Name == "GetById" && m.Kind == SymbolKind.Method);
        c.Members.Should().Contain(m => m.Name == "Save" && m.Kind == SymbolKind.Method);
        c.Members.Should().Contain(m => m.Name == "count" && m.Kind == SymbolKind.Field);
        c.Members.Should().Contain(m => m.Name == "Name" && m.Kind == SymbolKind.Field);
        // Signature carries the parameter list.
        c.Members.Should().Contain(m => m.Name == "GetById" && m.Signature.Contains("(id: number)"));
        // Return annotation is captured, trimmed of the leading colon/space.
        c.Members.Should().Contain(m => m.Name == "GetById" && m.ReturnType == "C");
    }

    [Fact]
    public void ExtractsInterfaceMembers()
    {
        SourceFileIndex r = ParseTs("""
            export interface IThing {
                id: number;
                compute(x: string): void;
            }
            """);

        TypeInfo t = Type(r, "IThing");
        t.Members.Should().Contain(m => m.Name == "id" && m.Kind == SymbolKind.Property);
        t.Members.Should().Contain(m => m.Name == "compute" && m.Kind == SymbolKind.Method);
    }

    [Fact]
    public void ScopesTypesByNamespace()
    {
        SourceFileIndex r = ParseTs("""
            export namespace DTOs {
                export interface IHashtag { id: number; }
                export class Tag { }
            }
            """);

        Type(r, "IHashtag").Namespace.Should().Be("DTOs");
        Type(r, "Tag").Namespace.Should().Be("DTOs");
    }

    [Fact]
    public void IndexesNonExportedTopLevel()
    {
        SourceFileIndex r = ParseTs("class Internal { m() { } }");
        Type(r, "Internal").Kind.Should().Be(SymbolKind.Class);
    }

    [Fact]
    public void ParsesTsxComponent()
    {
        SourceFileIndex r = ParseTs("""
            export const App = () => <div className={styles.toolbar}>hi</div>;
            export class Panel extends React.Component { render() { return <span/>; } }
            """, ext: ".tsx");

        r.Types.Should().Contain(t => t.Name == "App" && t.Kind == SymbolKind.Variable);
        r.Types.Should().Contain(t => t.Name == "Panel" && t.Kind == SymbolKind.Class);
    }

    // ── coverage additions (behavior-preserving; not in the original oracle) ────────

    [Fact]
    public void ReturnsNullWhenSourceUnreadable()
    {
        InMemoryFileSystem fs = new();
        SourceFileIndex? r = new TypeScriptParser(fs).Parse(@"C:\repo\web\missing.ts", "TestTsProj");
        r.Should().BeNull();
    }

    [Fact]
    public void ClassifiesAbstractClass()
    {
        SourceFileIndex r = ParseTs("export abstract class Base { protected step(): void { } }");

        TypeInfo b = Type(r, "Base");
        b.Kind.Should().Be(SymbolKind.AbstractClass);
        b.TypeKeyword.Should().Be("abstract class");
    }

    [Fact]
    public void ExtractsGeneratorFunction()
    {
        SourceFileIndex r = ParseTs("export function* gen(): Iterator<number> { yield 1; }");
        Type(r, "gen").Kind.Should().Be(SymbolKind.Function);
    }

    [Fact]
    public void ExtractsVarLetAndMultipleDeclarators()
    {
        SourceFileIndex r = ParseTs("""
            var legacy = 1, other = 2;
            let mutable = 3;
            """);

        Type(r, "legacy").Kind.Should().Be(SymbolKind.Variable);
        Type(r, "legacy").TypeKeyword.Should().Be("var");
        Type(r, "other").TypeKeyword.Should().Be("var");
        Type(r, "mutable").Kind.Should().Be(SymbolKind.Variable);
        Type(r, "mutable").TypeKeyword.Should().Be("const");
    }

    [Fact]
    public void CombinesNestedNamespaces()
    {
        SourceFileIndex r = ParseTs("""
            export namespace Outer {
                export namespace Inner {
                    export class Deep { }
                }
            }
            """);

        Type(r, "Deep").Namespace.Should().Be("Outer.Inner");
    }

    [Fact]
    public void ScopesTypesByModuleKeyword()
    {
        SourceFileIndex r = ParseTs("""
            module M {
                export class Widget { }
            }
            """);

        Type(r, "Widget").Namespace.Should().Be("M");
    }

    [Fact]
    public void ExtractsEnumWithAssignments()
    {
        SourceFileIndex r = ParseTs("export enum Status { Active = 1, Inactive = 2 }");
        Type(r, "Status").EnumValues.Should().Be("Active, Inactive");
    }

    [Fact]
    public void EmptyEnumHasNoValues()
    {
        SourceFileIndex r = ParseTs("export enum Empty { }");
        Type(r, "Empty").Kind.Should().Be(SymbolKind.Enum);
        Type(r, "Empty").EnumValues.Should().BeNull();
    }

    [Fact]
    public void IgnoresExportStatementWithoutInnerDeclaration()
    {
        SourceFileIndex r = ParseTs("""
            class Kept { }
            export { Kept };
            """);

        // The re-export carries no inner declaration, so only the class itself is indexed.
        r.Types.Should().ContainSingle(t => t.Name == "Kept");
    }

    [Fact]
    public void CollapsesMultiLineSignatureToOneLine()
    {
        SourceFileIndex r = ParseTs("""
            export interface IWide {
                doThing(
                    a: number,
                    b: string
                ): void;
            }
            """);

        TypeInfo t = Type(r, "IWide");
        t.Members.Should().Contain(m => m.Name == "doThing" && m.Signature.Contains("…"));
    }

    private static TypeInfo Type(SourceFileIndex r, string name) =>
        r.Types.Should().ContainSingle(t => t.Name == name).Which;
}
