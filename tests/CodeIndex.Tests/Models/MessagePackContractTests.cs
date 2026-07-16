using CodeIndex.Models;
using MessagePack;

namespace CodeIndex.Tests.Models;

/// <summary>
/// Locks the on-disk contract: MessagePack serializes enums by ordinal and objects by <c>[Key]</c> ordinal,
/// so a reordered <see cref="SymbolKind"/> or a shifted key silently corrupts every existing cache. These
/// tests fail loudly if the wire shape drifts.
/// </summary>
public sealed class MessagePackContractTests
{
    [Fact]
    public void SymbolKind_OrdinalOrder_IsStable()
    {
        ((int)SymbolKind.Class).Should().Be(0);
        ((int)SymbolKind.StaticClass).Should().Be(1);
        ((int)SymbolKind.AbstractClass).Should().Be(2);
        ((int)SymbolKind.SealedClass).Should().Be(3);
        ((int)SymbolKind.Interface).Should().Be(4);
        ((int)SymbolKind.Enum).Should().Be(5);
        ((int)SymbolKind.Method).Should().Be(6);
        ((int)SymbolKind.Property).Should().Be(7);
        ((int)SymbolKind.Field).Should().Be(8);
        ((int)SymbolKind.Constructor).Should().Be(9);
        ((int)SymbolKind.Event).Should().Be(10);
        ((int)SymbolKind.Struct).Should().Be(11);
        ((int)SymbolKind.Record).Should().Be(12);
        ((int)SymbolKind.RecordStruct).Should().Be(13);
        ((int)SymbolKind.Function).Should().Be(14);
        ((int)SymbolKind.Variable).Should().Be(15);
        ((int)SymbolKind.TypeAlias).Should().Be(16);
        ((int)SymbolKind.ScssSelector).Should().Be(17);
        ((int)SymbolKind.ScssMixin).Should().Be(18);
        ((int)SymbolKind.ScssFunction).Should().Be(19);
        ((int)SymbolKind.ScssVariable).Should().Be(20);
        ((int)SymbolKind.ScssPlaceholder).Should().Be(21);
    }

    [Fact]
    public void Language_ByteValues_AreStable()
    {
        ((byte)Language.CSharp).Should().Be(0);
        ((byte)Language.TypeScript).Should().Be(1);
        ((byte)Language.Scss).Should().Be(2);
    }

    [Fact]
    public void SourceFileIndex_RoundTrips_WithAllFields()
    {
        SourceFileIndex file = new()
        {
            FileName = "UserService.cs",
            SourceFilePath = @"C:\Repo\UserService.cs",
            ProjectName = "App",
            Namespace = "App.Services",
            Types =
            [
                new TypeInfo
                {
                    Name = "UserService",
                    Kind = SymbolKind.Class,
                    TypeKeyword = "class",
                    BaseTypes = ["IUserService"],
                    StartLine = 1,
                    LineCount = 20,
                    Namespace = "App.Services",
                    Members =
                    [
                        new MemberInfo
                        {
                            Name = "GetUser",
                            Kind = SymbolKind.Method,
                            ReturnType = "string",
                            Signature = "public string GetUser()",
                            StartLine = 5,
                            LineCount = 3,
                        },
                    ],
                },
            ],
            Usings = ["System", "App.Data"],
            UsingAliases = new Dictionary<string, string> { ["Db"] = "App.Data.Database" },
            Language = Language.CSharp,
        };

        byte[] bytes = MessagePackSerializer.Serialize(file);
        SourceFileIndex back = MessagePackSerializer.Deserialize<SourceFileIndex>(bytes);

        back.FileName.Should().Be("UserService.cs");
        back.SourceFilePath.Should().Be(@"C:\Repo\UserService.cs");
        back.ProjectName.Should().Be("App");
        back.Namespace.Should().Be("App.Services");
        back.Language.Should().Be(Language.CSharp);
        back.Usings.Should().BeEquivalentTo(["System", "App.Data"]);
        back.UsingAliases.Should().ContainKey("Db").WhoseValue.Should().Be("App.Data.Database");

        TypeInfo t = back.Types.Single();
        t.Kind.Should().Be(SymbolKind.Class);
        t.TypeKeyword.Should().Be("class");
        t.BaseTypes.Should().ContainSingle().Which.Should().Be("IUserService");
        t.BaseTypesDisplay.Should().Be("IUserService");

        MemberInfo m = t.Members.Single();
        m.Kind.Should().Be(SymbolKind.Method);
        m.ReturnType.Should().Be("string");
        m.StartLine.Should().Be(5);
        m.LineCount.Should().Be(3);
    }

