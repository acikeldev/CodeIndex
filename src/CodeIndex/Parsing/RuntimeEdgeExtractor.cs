using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeIndex.Models;

namespace CodeIndex.Parsing;

/// <summary>
/// Recovers runtime-wiring edges (DI registrations, service-locator consumption, legacy hardcoded construction)
/// from an already-parsed C# syntax tree — no semantic model, no compilation. Only edges whose endpoints are
/// literal in source are recorded as resolved; anything indirect (branching factory, string/variable reflection,
/// an interface-typed instance) is recorded as <see cref="RuntimeEdgeConfidence.Dynamic"/> and never guessed into
/// a concrete type. The solution-wide interface→implementation closure is a later, cross-file concern.
/// </summary>
public static class RuntimeEdgeExtractor
{
    // Cheap gate: files with none of these markers cannot contain a runtime edge we recognise, so we skip the
    // tree walk entirely. Keeps the ~99% of files that do no DI wiring at roughly one substring scan.
    private static readonly string[] Markers =
    [
        "AddSingleton", "AddScoped", "AddTransient", "TryAdd", "GetRequiredService", "GetService",
        "ServiceDescriptor", "Lazy<",
    ];

    private static readonly HashSet<string> LifetimeAddMethods = new(StringComparer.Ordinal)
    {
        "AddSingleton", "AddScoped", "AddTransient",
        "TryAddSingleton", "TryAddScoped", "TryAddTransient",
    };

