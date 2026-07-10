using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

[Collection("GeneratedFileClassifier")]
public class GeneratedFileClassifierTests : IDisposable
{
    // The classifier holds process-wide static globs; restore defaults after every test
    // so mutation tests never leak into the others.
    public void Dispose()
    {
        GeneratedFileClassifier.UseGlobs(GeneratedFileClassifier.DefaultGlobs);
    }

    [Fact]
    public void DefaultGlobs_ContainsExpectedPatterns()
    {
        GeneratedFileClassifier.DefaultGlobs.Should().Equal(
            "*.designer.cs", "*.g.cs", "*.g.i.cs", "*.generated.cs",
            "reference.cs", "temporarygeneratedfile_*.cs", "assemblyinfo.cs", "*.assemblyattributes.cs");
    }

    [Theory]
    [InlineData("MyType.designer.cs")]
    [InlineData("MyType.g.cs")]
    [InlineData("MyType.g.i.cs")]
    [InlineData("MyType.generated.cs")]
    [InlineData("reference.cs")]
    [InlineData("temporarygeneratedfile_ABC123.cs")]
    [InlineData("assemblyinfo.cs")]
    [InlineData("MyAssembly.assemblyattributes.cs")]
    public void IsGenerated_ReturnsTrue_ForDefaultGeneratedFiles(string fileName)
    {
        GeneratedFileClassifier.IsGenerated(fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("MyStore.cs")]
    [InlineData("IMyInterface.cs")]
    [InlineData("Program.cs")]
    [InlineData("designer.cs")]                 // suffix "*.designer.cs" needs a char before ".designer.cs"
    [InlineData("g.cs")]                         // suffix "*.g.cs" needs a char before ".g.cs"
    [InlineData("reference.txt")]                // exact-match glob, wrong extension
    [InlineData("assemblyinfoX.cs")]             // exact-match glob, extra char
    [InlineData("")]
    public void IsGenerated_ReturnsFalse_ForHandWrittenFiles(string fileName)
    {
        GeneratedFileClassifier.IsGenerated(fileName).Should().BeFalse();
    }

    [Fact]
    public void IsGenerated_IsCaseInsensitive_ForSuffixGlobs()
    {
        GeneratedFileClassifier.IsGenerated("MyType.DESIGNER.CS").Should().BeTrue();
    }

    [Fact]
    public void IsGenerated_IsCaseInsensitive_ForExactGlobs()
    {
        GeneratedFileClassifier.IsGenerated("ASSEMBLYINFO.CS").Should().BeTrue();
    }

    [Fact]
    public void IsGenerated_IsCaseInsensitive_ForPrefixGlobs()
    {
        GeneratedFileClassifier.IsGenerated("TEMPORARYGENERATEDFILE_XYZ.CS").Should().BeTrue();
    }

    [Theory]
    [InlineData("C:\\repo\\Models\\MyType.designer.cs")]
    [InlineData("/home/user/project/MyType.designer.cs")]
    [InlineData("Models/MyType.designer.cs")]
    public void IsGenerated_UsesFileNameOnly_IgnoringDirectory(string path)
    {
        GeneratedFileClassifier.IsGenerated(path).Should().BeTrue();
    }

    [Fact]
    public void IsGenerated_ReturnsFalse_WhenPathHasGeneratedLikeDirectoryButPlainFile()
    {
        GeneratedFileClassifier.IsGenerated("C:\\designer.cs\\MyStore.cs").Should().BeFalse();
    }

    [Fact]
    public void UseGlobs_OverridesDefaults()
    {
        GeneratedFileClassifier.UseGlobs(["*.custom.cs"]);

        GeneratedFileClassifier.IsGenerated("MyType.custom.cs").Should().BeTrue();
        // Old defaults no longer apply once overridden.
        GeneratedFileClassifier.IsGenerated("MyType.designer.cs").Should().BeFalse();
    }

    [Fact]
    public void UseGlobs_IgnoresEmptyAndWhitespaceEntries()
    {
        GeneratedFileClassifier.UseGlobs(["", "   ", "*.custom.cs", null!]);

        GeneratedFileClassifier.IsGenerated("MyType.custom.cs").Should().BeTrue();
    }

    [Fact]
    public void UseGlobs_AllEmpty_IsNoOp()
    {
        GeneratedFileClassifier.UseGlobs(["", "   "]);

        // Defaults remain intact because the filtered set was empty.
        GeneratedFileClassifier.IsGenerated("MyType.designer.cs").Should().BeTrue();
    }

    [Fact]
    public void UseGlobs_SupportsExactMatchGlob_NoStar()
    {
        GeneratedFileClassifier.UseGlobs(["exactname.cs"]);

        GeneratedFileClassifier.IsGenerated("exactname.cs").Should().BeTrue();
        GeneratedFileClassifier.IsGenerated("exactname.cs.bak").Should().BeFalse();
        GeneratedFileClassifier.IsGenerated("Xexactname.cs").Should().BeFalse();
    }

    [Fact]
    public void UseGlobs_SupportsStarInMiddle_PrefixAndSuffix()
    {
        GeneratedFileClassifier.UseGlobs(["foo*bar.cs"]);

        GeneratedFileClassifier.IsGenerated("foo_middle_bar.cs").Should().BeTrue();
        GeneratedFileClassifier.IsGenerated("foobar.cs").Should().BeTrue();      // star matches empty
        GeneratedFileClassifier.IsGenerated("foo.cs").Should().BeFalse();        // missing suffix
        GeneratedFileClassifier.IsGenerated("bar.cs").Should().BeFalse();        // missing prefix
    }

    [Fact]
    public void UseGlobs_LengthGuard_RejectsNameShorterThanPrefixPlusSuffix()
    {
        GeneratedFileClassifier.UseGlobs(["abcd*wxyz"]);

        // "ab" is shorter than prefix+suffix combined and must not overlap-match.
        GeneratedFileClassifier.IsGenerated("ab").Should().BeFalse();
        GeneratedFileClassifier.IsGenerated("abcdwxyz").Should().BeTrue();
    }

    [Fact]
    public void UseGlobs_StarAtStart_MatchesBySuffixOnly()
    {
        GeneratedFileClassifier.UseGlobs(["*.only.cs"]);

        GeneratedFileClassifier.IsGenerated("anything.only.cs").Should().BeTrue();
        GeneratedFileClassifier.IsGenerated(".only.cs").Should().BeTrue();
        GeneratedFileClassifier.IsGenerated("only.cs").Should().BeFalse();
    }
}
