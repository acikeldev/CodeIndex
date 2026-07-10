namespace CodeIndex.Models;

/// <summary>One candidate binding for a bare (unqualified) type name — see <c>resolve_bare_name</c>.</summary>
public sealed record BareNameCandidate(string FullyQualified, string Namespace, string SourceFilePath, string Via);

/// <summary>
/// What a bare type name binds to in a given file's using/alias scope. An alias wins outright
/// (<paramref name="AliasTarget"/>); otherwise <paramref name="InScope"/> holds candidates reachable via the
/// file's namespace/usings and <paramref name="OutOfScope"/> holds same-named types that exist but are not imported.
/// </summary>
public sealed record BareNameResolution(
    bool FileFound,
    string? FileNamespace,
    string? AliasTarget,
    List<BareNameCandidate> InScope,
    List<BareNameCandidate> OutOfScope);
