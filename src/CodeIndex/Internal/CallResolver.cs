using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>How confidently an invocation was bound to an indexed method.</summary>
internal enum CallResolutionKind
{
    /// <summary>The receiver's type was determined and the method found on it (or its bases) in the index.</summary>
    Resolved,

    /// <summary>The receiver's type could not be determined syntactically; <see cref="CallResolution.Targets"/>
    /// lists every indexed type declaring a method of that name (a name match, not a binding).</summary>
    Unresolved,

    /// <summary>The call goes outside the index (framework/library type, local function, or no indexed method of
    /// that name exists).</summary>
    External,
}

/// <summary>One invocation's binding. <see cref="Targets"/> holds full type keys ("Ns.Type").</summary>
internal readonly record struct CallResolution(CallResolutionKind Kind, string Name, IReadOnlyList<string> Targets);

/// <summary>
/// Syntax-only receiver-type resolution for C# invocations — no compilation, no semantic model. Binds `X()`,
/// `this.X()`, `base.X()`, `field.X()`, `local.X()`, `param.X()`, `Type.X()`, `new T().X()`, `((T)x).X()`,
/// `a.b.X()`, `a?.X()`, `M().X()` and `(await M()).X()` to the declaring type by reading declared types of locals,
/// parameters, fields and properties (from the index) and walking base types. When the receiver's type cannot be
/// determined the call is reported as <see cref="CallResolutionKind.Unresolved"/> with its name-match candidates
/// rather than guessed — a wrong edge presented as fact is worse than an honest "unknown".
/// Not handled (reported Unresolved/External): overload selection, `var` from non-trivial expressions, lambdas'
/// implicitly-typed parameters, generic type substitution beyond `M&lt;T&gt;()` returning `T`, `using static`.
/// </summary>
internal sealed partial class CallResolver
{
    private const int MaxDepth = 8;