    [Fact]
    public void CacheData_RoundTrips()
    {
        CacheData data = new()
        {
            Projects = [new ProjectIndex { Name = "App", ProjectDirPath = @"C:\Repo\App", SourceFiles = ["App.cs"] }],
            SourceFiles = [],
            FileTimestamps = new Dictionary<string, long> { [@"C:\Repo\App\App.cs"] = 123L },
            SchemaVersion = 4,
        };

        byte[] bytes = MessagePackSerializer.Serialize(data);
        CacheData back = MessagePackSerializer.Deserialize<CacheData>(bytes);

        back.SchemaVersion.Should().Be(4);
        back.Projects.Single().Name.Should().Be("App");
        back.Projects.Single().SourceFiles.Should().ContainSingle().Which.Should().Be("App.cs");
        back.FileTimestamps.Should().ContainKey(@"C:\Repo\App\App.cs").WhoseValue.Should().Be(123L);
    }

    [Fact]
    public void TypeInfo_BaseTypesDisplay_IsNullWithoutBases()
    {
        TypeInfo t = new() { Name = "X", Kind = SymbolKind.Class, TypeKeyword = "class", StartLine = 1, LineCount = 1 };
        t.BaseTypesDisplay.Should().BeNull();
    }

    // --- v5 runtime-edge plumbing (Phase 0) ---

    [Fact]
    public void RegKind_OrdinalOrder_IsStable()
    {
        // Append-only contract: reordering silently rebinds every serialized RuntimeRegistration.
        ((int)RegKind.DiGeneric).Should().Be(0);
        ((int)RegKind.DiFactory).Should().Be(1);
        ((int)RegKind.DiInstance).Should().Be(2);
        ((int)RegKind.DiOpenGeneric).Should().Be(3);
        ((int)RegKind.DiReplace).Should().Be(4);
        ((int)RegKind.DiModule).Should().Be(5);
        ((int)RegKind.LocatorConsume).Should().Be(6);
        ((int)RegKind.LegacyNew).Should().Be(7);
        ((int)RegKind.Reflection).Should().Be(8);
        ((int)RegKind.KnownType).Should().Be(9);
        ((int)RegKind.ExtensionOf).Should().Be(10);
        ((int)RegKind.DelegateWiring).Should().Be(11);
    }

    [Fact]
    public void RuntimeEdgeConfidence_OrdinalOrder_IsStable()
    {
        ((int)RuntimeEdgeConfidence.Resolved).Should().Be(0);
        ((int)RuntimeEdgeConfidence.Partial).Should().Be(1);
        ((int)RuntimeEdgeConfidence.Dynamic).Should().Be(2);
    }

    [Fact]
    public void FrameworkRootKind_OrdinalOrder_IsStable()
    {
        ((int)FrameworkRootKind.ServiceHost).Should().Be(0);
        ((int)FrameworkRootKind.ReflectionTarget).Should().Be(1);
        ((int)FrameworkRootKind.KnownType).Should().Be(2);
        ((int)FrameworkRootKind.ExtensionOf).Should().Be(3);
    }

    [Fact]
    public void MemberRootKind_BitValues_AreStable()
    {
        // Append-only bit contract: renumbering corrupts the stored flag byte on every member.
        ((int)MemberRootKind.None).Should().Be(0);
        ((int)MemberRootKind.WcfOperation).Should().Be(1);
        ((int)MemberRootKind.SerializationCallback).Should().Be(2);
        ((int)MemberRootKind.DataMember).Should().Be(4);
        ((int)MemberRootKind.Test).Should().Be(8);
        ((int)MemberRootKind.HttpHandlerMethod).Should().Be(16);
        ((int)MemberRootKind.ServiceControlMethod).Should().Be(32);
        ((int)MemberRootKind.EntryPointMain).Should().Be(64);
        ((int)MemberRootKind.HttpAppLifecycle).Should().Be(128);
    }

    [Fact]
    public void TypeRootKind_BitValues_AreStable()
    {
        ((int)TypeRootKind.None).Should().Be(0);
        ((int)TypeRootKind.WcfServiceContract).Should().Be(1);
        ((int)TypeRootKind.WcfServiceImpl).Should().Be(2);
        ((int)TypeRootKind.HttpHandler).Should().Be(4);
        ((int)TypeRootKind.HttpApplication).Should().Be(8);
        ((int)TypeRootKind.ServiceBase).Should().Be(16);
        ((int)TypeRootKind.DataContract).Should().Be(32);
        ((int)TypeRootKind.TestClass).Should().Be(64);
    }

    [Fact]
    public void RuntimeRegistration_RoundTrips_WithAllFields()
    {
        RuntimeRegistration reg = new()
        {
            Kind = RegKind.DiGeneric,
            ServiceTypeName = "IConfigProvider",
            ImplTypeName = "ConfigProvider",
            Lifetime = "Singleton",
            StartLine = 23,
            Confidence = RuntimeEdgeConfidence.Resolved,
            Snippet = "services.AddSingleton<IConfigProvider, ConfigProvider>()",
            EnclosingType = "DependencyInitialiser",
            EnclosingMember = "ConfigureServices",
            Conditional = false,
        };

        RuntimeRegistration back = MessagePackSerializer.Deserialize<RuntimeRegistration>(MessagePackSerializer.Serialize(reg));

        back.Kind.Should().Be(RegKind.DiGeneric);
        back.ServiceTypeName.Should().Be("IConfigProvider");
        back.ImplTypeName.Should().Be("ConfigProvider");
        back.Lifetime.Should().Be("Singleton");
        back.StartLine.Should().Be(23);
        back.Confidence.Should().Be(RuntimeEdgeConfidence.Resolved);
        back.Snippet.Should().Be("services.AddSingleton<IConfigProvider, ConfigProvider>()");
        back.EnclosingType.Should().Be("DependencyInitialiser");
        back.EnclosingMember.Should().Be("ConfigureServices");
        back.Conditional.Should().BeFalse();
    }

