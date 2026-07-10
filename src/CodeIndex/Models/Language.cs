namespace CodeIndex.Models;

/// <summary>
/// Which source language a <see cref="SourceFileIndex"/> was parsed from. Lets C#-semantic readers
/// (notably resolve_bare_name, which reasons about C# usings/namespaces) scope to <see cref="CSharp"/>
/// and skip TS/SCSS rows instead of emitting cross-language garbage. Byte-backed so MessagePack stores
/// it compactly; the default (0 = <see cref="CSharp"/>) is what MessagePack fills in for the missing key
/// on a pre-Phase-2 cache row — which is correct, since the C# cache only ever holds C# files.
/// </summary>
public enum Language : byte
{
    CSharp = 0,
    TypeScript = 1,
    Scss = 2,
}