    private static readonly HashSet<string> PredefinedTypes = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "nint", "nuint", "long",
        "ulong", "short", "ushort", "object", "string", "dynamic", "void",
    };

    private static readonly HashSet<string> TaskTypes = new(StringComparer.Ordinal) { "Task", "ValueTask" };

    private readonly Dictionary<string, List<TypeDecl>> _byFull = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _bySimple = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _definersByMethod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _derived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _bases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalBase = new(StringComparer.Ordinal);

    /// <summary>One declaration of a type (a partial type has several, possibly in different files).</summary>
    internal sealed record TypeDecl(Models.TypeInfo Type, SourceFileIndex File);

    /// <summary>A method's definition site, keyed "Ns.Type.Method" (overloads collapse to the first).</summary>
    internal sealed record MethodSite(string TypeKey, string TypeName, SourceFileIndex File, MemberInfo Member, bool OnInterface);

    public CallResolver(IReadOnlyList<SourceFileIndex> files)
    {
        foreach (SourceFileIndex f in files)
        {
            if (f.Language is not (Language.CSharp or Language.Xaml)) // XAML: the x:Class partial with its x:Name fields
            {
                continue;
            }

            foreach (Models.TypeInfo t in f.Types)
            {
                string key = Key(t.Namespace, t.Name);
                if (!_byFull.TryGetValue(key, out List<TypeDecl>? decls))
                {
                    decls = new List<TypeDecl>();
                    _byFull[key] = decls;
                    Add(_bySimple, t.Name, key);
                }

                decls.Add(new TypeDecl(t, f));
                foreach (MemberInfo m in t.Members)
                {
                    if (m.Kind == Models.SymbolKind.Method)
                    {
                        AddDistinct(_definersByMethod, m.Name, key);
                    }
                }
            }
        }

        // Base-type edges, resolved through each declaring file's namespace/usings. A base whose name is not
        // indexed (Window, Exception, …) marks the type as having an external base: an unqualified call that
        // is not found on the indexed part of the hierarchy is then most likely inherited from the framework.
        foreach ((string key, List<TypeDecl> decls) in _byFull)
        {
            foreach (TypeDecl d in decls)
            {
                foreach (string raw in d.Type.BaseTypes ?? [])
                {
                    string? simple = SimpleName(raw);
                    List<string> resolved = simple is null ? [] : TypeKeys(simple, d.File, d.Type.Namespace);
                    if (resolved.Count == 0)
                    {
                        _externalBase.Add(key);
                        continue;
                    }

                    foreach (string b in resolved.Where(b => b != key))
                    {
                        AddDistinct(_bases, key, b);
                        AddDistinct(_derived, b, key);
                    }
                }
            }
        }
    }

    /// <summary>Full keys of indexed types declaring a method named <paramref name="method"/>.</summary>
    public IReadOnlyList<string> Definers(string method) =>
        _definersByMethod.TryGetValue(method, out List<string>? list) ? list : [];

    /// <summary>True when any indexed C# type declares a method of that name.</summary>
    public bool IsIndexedMethod(string method) => _definersByMethod.ContainsKey(method);

    /// <summary>Simple (display) name of a type key.</summary>
    public string TypeName(string typeKey) =>
        _byFull.TryGetValue(typeKey, out List<TypeDecl>? decls) ? decls[0].Type.Name : typeKey;

    /// <summary>Distinct files declaring (a part of) <paramref name="typeKey"/>.</summary>
    public IEnumerable<SourceFileIndex> DeclaringFiles(string typeKey) =>
        _byFull.TryGetValue(typeKey, out List<TypeDecl>? decls)
            ? decls.Select(d => d.File).DistinctBy(f => f.SourceFilePath, StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>The definition site of <paramref name="typeKey"/>.<paramref name="method"/>, or null.</summary>
    public MethodSite? Site(string typeKey, string method)
    {
        if (!_byFull.TryGetValue(typeKey, out List<TypeDecl>? decls))
        {
            return null;
        }

        foreach (TypeDecl d in decls)
        {
            MemberInfo? m = d.Type.Members.FirstOrDefault(x => x.Kind == Models.SymbolKind.Method && x.Name == method);
            if (m is not null)
            {
                return new MethodSite(typeKey, d.Type.Name, d.File, m, d.Type.Kind == Models.SymbolKind.Interface);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="typeKey"/> is <paramref name="ancestorKey"/> or (transitively) derives
    /// from / implements it.</summary>
    public bool IsOrDerivesFrom(string typeKey, string ancestorKey)
    {
        if (typeKey == ancestorKey)
        {
            return true;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> todo = new();
        todo.Push(typeKey);
        while (todo.Count > 0)
        {
            string t = todo.Pop();
            if (!seen.Add(t) || !_bases.TryGetValue(t, out List<string>? bs))
            {
                continue;
            }

            foreach (string b in bs)
            {
                if (b == ancestorKey)
                {
                    return true;
                }

                todo.Push(b);
            }
        }

        return false;
    }

    /// <summary>Types (transitively) deriving from / implementing <paramref name="typeKey"/> that declare
    /// <paramref name="method"/> themselves — the bodies a call through an interface/base can reach.</summary>
    public List<string> Overriders(string typeKey, string method)
    {
        List<string> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal) { typeKey };
        Queue<string> todo = new();
        todo.Enqueue(typeKey);
        while (todo.Count > 0)
        {
            string t = todo.Dequeue();
            if (!_derived.TryGetValue(t, out List<string>? ds))
            {
                continue;
            }

            foreach (string d in ds)
            {
                if (!seen.Add(d))
                {
                    continue;
                }

                if (Site(d, method) is not null)
                {
                    result.Add(d);
                }

                todo.Enqueue(d);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>Types declaring <paramref name="method"/> nearest to <paramref name="typeKey"/> in its hierarchy
    /// (the type itself, else its bases) — what a call on that type binds to. Empty when none.</summary>
    public List<string> Lookup(string typeKey, string method) => FindInHierarchy(typeKey, method, out _);

    /// <summary>The full key of the type declaration enclosing <paramref name="node"/>, or null at file level.</summary>
    public static string? EnclosingTypeKey(SyntaxNode node)
    {
        TypeDeclarationSyntax? type = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return type is null ? null : Key(NamespaceOf(type), type.Identifier.Text);
    }

    /// <summary>Bind <paramref name="inv"/> to its declaring type(s).</summary>
    public CallResolution Resolve(InvocationExpressionSyntax inv, FileScope scope) => Resolve(inv, scope, 0);

    private CallResolution Resolve(InvocationExpressionSyntax inv, FileScope scope, int depth)
    {
        string? name = InvokedName(inv);
        if (name is null)
        {
            return new CallResolution(CallResolutionKind.External, string.Empty, []);
        }

        if (!_definersByMethod.TryGetValue(name, out List<string>? candidates))
        {
            return new CallResolution(CallResolutionKind.External, name, []);
        }

        switch (inv.Expression)
        {
            case IdentifierNameSyntax:
            case GenericNameSyntax:
                return ResolveImplicit(inv, name, candidates);

            case MemberAccessExpressionSyntax ma when ma.Expression is ThisExpressionSyntax:
                return ResolveImplicit(inv, name, candidates);

            case MemberAccessExpressionSyntax ma when ma.Expression is BaseExpressionSyntax:
                return ResolveOnBases(inv, name, candidates);

            case MemberAccessExpressionSyntax ma:
                return ResolveOnReceiver(TypeOf(ma.Expression, scope, depth + 1), name, candidates, scope);

            case MemberBindingExpressionSyntax:
                ConditionalAccessExpressionSyntax? ca = inv.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                    .FirstOrDefault(c => c.WhenNotNull.Span.Contains(inv.Span));
                return ca is null
                    ? Unresolved(name, candidates)
                    : ResolveOnReceiver(TypeOf(ca.Expression, scope, depth + 1), name, candidates, scope);

            default:
                return Unresolved(name, candidates);
        }
    }

    // X() / this.X(): the enclosing type's hierarchy, then outer types (nested), then a local function.
    private CallResolution ResolveImplicit(InvocationExpressionSyntax inv, string name, List<string> candidates)
    {
        bool sawExternalBase = false;
        foreach (TypeDeclarationSyntax type in inv.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            string key = Key(NamespaceOf(type), type.Identifier.Text);
            List<string> found = FindInHierarchy(key, name, out bool external);
            if (found.Count > 0)
            {
                return new CallResolution(CallResolutionKind.Resolved, name, found);
            }

            sawExternalBase |= external || !_byFull.ContainsKey(key);
        }

        if (inv.Ancestors().OfType<LocalFunctionStatementSyntax>().Any(l => l.Identifier.Text == name)
            || EnclosingMemberBody(inv)?.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any(l => l.Identifier.Text == name) == true)
        {
            return new CallResolution(CallResolutionKind.External, name, []);
        }

        return sawExternalBase ? new CallResolution(CallResolutionKind.External, name, []) : Unresolved(name, candidates);
    }

    private CallResolution ResolveOnBases(InvocationExpressionSyntax inv, string name, List<string> candidates)
    {
        string? self = EnclosingTypeKey(inv);
        if (self is null || !_bases.TryGetValue(self, out List<string>? bases))
        {
            return self is not null && _externalBase.Contains(self)
                ? new CallResolution(CallResolutionKind.External, name, [])
                : Unresolved(name, candidates);
        }

        bool anyExternal = _externalBase.Contains(self);
        List<string> found = new();
        foreach (string b in bases)
        {
            found.AddRange(FindInHierarchy(b, name, out bool external));
            anyExternal |= external;
        }

        if (found.Count > 0)
        {
            return new CallResolution(CallResolutionKind.Resolved, name, found.Distinct(StringComparer.Ordinal).ToList());
        }

        return anyExternal ? new CallResolution(CallResolutionKind.External, name, []) : Unresolved(name, candidates);
    }

    private CallResolution ResolveOnReceiver(TypeRef receiver, string name, List<string> candidates, FileScope scope)
    {
        if (receiver.Unknown)
        {
            return Unresolved(name, candidates);
        }

        List<string> keys = receiver.Keys(this, scope);
        if (keys.Count == 0)
        {
            // Not an indexed type (framework/library/predefined). An indexed static class declaring the name may
            // still be an extension method on it — say so instead of claiming External.
            List<string> extensionHosts = StaticHosts(candidates);
            return extensionHosts.Count > 0 && !receiver.IsPredefined
                ? Unresolved(name, extensionHosts)
                : new CallResolution(CallResolutionKind.External, name, []);
        }

        List<string> found = new();
        bool anyExternal = false;
        foreach (string k in keys)
        {
            found.AddRange(FindInHierarchy(k, name, out bool external));
            anyExternal |= external;
        }

        if (found.Count > 0)
        {
            return new CallResolution(CallResolutionKind.Resolved, name, found.Distinct(StringComparer.Ordinal).ToList());
        }

        // Indexed receiver without the method: with a fully indexed hierarchy only an extension method can
        // bind; with a framework base the method is most likely inherited from it.
        List<string> hosts = StaticHosts(candidates);
        if (hosts.Count > 0)
        {
            return anyExternal ? Unresolved(name, hosts) : new CallResolution(CallResolutionKind.Resolved, name, hosts);
        }

        return anyExternal ? new CallResolution(CallResolutionKind.External, name, []) : Unresolved(name, candidates);
    }

    private List<string> StaticHosts(List<string> candidates) =>
        candidates.Where(c => _byFull.TryGetValue(c, out List<TypeDecl>? d) && d.Any(x => x.Type.Kind == Models.SymbolKind.StaticClass)).ToList();

    private static CallResolution Unresolved(string name, IReadOnlyList<string> candidates) =>
        new(CallResolutionKind.Unresolved, name, candidates);

    // Nearest declaration of `method` walking up from `typeKey` (the type itself wins over its bases).
    private List<string> FindInHierarchy(string typeKey, string method, out bool externalBase)
    {
        externalBase = false;
        List<string> found = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<string> todo = new();
        todo.Enqueue(typeKey);
        while (todo.Count > 0)
        {
            string t = todo.Dequeue();
            if (!seen.Add(t))
            {
                continue;
            }

            if (Site(t, method) is not null)
            {
                found.Add(t);
                continue; // nearest declaration on this path — do not climb past it
            }

            externalBase |= _externalBase.Contains(t);
            if (_bases.TryGetValue(t, out List<string>? bs))
            {
                foreach (string b in bs)
                {
                    todo.Enqueue(b);
                }
            }
        }

        return found;
    }

    // ── Expression typing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A receiver's type as far as syntax tells: unknown, or a type name (raw text kept for generic
    /// unwrapping), plus whether it is a C# keyword type.</summary>
    internal readonly record struct TypeRef(string? Raw, bool Unknown, string? Namespace)
    {
        public static readonly TypeRef None = new(null, true, null);

        public bool IsPredefined => Raw is not null && PredefinedTypes.Contains(Raw.Trim().TrimEnd('?'));

        public static TypeRef Of(string? raw, string? ns = null) =>
            string.IsNullOrWhiteSpace(raw) || raw.Trim() == "var" ? None : new TypeRef(raw.Trim(), false, ns);

        public List<string> Keys(CallResolver r, FileScope scope)
        {
            string? simple = Raw is null ? null : SimpleName(Raw);
            return simple is null || PredefinedTypes.Contains(simple) ? [] : r.TypeKeys(simple, scope.File, Namespace ?? scope.File.Namespace);
        }
    }

    private TypeRef TypeOf(ExpressionSyntax expr, FileScope scope, int depth)
    {
        if (depth > MaxDepth)
        {
            return TypeRef.None;
        }

        switch (expr)
        {
            case ParenthesizedExpressionSyntax p:
                return TypeOf(p.Expression, scope, depth + 1);
            case CastExpressionSyntax c:
                return TypeRef.Of(c.Type.ToString());
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression):
                return TypeRef.Of(b.Right.ToString());
            case ObjectCreationExpressionSyntax o:
                return TypeRef.Of(o.Type.ToString());
            case PredefinedTypeSyntax pt:
                return TypeRef.Of(pt.Keyword.Text);
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
            case InterpolatedStringExpressionSyntax:
                return TypeRef.Of("string");
            case ThisExpressionSyntax:
                return TypeOfEnclosing(expr);
            case AwaitExpressionSyntax a:
                return UnwrapTask(TypeOf(a.Expression, scope, depth + 1));
            case InvocationExpressionSyntax inv:
                return ReturnTypeOf(inv, scope, depth + 1);
            case IdentifierNameSyntax id:
                return TypeOfName(id, id.Identifier.Text, scope);
            case GenericNameSyntax g:
                return TypeOfName(g, g.Identifier.Text, scope);
            case MemberAccessExpressionSyntax ma:
                return TypeOfMemberAccess(ma, scope, depth);
            case ElementAccessExpressionSyntax ea:
                TypeRef arr = TypeOf(ea.Expression, scope, depth + 1);
                return !arr.Unknown && arr.Raw!.EndsWith("[]", StringComparison.Ordinal)
                    ? TypeRef.Of(arr.Raw[..^2], arr.Namespace)
                    : TypeRef.None;
            default:
                return TypeRef.None;
        }
    }

    private static TypeRef TypeOfEnclosing(SyntaxNode node)
    {
        TypeDeclarationSyntax? type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return type is null ? TypeRef.None : TypeRef.Of(type.Identifier.Text, NamespaceOf(type));
    }

    // a.b where b is a field/property of a's type, or a namespace-qualified type name (System.IO.File).
    private TypeRef TypeOfMemberAccess(MemberAccessExpressionSyntax ma, FileScope scope, int depth)
    {
        string member = ma.Name.Identifier.Text;
        TypeRef owner = TypeOf(ma.Expression, scope, depth + 1);
        if (!owner.Unknown)
        {
            foreach (string k in owner.Keys(this, scope))
            {
                TypeRef t = MemberTypeInHierarchy(k, member);
                if (!t.Unknown)
                {
                    return t;
                }
            }

            return TypeRef.None;
        }

        // Qualified type name: the right-most segment names an indexed type.
        return _bySimple.ContainsKey(member) ? TypeRef.Of(member) : TypeRef.None;
    }

    // An identifier: a local/parameter in scope, else a field/property of the enclosing type chain, else a type.
    private TypeRef TypeOfName(ExpressionSyntax id, string name, FileScope scope)
    {
        (bool declared, TypeRef local) = scope.Local(id, name, this);
        if (declared)
        {
            return local;
        }

        foreach (TypeDeclarationSyntax type in id.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            TypeRef m = MemberTypeInHierarchy(Key(NamespaceOf(type), type.Identifier.Text), name);
            if (!m.Unknown)
            {
                return m;
            }
        }

        if (_bySimple.ContainsKey(name))
        {
            return TypeRef.Of(name);
        }

        // PascalCase and neither a local nor an indexed member/type: a framework type used statically (Math,
        // File, Application…) or a member inherited from a framework base — outside the index either way.
        return name.Length > 0 && char.IsUpper(name[0]) ? TypeRef.Of(name) : TypeRef.None;
    }

    private TypeRef MemberTypeInHierarchy(string typeKey, string member)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<string> todo = new();
        todo.Enqueue(typeKey);
        while (todo.Count > 0)
        {
            string t = todo.Dequeue();
            if (!seen.Add(t) || !_byFull.TryGetValue(t, out List<TypeDecl>? decls))
            {
                continue;
            }

            foreach (TypeDecl d in decls)
            {
                MemberInfo? m = d.Type.Members.FirstOrDefault(x =>
                    x.Name == member && x.Kind is Models.SymbolKind.Field or Models.SymbolKind.Property or Models.SymbolKind.Event);
                if (m is not null)
                {
                    return TypeRef.Of(m.ReturnType, d.Type.Namespace);
                }
            }

            if (_bases.TryGetValue(t, out List<string>? bs))
            {
                foreach (string b in bs)
                {
                    todo.Enqueue(b);
                }
            }
        }

        return TypeRef.None;
    }

    // The return type of a resolved call; `M<T>()` declared as returning `T` yields the type argument.
    private TypeRef ReturnTypeOf(InvocationExpressionSyntax inv, FileScope scope, int depth)
    {
        CallResolution r = Resolve(inv, scope, depth);
        if (r.Kind != CallResolutionKind.Resolved || r.Targets.Count != 1)
        {
            return r.Kind == CallResolutionKind.External && InvokedName(inv) is "ToString" ? TypeRef.Of("string") : TypeRef.None;
        }

        MethodSite? site = Site(r.Targets[0], r.Name);
        if (site is null)
        {
            return TypeRef.None;
        }

        string ret = site.Member.ReturnType;
        SimpleNameSyntax? invoked = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name,
            MemberBindingExpressionSyntax mb => mb.Name,
            SimpleNameSyntax s => s,
            _ => null,
        };
        if (invoked is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } g && TypeParameterLike().IsMatch(ret))
        {
            return TypeRef.Of(g.TypeArgumentList.Arguments[0].ToString());
        }

        return TypeRef.Of(ret, site.File.Types.FirstOrDefault(t => t.Name == site.TypeName)?.Namespace);
    }

    private static TypeRef UnwrapTask(TypeRef t)
    {
        if (t.Unknown)
        {
            return t;
        }

        string raw = t.Raw!;
        int lt = raw.IndexOf('<', StringComparison.Ordinal);
        if (lt > 0 && raw.EndsWith('>') && TaskTypes.Contains(SimpleName(raw[..lt]) ?? string.Empty))
        {
            return TypeRef.Of(raw[(lt + 1)..^1], t.Namespace);
        }

        return t;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Indexed type keys for a simple name, preferring the caller's namespace, then its usings.</summary>
    internal List<string> TypeKeys(string simple, SourceFileIndex file, string? ns)
    {
        if (file.UsingAliases.TryGetValue(simple, out string? alias))
        {
            simple = SimpleName(alias) ?? simple;
        }

        if (!_bySimple.TryGetValue(simple, out List<string>? keys))
        {
            return [];
        }

        if (keys.Count == 1)
        {
            return keys;
        }

        List<string> sameNs = keys.Where(k => NamespaceOfKey(k, simple) == (ns ?? string.Empty)).ToList();
        if (sameNs.Count > 0)
        {
            return sameNs;
        }

        List<string> imported = keys.Where(k => file.Usings.Contains(NamespaceOfKey(k, simple))).ToList();
        return imported.Count > 0 ? imported : keys;
    }

    private static string NamespaceOfKey(string key, string simple) =>
        key.Length > simple.Length ? key[..(key.Length - simple.Length - 1)] : string.Empty;

    /// <summary>Strips `global::`, namespace qualifiers, generic arguments, nullable and array markers.</summary>
    internal static string? SimpleName(string raw)
    {
        string s = raw.Trim();
        if (s.Length == 0 || s[0] == '(')
        {
            return null; // tuple
        }

        if (s.StartsWith("global::", StringComparison.Ordinal))
        {
            s = s["global::".Length..];
        }

        int lt = s.IndexOf('<', StringComparison.Ordinal);
        if (lt >= 0)
        {
            s = s[..lt];
        }

        s = s.TrimEnd('?', ']', '[', '*');
        int dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : s;
    }

    internal static string Key(string? ns, string name) => string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

    internal static string? NamespaceOf(SyntaxNode node)
    {
        List<string> parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>The simple name being invoked: X(), this.X(), a.b.X(), a?.X(), X&lt;T&gt;(), a.X&lt;T&gt;().</summary>
    internal static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        _ => null,
    };

    internal static SyntaxNode? EnclosingMemberBody(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax
            or FieldDeclarationSyntax or GlobalStatementSyntax);

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out List<string>? list))
        {
            list = new List<string>();
            map[key] = list;
        }

        list.Add(value);
    }

    private static void AddDistinct(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out List<string>? list))
        {
            list = new List<string>();
            map[key] = list;
        }

        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }

    [GeneratedRegex(@"^T(\p{Lu}\w*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeParameterLike();

    /// <summary>
    /// Per-file resolution state: the file's index entry and a per-member cache of declared locals/parameters
    /// (built once per member so resolving every invocation in a large method stays linear). Not thread-safe —
    /// one instance per file per thread.
    /// </summary>
    internal sealed class FileScope(SourceFileIndex file)
    {
        private readonly Dictionary<SyntaxNode, Dictionary<string, SyntaxNode>> _locals = new();

        public SourceFileIndex File { get; } = file;

        /// <summary>Whether <paramref name="name"/> is declared as a local/parameter in the member enclosing
        /// <paramref name="at"/>, and its type when the declaration states it (or `var x = new T()` etc.).</summary>
        public (bool Declared, TypeRef Type) Local(SyntaxNode at, string name, CallResolver resolver)
        {
            SyntaxNode? body = EnclosingMemberBody(at);
            if (body is null)
            {
                return (false, TypeRef.None);
            }

            if (!_locals.TryGetValue(body, out Dictionary<string, SyntaxNode>? map))
            {
                map = Collect(body);
                _locals[body] = map;
            }

            if (!map.TryGetValue(name, out SyntaxNode? decl))
            {
                // A setter's implicit `value` has the property's type.
                if (name == "value" && at.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault() is { } acc
                    && acc.Parent?.Parent is PropertyDeclarationSyntax prop)
                {
                    return (true, TypeRef.Of(prop.Type.ToString()));
                }

                return (false, TypeRef.None);
            }

            return (true, decl switch
            {
                ParameterSyntax p => TypeRef.Of(p.Type?.ToString()),
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax vd } v when vd.Type.IsVar =>
                    v.Initializer is null ? TypeRef.None : resolver.TypeOf(v.Initializer.Value, this, 1),
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax vd } => TypeRef.Of(vd.Type.ToString()),
                ForEachStatementSyntax fe => TypeRef.Of(fe.Type.ToString()),
                CatchDeclarationSyntax cd => TypeRef.Of(cd.Type.ToString()),
                SingleVariableDesignationSyntax { Parent: DeclarationPatternSyntax dp } => TypeRef.Of(dp.Type.ToString()),
                SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax de } => TypeRef.Of(de.Type.ToString()),
                SingleVariableDesignationSyntax { Parent: RecursivePatternSyntax rp } => TypeRef.Of(rp.Type?.ToString()),
                _ => TypeRef.None,
            });
        }

        private static Dictionary<string, SyntaxNode> Collect(SyntaxNode body)
        {
            Dictionary<string, SyntaxNode> map = new(StringComparer.Ordinal);
            foreach (SyntaxNode n in body.DescendantNodes())
            {
                string? id = n switch
                {
                    ParameterSyntax p => p.Identifier.Text,
                    VariableDeclaratorSyntax v when v.Parent is VariableDeclarationSyntax { Parent: not FieldDeclarationSyntax and not EventFieldDeclarationSyntax } => v.Identifier.Text,
                    ForEachStatementSyntax fe => fe.Identifier.Text,
                    CatchDeclarationSyntax cd when !cd.Identifier.IsKind(SyntaxKind.None) => cd.Identifier.Text,
                    SingleVariableDesignationSyntax s => s.Identifier.Text,
                    _ => null,
                };
                if (id is not null)
                {
                    map.TryAdd(id, n);
                }
            }

            return map;
        }
    }
}
