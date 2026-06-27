namespace CodeIndex.Models;

/// <summary>
/// Classifies a C# symbol for filtering and display purposes.
/// </summary>
public enum SymbolKind
{
    Class,
    StaticClass,
    AbstractClass,
    SealedClass,
    Interface,
    Enum,
    Struct,
    Record,
    Method,
    Property,
    Field,
    Constructor,
    Event,
}
