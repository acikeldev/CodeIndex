namespace CodeIndex.Models;

public enum SymbolKind
{
    Class,
    StaticClass,
    AbstractClass,
    SealedClass,
    Interface,
    Enum,
    Method,
    Property,
    Field,
    Constructor,
    Event,
    // Appended (never reorder — MessagePack serializes enums by ordinal value).
    Struct,
    Record,
    RecordStruct,
    // TypeScript (language-neutral additions).
    Function,
    Variable,
    TypeAlias,
    // SCSS.
    ScssSelector,
    ScssMixin,
    ScssFunction,
    ScssVariable,
    ScssPlaceholder
}