    /// <summary>Walk <paramref name="root"/> for runtime-wiring sites. Returns null when there are none.</summary>
    public static List<RuntimeRegistration>? Extract(CompilationUnitSyntax root, string sourceText)
    {
        if (!HasMarker(sourceText))
        {
            return null;
        }

        List<RuntimeRegistration> edges = [];

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv:
                    HandleInvocation(inv, edges);
                    break;
                case FieldDeclarationSyntax field:
                    HandleLegacyField(field, edges);
                    break;
            }
        }

        return edges.Count > 0 ? edges : null;
    }

    private static bool HasMarker(string sourceText)
    {
        foreach (string marker in Markers)
        {
            if (sourceText.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void HandleInvocation(InvocationExpressionSyntax inv, List<RuntimeRegistration> edges)
    {
        SimpleNameSyntax? invoked = GetInvokedName(inv);
        if (invoked is null)
        {
            return;
        }

        string name = invoked.Identifier.Text;
        IReadOnlyList<TypeSyntax> typeArgs = invoked is GenericNameSyntax g ? g.TypeArgumentList.Arguments : [];
        SeparatedSyntaxList<ArgumentSyntax> args = inv.ArgumentList.Arguments;

        // --- Service-locator consumption: GetRequiredService<IFoo>() / GetService<IFoo>() ---
        if ((name == "GetRequiredService" || name == "GetService") && typeArgs.Count == 1)
        {
            edges.Add(Build(inv, RegKind.LocatorConsume, typeArgs[0].ToString(), impl: null, lifetime: null,
                RuntimeEdgeConfidence.Partial));
            return;
        }

        // --- services.Replace(ServiceDescriptor.Xxx<I,Impl>()) / services.Add(descriptor) / TryAddEnumerable(...) ---
        if (name is "Replace" or "Add" or "TryAddEnumerable" && args.Count >= 1
            && TryReadServiceDescriptor(args[0].Expression, out string? sdService, out string? sdImpl, out string? sdLife))
        {
            RegKind sdKind = name == "Replace" ? RegKind.DiReplace : RegKind.DiGeneric;
            edges.Add(Build(inv, sdKind, sdService, sdImpl, sdLife,
                sdImpl is null ? RuntimeEdgeConfidence.Dynamic : RuntimeEdgeConfidence.Resolved));
            return;
        }

        if (!LifetimeAddMethods.Contains(name))
        {
            return;
        }

        string? lifetime = LifetimeFromName(name);

        // --- Closed-generic: Add*<IFoo, Foo>() — both endpoints literal ---
        if (typeArgs.Count == 2)
        {
            edges.Add(Build(inv, RegKind.DiGeneric, typeArgs[0].ToString(), typeArgs[1].ToString(), lifetime,
                RuntimeEdgeConfidence.Resolved));
            return;
        }

        // --- Single-generic-arg: Add*<IFoo>(factory | instance) ---
        if (typeArgs.Count == 1)
        {
            string service = typeArgs[0].ToString();
            HandleSingleGenericAdd(inv, service, lifetime, args, edges);
            return;
        }

        // --- Non-generic: Add*(typeof(I<,>), typeof(Impl<,>)) open-generic, or Add*(new Foo()) instance ---
        if (typeArgs.Count == 0 && args.Count >= 1)
        {
            HandleNonGenericAdd(inv, lifetime, args, edges);
        }
    }

    private static void HandleSingleGenericAdd(
        InvocationExpressionSyntax inv, string service, string? lifetime,
        SeparatedSyntaxList<ArgumentSyntax> args, List<RuntimeRegistration> edges)
    {
        if (args.Count == 0)
        {
            // Add<IFoo>() with no impl arg — impl inferred from the ctor at runtime; service known, impl not.
            edges.Add(Build(inv, RegKind.DiFactory, service, impl: null, lifetime, RuntimeEdgeConfidence.Dynamic));
            return;
        }

        ExpressionSyntax arg = args[0].Expression;

        if (arg is LambdaExpressionSyntax lambda)
        {
            string? impl = SingleNewInLambda(lambda);
            edges.Add(Build(inv, RegKind.DiFactory, service, impl, lifetime,
                impl is null ? RuntimeEdgeConfidence.Dynamic : RuntimeEdgeConfidence.Resolved));
            return;
        }

        // Add<IFoo>(new Foo()) or Add<IFoo>(Foo.Instance) — a concrete instance expression.
        string? instanceImpl = InstanceImplType(arg);
        edges.Add(Build(inv, RegKind.DiInstance, service, instanceImpl, lifetime,
            instanceImpl is null ? RuntimeEdgeConfidence.Dynamic : RuntimeEdgeConfidence.Resolved));
    }

    private static void HandleNonGenericAdd(
        InvocationExpressionSyntax inv, string? lifetime,
        SeparatedSyntaxList<ArgumentSyntax> args, List<RuntimeRegistration> edges)
    {
        // Open generic: Add*(typeof(IFoo<,>), typeof(Foo<,>)).
        if (args.Count >= 2
            && args[0].Expression is TypeOfExpressionSyntax svcTypeOf
            && args[1].Expression is TypeOfExpressionSyntax implTypeOf)
        {
            edges.Add(Build(inv, RegKind.DiOpenGeneric, svcTypeOf.Type.ToString(), implTypeOf.Type.ToString(),
                lifetime, RuntimeEdgeConfidence.Resolved));
            return;
        }

        // Instance self-registration: TryAddSingleton(new Hl7StoreOptions()) — service == impl == created type.
        if (args[0].Expression is ObjectCreationExpressionSyntax oce)
        {
            string impl = oce.Type.ToString();
            edges.Add(Build(inv, RegKind.DiInstance, impl, impl, lifetime, RuntimeEdgeConfidence.Resolved));
        }
    }

    private static void HandleLegacyField(FieldDeclarationSyntax field, List<RuntimeRegistration> edges)
    {
        string declaredType = field.Declaration.Type.ToString();
        if (declaredType == "var")
        {
            return;
        }

        // Unwrap a single Lazy<T>/Func<T> layer: the SERVICE type is the wrapped T, and the impl is the concrete
        // `new X()` produced inside the wrapper's factory lambda.
        string? service = declaredType;
        bool wrapped = false;
        if (field.Declaration.Type is GenericNameSyntax gn
            && gn.Identifier.Text is "Lazy" or "Func"
            && gn.TypeArgumentList.Arguments.Count == 1)
        {
            service = gn.TypeArgumentList.Arguments[0].ToString();
            wrapped = true;
        }

        foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
        {
            if (v.Initializer?.Value is not ObjectCreationExpressionSyntax oce)
            {
                continue;
            }

            string? impl;
            if (wrapped)
            {
                // new Lazy<IFoo>(() => new Foo()) — dig into the lambda. If the lambda instead resolves from the
                // container (() => provider.GetRequiredService<IFoo>()), SingleNewInLambda returns null and we
                // suppress the LegacyNew edge: that site is a locator consumption, captured by HandleInvocation.
                LambdaExpressionSyntax? factory = oce.ArgumentList?.Arguments
                    .Select(a => a.Expression)
                    .OfType<LambdaExpressionSyntax>()
                    .FirstOrDefault();
                impl = factory is null ? null : SingleNewInLambda(factory);
            }
            else
            {
                impl = oce.Type.ToString();
            }

            // Only a genuine interface→implementation binding: declared service type must differ from the concrete
            // type. `Foo _f = new Foo()` is not a wiring edge; `IFoo _f = new Foo()` is.
            if (impl is not null && !string.Equals(impl, service, StringComparison.Ordinal))
            {
                edges.Add(Build(v, RegKind.LegacyNew, service, impl, lifetime: null, RuntimeEdgeConfidence.Resolved));
            }
        }
    }

    // ---- helpers ----

    private static SimpleNameSyntax? GetInvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name,
        MemberBindingExpressionSyntax mb => mb.Name,
        SimpleNameSyntax sn => sn,
        _ => null,
    };

    private static string? LifetimeFromName(string name) =>
        name.Contains("Singleton", StringComparison.Ordinal) ? "Singleton"
        : name.Contains("Scoped", StringComparison.Ordinal) ? "Scoped"
        : name.Contains("Transient", StringComparison.Ordinal) ? "Transient"
        : null;

    /// <summary>The concrete type produced by a factory lambda whose body is a single <c>new X(...)</c>; null when
    /// the lambda branches, delegates, or resolves from the container (⇒ dynamic, don't guess).</summary>
    private static string? SingleNewInLambda(LambdaExpressionSyntax lambda)
    {
        if (lambda.Body is ObjectCreationExpressionSyntax exprBody)
        {
            return exprBody.Type.ToString();
        }

        if (lambda.Body is BlockSyntax block)
        {
            List<ReturnStatementSyntax> returns = block.Statements.OfType<ReturnStatementSyntax>().ToList();
            if (returns.Count == 1 && returns[0].Expression is ObjectCreationExpressionSyntax blockNew)
            {
                return blockNew.Type.ToString();
            }
        }

        return null;
    }

    /// <summary>Impl type of a direct instance argument: <c>new Foo()</c> ⇒ Foo, <c>Foo.Instance</c> ⇒ Foo; null
    /// for anything else (a variable, a method call, an interface-typed value).</summary>
    private static string? InstanceImplType(ExpressionSyntax arg) => arg switch
    {
        ObjectCreationExpressionSyntax oce => oce.Type.ToString(),
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax id } => id.Identifier.Text,
        _ => null,
    };

    private static bool TryReadServiceDescriptor(
        ExpressionSyntax expr, out string? service, out string? impl, out string? lifetime)
    {
        service = impl = lifetime = null;

        // ServiceDescriptor.Singleton<I, Impl>() / .Scoped<,> / .Transient<,>
        if (expr is InvocationExpressionSyntax sdInv
            && sdInv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "ServiceDescriptor" } } sdMa)
        {
            lifetime = LifetimeFromName(sdMa.Name.Identifier.Text);

            if (sdMa.Name is GenericNameSyntax { TypeArgumentList.Arguments: { Count: 2 } tas })
            {
                service = tas[0].ToString();
                impl = tas[1].ToString();
                return true;
            }

            // ServiceDescriptor.Describe(typeof(I), typeof(Impl), lifetime) — typeof operands.
            List<TypeOfExpressionSyntax> typeOfs = sdInv.ArgumentList.Arguments
                .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
            if (typeOfs.Count >= 2)
            {
                service = typeOfs[0].Type.ToString();
                impl = typeOfs[1].Type.ToString();
                return true;
            }

            // A ServiceDescriptor factory we could not fully read — still a descriptor site, but dynamic.
            return true;
        }

        // new ServiceDescriptor(typeof(I), typeof(Impl), lifetime)
        if (expr is ObjectCreationExpressionSyntax { Type: var t } oce && t.ToString() == "ServiceDescriptor")
        {
            List<TypeOfExpressionSyntax> typeOfs = oce.ArgumentList?.Arguments
                .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList() ?? [];
            if (typeOfs.Count >= 2)
            {
                service = typeOfs[0].Type.ToString();
                impl = typeOfs[1].Type.ToString();
            }

            return true;
        }

        return false;
    }

    private static RuntimeRegistration Build(
        SyntaxNode site, RegKind kind, string? service, string? impl, string? lifetime,
        RuntimeEdgeConfidence confidence)
    {
        bool conditional = IsConditional(site);
        // A conditional registration is not unconditionally wired; never present it as a clean resolved binding.
        RuntimeEdgeConfidence effective = conditional && confidence == RuntimeEdgeConfidence.Resolved
            ? RuntimeEdgeConfidence.Partial
            : confidence;

        return new RuntimeRegistration
        {
            Kind = kind,
            ServiceTypeName = service,
            ImplTypeName = impl,
            Lifetime = lifetime,
            StartLine = site.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = effective,
            Snippet = Snippet(site),
            EnclosingType = EnclosingTypeName(site),
            EnclosingMember = EnclosingMemberName(site),
            Conditional = conditional,
        };
    }

    private static bool IsConditional(SyntaxNode site)
    {
        foreach (SyntaxNode a in site.Ancestors())
        {
            switch (a)
            {
                case IfStatementSyntax:
                case SwitchStatementSyntax:
                case SwitchExpressionSyntax:
                case ConditionalExpressionSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                    return true;
                case MethodDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case TypeDeclarationSyntax:
                    return false; // reached the enclosing member/type without a conditional
            }
        }

        return false;
    }

    private static string? EnclosingTypeName(SyntaxNode site) =>
        site.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;

    private static string? EnclosingMemberName(SyntaxNode site)
    {
        foreach (SyntaxNode a in site.Ancestors())
        {
            switch (a)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case ConstructorDeclarationSyntax c: return c.Identifier.Text;
                case PropertyDeclarationSyntax p: return p.Identifier.Text;
                case FieldDeclarationSyntax f: return f.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                case TypeDeclarationSyntax: return null; // a member-less site (e.g. a field initializer) — no method
            }
        }

        return null;
    }

    private static string Snippet(SyntaxNode site)
    {
        string text = string.Join(' ', site.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}
