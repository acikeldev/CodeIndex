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
}