    [Fact]
    public void RuntimeRegistration_RoundTrips_DynamicWithNullImpl()
    {
        // A dynamic edge is never dropped and never guessed: null impl survives the round-trip.
        RuntimeRegistration reg = new()
        {
            Kind = RegKind.Reflection,
            ServiceTypeName = null,
            ImplTypeName = null,
            StartLine = 42,
            Confidence = RuntimeEdgeConfidence.Dynamic,
        };

        RuntimeRegistration back = MessagePackSerializer.Deserialize<RuntimeRegistration>(MessagePackSerializer.Serialize(reg));

        back.ImplTypeName.Should().BeNull();
        back.Confidence.Should().Be(RuntimeEdgeConfidence.Dynamic);
    }

    [Fact]
    public void FrameworkRootMark_RoundTrips()
    {
        FrameworkRootMark mark = new()
        {
            TargetTypeName = "MSIService",
            TargetMemberName = null,
            Kind = FrameworkRootKind.ServiceHost,
            StartLine = 72,
        };

        FrameworkRootMark back = MessagePackSerializer.Deserialize<FrameworkRootMark>(MessagePackSerializer.Serialize(mark));

        back.TargetTypeName.Should().Be("MSIService");
        back.TargetMemberName.Should().BeNull();
        back.Kind.Should().Be(FrameworkRootKind.ServiceHost);
        back.StartLine.Should().Be(72);
    }

    [Fact]
    public void SourceFileIndex_RoundTrips_WithRuntimeEdgesAndRootKinds()
    {
        SourceFileIndex file = new()
        {
            FileName = "DependencyInitialiser.cs",
            SourceFilePath = @"C:\Repo\DependencyInitialiser.cs",
            ProjectName = "Web",
            Types =
            [
                new TypeInfo
                {
                    Name = "B3Dnet",
                    Kind = SymbolKind.Class,
                    TypeKeyword = "class",
                    StartLine = 1,
                    LineCount = 100,
                    RootKinds = TypeRootKind.WcfServiceImpl,
                    Members =
                    [
                        new MemberInfo
                        {
                            Name = "GetReport",
                            Kind = SymbolKind.Method,
                            ReturnType = "string",
                            Signature = "string GetReport()",
                            StartLine = 10,
                            LineCount = 5,
                            RootKinds = MemberRootKind.WcfOperation | MemberRootKind.DataMember,
                        },
                    ],
                },
            ],
            Registrations =
            [
                new RuntimeRegistration
                {
                    Kind = RegKind.DiGeneric,
                    ServiceTypeName = "IConfigProvider",
                    ImplTypeName = "ConfigProvider",
                    Lifetime = "Singleton",
                    StartLine = 23,
                    Confidence = RuntimeEdgeConfidence.Resolved,
                },
            ],
            FrameworkRoots =
            [
                new FrameworkRootMark
                {
                    TargetTypeName = "MSIService",
                    Kind = FrameworkRootKind.ServiceHost,
                    StartLine = 72,
                },
            ],
        };

        SourceFileIndex back = MessagePackSerializer.Deserialize<SourceFileIndex>(MessagePackSerializer.Serialize(file));

        back.Registrations.Should().ContainSingle();
        back.Registrations![0].ServiceTypeName.Should().Be("IConfigProvider");
        back.Registrations[0].ImplTypeName.Should().Be("ConfigProvider");
        back.FrameworkRoots.Should().ContainSingle();
        back.FrameworkRoots![0].TargetTypeName.Should().Be("MSIService");

        TypeInfo t = back.Types.Single();
        t.RootKinds.Should().Be(TypeRootKind.WcfServiceImpl);
        MemberInfo m = t.Members.Single();
        m.RootKinds.Should().Be(MemberRootKind.WcfOperation | MemberRootKind.DataMember);
        m.RootKinds.HasFlag(MemberRootKind.DataMember).Should().BeTrue();
    }

    [Fact]
    public void SourceFileIndex_RoundTrips_WithNullRuntimeEdges()
    {
        // The common case: an ordinary file has no runtime edges — both lists stay null, RootKinds default None.
        SourceFileIndex file = new()
        {
            FileName = "Plain.cs",
            SourceFilePath = @"C:\Repo\Plain.cs",
            ProjectName = "App",
        };

        SourceFileIndex back = MessagePackSerializer.Deserialize<SourceFileIndex>(MessagePackSerializer.Serialize(file));

        back.Registrations.Should().BeNull();
        back.FrameworkRoots.Should().BeNull();
    }
}
