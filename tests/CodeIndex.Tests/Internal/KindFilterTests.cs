using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

public class KindFilterTests
{
    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("record")]
    [InlineData("interface")]
    [InlineData("enum")]
    [InlineData("method")]
    [InlineData("property")]
    [InlineData("field")]
    [InlineData("constructor")]
    [InlineData("event")]
    public void IsValidSymbolKind_ReturnsTrue_ForKnownSymbolKinds(string kind)
    {
        KindFilter.IsValidSymbolKind(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData("CLASS")]
    [InlineData("Method")]
    [InlineData("Interface")]
    public void IsValidSymbolKind_IsCaseInsensitive(string kind)
    {
        KindFilter.IsValidSymbolKind(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("delegate")]
    [InlineData("namespace")]
    [InlineData("classes")]
    [InlineData("meth")]
    public void IsValidSymbolKind_ReturnsFalse_ForUnknownKinds(string kind)
    {
        KindFilter.IsValidSymbolKind(kind).Should().BeFalse();
    }

    [Theory]
    [InlineData("method")]
    [InlineData("property")]
    [InlineData("field")]
    [InlineData("constructor")]
    [InlineData("event")]
    public void IsValidMemberKind_ReturnsTrue_ForKnownMemberKinds(string kind)
    {
        KindFilter.IsValidMemberKind(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData("METHOD")]
    [InlineData("Property")]
    public void IsValidMemberKind_IsCaseInsensitive(string kind)
    {
        KindFilter.IsValidMemberKind(kind).Should().BeTrue();
    }

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("record")]
    [InlineData("interface")]
    [InlineData("enum")]
    public void IsValidMemberKind_ReturnsFalse_ForTypeOnlyKinds(string kind)
    {
        KindFilter.IsValidMemberKind(kind).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void IsValidMemberKind_ReturnsFalse_ForUnknownKinds(string kind)
    {
        KindFilter.IsValidMemberKind(kind).Should().BeFalse();
    }

    [Theory]
    [InlineData("ctor")]
    [InlineData("CTOR")]
    [InlineData("Ctor")]
    public void CtorAlias_IsAccepted_AsSymbolAndMemberKind(string kind)
    {
        KindFilter.IsValidSymbolKind(kind).Should().BeTrue();
        KindFilter.IsValidMemberKind(kind).Should().BeTrue();
    }

    [Fact]
    public void SymbolKinds_ContainsExpectedSet()
    {
        KindFilter.SymbolKinds.Should().Equal(
            "class", "struct", "record", "interface", "enum",
            "method", "property", "field", "constructor", "event");
    }

    [Fact]
    public void MemberKinds_ContainsExpectedSet()
    {
        KindFilter.MemberKinds.Should().Equal(
            "method", "property", "field", "constructor", "event");
    }

    [Fact]
    public void ValidSymbolKindsList_JoinsAllSymbolKinds()
    {
        KindFilter.ValidSymbolKindsList().Should().Be(
            "class, struct, record, interface, enum, method, property, field, constructor, event");
    }

    [Fact]
    public void ValidMemberKindsList_JoinsAllMemberKinds()
    {
        KindFilter.ValidMemberKindsList().Should().Be(
            "method, property, field, constructor, event");
    }
}
