using MessagePack;

namespace CodeIndex.Models;

/// <summary>
/// How a runtime edge was expressed in source. Drives detection and how <c>find_references</c> renders it.
/// APPEND-ONLY — MessagePack serializes enums by ordinal value, so never reorder or remove members.
/// </summary>
public enum RegKind
{
    /// <summary>Closed-generic DI registration: <c>services.AddSingleton&lt;IFoo, Foo&gt;()</c>.</summary>
    DiGeneric,
    /// <summary>Factory-lambda DI registration: <c>services.AddSingleton&lt;IFoo&gt;(sp =&gt; new Foo(...))</c>.</summary>
    DiFactory,
    /// <summary>Instance DI registration: <c>services.AddSingleton&lt;IFoo&gt;(instance)</c>.</summary>
    DiInstance,
    /// <summary>Open-generic DI registration: <c>services.AddTransient(typeof(IFoo&lt;,&gt;), typeof(Foo&lt;,&gt;))</c>.</summary>
    DiOpenGeneric,
    /// <summary>Overriding DI registration via <c>services.Replace(ServiceDescriptor.Singleton&lt;IFoo, Foo&gt;())</c>
    /// or a bare <c>ServiceDescriptor</c>. The JOIN treats this as overriding earlier candidates for the service.</summary>
    DiReplace,
    /// <summary>A call to a <c>this IServiceCollection AddXyz(...)</c> module extension method; the real registrations
    /// live in that method body and are linked by <see cref="RuntimeRegistration.EnclosingMember"/> identity.</summary>
    DiModule,
    /// <summary>Service-locator consumption: <c>...ServiceProvider.GetRequiredService&lt;IFoo&gt;()</c> (consumer → interface).</summary>
    LocatorConsume,
    /// <summary>Legacy hardcoded construction: <c>new Lazy&lt;IFoo&gt;(() =&gt; new Foo())</c> field init (no container).</summary>
    LegacyNew,
    /// <summary>Reflective instantiation: <c>Activator.CreateInstance(typeof(X))</c> / literal <c>Type.GetType("X")</c>.</summary>
    Reflection,
    /// <summary>Polymorphic serialization hint: <c>[KnownType(typeof(Derived))]</c> (base → derived candidate).</summary>
    KnownType,
    /// <summary>Plugin dispatch: <c>[ExtensionOf(typeof(SomeExtensionPoint))]</c> (impl → extension point).</summary>
    ExtensionOf,
    /// <summary>Delegate/method-group wiring: <c>new Thread(Handler)</c>, <c>evt += Handler</c>, compiled-query Func.
    /// A real syntactic use that a naive "is this method ever called?" check misses. NOT reflection.</summary>
    DelegateWiring,
}

/// <summary>
/// How confident the extractor is that the recovered edge is the effective runtime binding.
/// APPEND-ONLY (serialized by ordinal). Rendered as ◆ (Resolved) / ◑ (Partial) / ⚠ (Dynamic).
/// </summary>
public enum RuntimeEdgeConfidence
{
    /// <summary>Both endpoints are literal in source and the binding is unconditional and un-overridden.</summary>
    Resolved,
    /// <summary>One leg was inferred (cross-file JOIN, module hop, single-branch factory) or the registration is
    /// conditional / overridable — plausible but not confirmed as the effective binding.</summary>
    Partial,
    /// <summary>The target is not statically knowable (branching factory, assembly scan, string/config/variable
    /// reflection). Recorded as explicitly unresolvable — never dropped, never guessed into a concrete edge.</summary>
    Dynamic,
}

/// <summary>
/// One runtime-wiring site captured syntactically from a single file (a DI registration, a service-locator
/// consumption, a legacy construction, or a reflective target). Stored per-file on <see cref="SourceFileIndex"/>;
/// the solution-wide interface→implementation closure is a derived, non-serialized graph built over these.
/// </summary>
[MessagePackObject]
public sealed class RuntimeRegistration
{
    [Key(0)] public required RegKind Kind { get; init; }
    /// <summary>The service/interface type (simple name, resolved to a declaration by the JOIN). Null when the
    /// site has no service type (e.g. a pure reflective construction with only a target).</summary>
    [Key(1)] public string? ServiceTypeName { get; init; }
    /// <summary>The implementation/target type (simple name). Null ⇒ dynamic / not statically known.</summary>
    [Key(2)] public string? ImplTypeName { get; init; }
    /// <summary>Lifetime for DI edges (Singleton/Scoped/Transient); null when not applicable.</summary>
    [Key(3)] public string? Lifetime { get; init; }
    [Key(4)] public required int StartLine { get; init; }
    [Key(5)] public required RuntimeEdgeConfidence Confidence { get; init; }
    /// <summary>Provenance text — the source expression this edge was read from, for verifiable output.</summary>
    [Key(6)] public string? Snippet { get; init; }
    /// <summary>The type that lexically encloses this site. Lets the JOIN scope module/consumer edges by identity
    /// without retaining syntax trees (they are discarded after parse).</summary>
    [Key(7)] public string? EnclosingType { get; init; }
    /// <summary>The method that lexically encloses this site (e.g. the <c>AddXyz</c> module method or the consumer
    /// member). Required for the 2-hop <see cref="RegKind.DiModule"/> JOIN.</summary>
    [Key(8)] public string? EnclosingMember { get; init; }
    /// <summary>True when the registration sits inside an <c>if</c>/<c>switch</c>/conditional expression, so it is
    /// not unconditionally wired. Forces <see cref="RuntimeEdgeConfidence.Partial"/> rendering.</summary>
    [Key(9)] public bool Conditional { get; init; }
}
