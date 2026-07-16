using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>
/// Phase 1 of the runtime-edge feature: the syntactic DI/locator/legacy-construction extractor. Cases are
/// grounded in real OpenRad patterns (DependencyInitialiser.cs, Nexus DependencyInjection.cs, B3Dnet.svc.cs).
/// </summary>
public sealed class RuntimeEdgeExtractorTests
{
    // --- closed-generic DI (the dominant pattern) ---

    [Fact]
    public void ClosedGeneric_AddSingleton_ResolvesBothEndpoints()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public class DependencyInitialiser
            {
                public void ConfigureServices(IServiceCollection services)
                {
                    services.AddSingleton<IConfigProvider, ConfigProvider>();
                }
            }
            """);

        reg.Kind.Should().Be(RegKind.DiGeneric);
        reg.ServiceTypeName.Should().Be("IConfigProvider");
        reg.ImplTypeName.Should().Be("ConfigProvider");
        reg.Lifetime.Should().Be("Singleton");
        reg.Confidence.Should().Be(RuntimeEdgeConfidence.Resolved);
        reg.EnclosingType.Should().Be("DependencyInitialiser");
        reg.EnclosingMember.Should().Be("ConfigureServices");
        reg.Conditional.Should().BeFalse();
    }

    [Theory]
    [InlineData("AddScoped", "Scoped")]
    [InlineData("AddTransient", "Transient")]
    [InlineData("TryAddSingleton", "Singleton")]
    public void ClosedGeneric_LifetimeAndTryAdd(string method, string expectedLifetime)
    {
        RuntimeRegistration reg = Single($$"""
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) => services.{{method}}<IFoo, Foo>();
            }
            """);

        reg.Kind.Should().Be(RegKind.DiGeneric);
        reg.ServiceTypeName.Should().Be("IFoo");
        reg.ImplTypeName.Should().Be("Foo");
        reg.Lifetime.Should().Be(expectedLifetime);
    }

    // --- BLOCKER 1: services.Replace / ServiceDescriptor must be captured and must not be shadowed by a NullObject ---

    [Fact]
    public void Replace_ServiceDescriptor_CapturesRealImplAlongsideNullObjectFallback()
    {
        // Real Nexus pattern: a NullObject is Added, then Replaced by the concrete collector two lines down.
        List<RuntimeRegistration> regs = Extract("""
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;
            namespace N;
            public static class M
            {
                public static void AddDicomTelemetryCollectors(IServiceCollection services)
                {
                    services.AddSingleton<IDicomConnectivityCollector, NullDicomConnectivityCollector>();
                    services.Replace(ServiceDescriptor.Singleton<IDicomConnectivityCollector, DicomConnectivityCollector>());
                }
            }
            """);

        // The NullObject Add is a candidate...
        regs.Should().Contain(r => r.Kind == RegKind.DiGeneric
            && r.ServiceTypeName == "IDicomConnectivityCollector" && r.ImplTypeName == "NullDicomConnectivityCollector");

        // ...and the Replace target — invisible before this fix — is captured as an overriding edge.
        RuntimeRegistration replace = regs.Should().ContainSingle(r => r.Kind == RegKind.DiReplace).Which;
        replace.ServiceTypeName.Should().Be("IDicomConnectivityCollector");
        replace.ImplTypeName.Should().Be("DicomConnectivityCollector");
        replace.Lifetime.Should().Be("Singleton");
    }

    [Fact]
    public void ServiceDescriptor_ViaAdd_IsCaptured()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) =>
                    services.Add(ServiceDescriptor.Transient<IFoo, Foo>());
            }
            """);

        reg.Kind.Should().Be(RegKind.DiGeneric);
        reg.ServiceTypeName.Should().Be("IFoo");
        reg.ImplTypeName.Should().Be("Foo");
        reg.Lifetime.Should().Be("Transient");
    }

    // --- factory lambdas ---

    [Fact]
    public void FactoryLambda_SingleNew_IsResolved()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) =>
                    services.AddSingleton<ICloudIdentityAuthenticator>(sp => new CloudIdentityAuthenticator(sp));
            }
            """);

        reg.Kind.Should().Be(RegKind.DiFactory);
        reg.ServiceTypeName.Should().Be("ICloudIdentityAuthenticator");
        reg.ImplTypeName.Should().Be("CloudIdentityAuthenticator");
        reg.Confidence.Should().Be(RuntimeEdgeConfidence.Resolved);
    }

    [Fact]
    public void FactoryLambda_ResolvingFromContainer_IsDynamic_AndEmitsConsumerEdge()
    {
        List<RuntimeRegistration> regs = Extract("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) =>
                    services.AddSingleton<IFoo>(sp => sp.GetRequiredService<FooImpl>());
            }
            """);

        RuntimeRegistration factory = regs.Should().ContainSingle(r => r.Kind == RegKind.DiFactory).Which;
        factory.ServiceTypeName.Should().Be("IFoo");
        factory.ImplTypeName.Should().BeNull();
        factory.Confidence.Should().Be(RuntimeEdgeConfidence.Dynamic);

        // The GetRequiredService inside the factory is itself a locator consumption.
        regs.Should().Contain(r => r.Kind == RegKind.LocatorConsume && r.ServiceTypeName == "FooImpl");
    }

    // --- instances ---

    [Fact]
    public void Instance_DotInstance_IsResolved()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) =>
                    services.AddSingleton<IDataDirectoriesImpl>(DataDirectoriesImpl.Instance);
            }
            """);

        reg.Kind.Should().Be(RegKind.DiInstance);
        reg.ServiceTypeName.Should().Be("IDataDirectoriesImpl");
        reg.ImplTypeName.Should().Be("DataDirectoriesImpl");
    }

    [Fact]
    public void Instance_NonGenericSelfRegistration_ServiceEqualsImpl()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) => services.TryAddSingleton(new Hl7StoreOptions());
            }
            """);

        reg.Kind.Should().Be(RegKind.DiInstance);
        reg.ServiceTypeName.Should().Be("Hl7StoreOptions");
        reg.ImplTypeName.Should().Be("Hl7StoreOptions");
    }

    [Fact]
    public void OpenGeneric_TypeofPair_IsResolved()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services) =>
                    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestPostProcessorBehavior<,>));
            }
            """);

        reg.Kind.Should().Be(RegKind.DiOpenGeneric);
        reg.ServiceTypeName.Should().Be("IPipelineBehavior<,>");
        reg.ImplTypeName.Should().Be("RequestPostProcessorBehavior<,>");
    }

    // --- service-locator consumption ---

    [Fact]
    public void Locator_GetRequiredService_IsPartialConsumerEdge()
    {
        RuntimeRegistration reg = Single("""
            namespace N;
            public class B3Dnet
            {
                public void Handle() =>
                    B3DServiceConfiguration.ServiceProvider.GetRequiredService<IRISService>().Do();
            }
            """);

        reg.Kind.Should().Be(RegKind.LocatorConsume);
        reg.ServiceTypeName.Should().Be("IRISService");
        reg.ImplTypeName.Should().BeNull();
        reg.Confidence.Should().Be(RuntimeEdgeConfidence.Partial);
        reg.EnclosingType.Should().Be("B3Dnet");
        reg.EnclosingMember.Should().Be("Handle");
    }

    // --- legacy hardcoded construction + the :96-vs-:140 major-fix distinction ---

    [Fact]
    public void LegacyNew_LazyWrapped_ResolvesInnerImpl()
    {
        RuntimeRegistration reg = Single("""
            using System;
            namespace N;
            public class Svc
            {
                private readonly Lazy<IConfigurationService> _cfg = new Lazy<IConfigurationService>(() => new ConfigurationService());
            }
            """);

        reg.Kind.Should().Be(RegKind.LegacyNew);
        reg.ServiceTypeName.Should().Be("IConfigurationService");
        reg.ImplTypeName.Should().Be("ConfigurationService");
    }

    [Fact]
    public void LegacyNew_LazyWrappingLocator_IsSuppressed_OnlyConsumerEdgeRemains()
    {
        // B3Dnet.svc.cs:140 shape — the Lazy resolves from the container, so there is NO hardcoded impl to bind.
        List<RuntimeRegistration> regs = Extract("""
            using System;
            namespace N;
            public class Svc
            {
                private readonly Lazy<IRISService> _ris = new Lazy<IRISService>(() => B3DServiceConfiguration.ServiceProvider.GetRequiredService<IRISService>());
            }
            """);

        regs.Should().NotContain(r => r.Kind == RegKind.LegacyNew);
        regs.Should().ContainSingle(r => r.Kind == RegKind.LocatorConsume)
            .Which.ServiceTypeName.Should().Be("IRISService");
    }

    [Fact]
    public void LegacyNew_DirectField_ServiceEqualsImpl_IsNotAnEdge()
    {
        // A concrete-typed field is not an interface→impl wiring; a marker is present so the file IS walked.
        List<RuntimeRegistration> regs = Extract("""
            using System;
            namespace N;
            public class Svc
            {
                private readonly Lazy<int> _n = new Lazy<int>(() => 1);
                private readonly FooImpl _foo = new FooImpl();
            }
            """);

        regs.Should().NotContain(r => r.Kind == RegKind.LegacyNew);
    }

    // --- conditional downgrade ---

    [Fact]
    public void ConditionalRegistration_IsDowngradedToPartial()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class M
            {
                public static void Go(IServiceCollection services, bool enabled)
                {
                    if (enabled)
                    {
                        services.AddSingleton<IFoo, Foo>();
                    }
                }
            }
            """);

        reg.Kind.Should().Be(RegKind.DiGeneric);
        reg.Conditional.Should().BeTrue();
        reg.Confidence.Should().Be(RuntimeEdgeConfidence.Partial);
    }

    // --- module-body capture (unblocks the Phase 3 2-hop JOIN) ---

    [Fact]
    public void RegistrationInsideModuleMethod_RecordsEnclosingMember()
    {
        RuntimeRegistration reg = Single("""
            using Microsoft.Extensions.DependencyInjection;
            namespace N;
            public static class AuditServiceCollectionExtensions
            {
                public static IServiceCollection AddAuditService(this IServiceCollection services)
                {
                    services.AddScoped<IAuditService, AuditService>();
                    return services;
                }
            }
            """);

        reg.EnclosingType.Should().Be("AuditServiceCollectionExtensions");
        reg.EnclosingMember.Should().Be("AddAuditService");
    }

    // --- the gate: ordinary files pay ~one substring scan and get null ---

    [Fact]
    public void NoMarkers_ReturnsNullRegistrations()
    {
        SourceFileIndex file = ParseSnippet("""
            namespace N;
            public class Plain
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        file.Registrations.Should().BeNull();
    }

    // ---- helpers ----

    private static SourceFileIndex ParseSnippet(string source)
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\Snippet.cs", source);
        return new SourceFileParser(fs).Parse(@"C:\repo\Snippet.cs", "TestProject")!;
    }

    private static List<RuntimeRegistration> Extract(string source) =>
        ParseSnippet(source).Registrations ?? [];

    private static RuntimeRegistration Single(string source) =>
        Extract(source).Should().ContainSingle().Which;
}
