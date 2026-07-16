using MessagePack;

namespace CodeIndex.Models;

/// <summary>
/// A SITE-based framework root — a place that marks some OTHER declaration as runtime-invoked (so it must not be
/// reported dead), where the mark originates away from that declaration. Declaration-local roots (a method's own
/// <c>[OperationContract]</c>/<c>[Fact]</c>, a property's <c>[DataMember]</c>) are cheaper as the
/// <see cref="MemberRootKind"/>/<see cref="TypeRootKind"/> bitflags on the declaration itself; discrete records
/// here are reserved for the genuinely sparse cross-declaration cases.
/// APPEND-ONLY (serialized by ordinal).
/// </summary>
public enum FrameworkRootKind
{
    /// <summary><c>new ServiceHost(typeof(X))</c> — hosts X; X is a root even with no C# caller.</summary>
    ServiceHost,
    /// <summary>A literal reflective instantiation target (<c>Activator.CreateInstance(typeof(X))</c>) — X is a root.</summary>
    ReflectionTarget,
    /// <summary>A <c>[KnownType(typeof(X))]</c> target declared on another type.</summary>
    KnownType,
    /// <summary>An <c>[ExtensionOf(typeof(Point))]</c> plugin discovered by reflection at a foreign extension point.</summary>
    ExtensionOf,
}

/// <summary>
/// A "this declaration is invoked by the framework — treat as used" mark whose evidence lives at a different
/// site than the declaration. Its only job is to stop a dead-code / <c>likely_unused</c> pass from reporting
/// framework-invoked types/members as dead, and to annotate <c>find_references</c>.
/// </summary>
[MessagePackObject]
public sealed class FrameworkRootMark
{
    /// <summary>The type kept alive (simple name, resolved to a declaration by the JOIN).</summary>
    [Key(0)] public required string TargetTypeName { get; init; }
    /// <summary>The specific member kept alive, when the mark is member-scoped; null ⇒ whole type.</summary>
    [Key(1)] public string? TargetMemberName { get; init; }
    [Key(2)] public required FrameworkRootKind Kind { get; init; }
    /// <summary>Line of the marking SITE (e.g. the <c>ServiceHost</c> construction), for provenance.</summary>
    [Key(3)] public required int StartLine { get; init; }
}
