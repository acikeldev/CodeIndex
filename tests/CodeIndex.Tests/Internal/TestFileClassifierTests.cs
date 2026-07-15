using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Heuristics for <see cref="TestFileClassifier"/>: a file is test code when its project's last name segment is
/// Tests/Test, its filename ends with Tests.cs, or a path segment is test/tests — and NOT for look-alikes like
/// Latest.cs. Drives the production-vs-test split in find_references' Usage facet.
/// </summary>
public sealed class TestFileClassifierTests
{
    [Theory]
    [InlineData(@"C:\repo\App.Tests\Foo.cs", "App.Tests", true)]     // test project (last segment 'Tests')
    [InlineData(@"C:\repo\Foo.Test\Bar.cs", "Foo.Test", true)]       // last segment 'Test'
    [InlineData(@"C:\repo\App\WidgetTests.cs", "App", true)]         // filename ends with 'Tests.cs'
    [InlineData(@"C:\repo\test\Helpers.cs", "App", true)]            // 'test' path segment
    [InlineData(@"C:\repo\App\tests\Helpers.cs", "App", true)]       // 'tests' path segment
    [InlineData(@"C:\repo\App\Latest.cs", "App", false)]            // NOT '*Tests.cs'
    [InlineData(@"C:\repo\App\Manifest.cs", "App", false)]
    [InlineData(@"C:\repo\App\Service.cs", "App", false)]           // plain production
    [InlineData(@"C:\repo\App\Service.cs", null, false)]            // null project name
    public void IsTest_AppliesProjectFileAndDirHeuristics(string path, string? project, bool expected)
    {
        TestFileClassifier.IsTest(path, project).Should().Be(expected);
    }

    [Fact]
    public void IsTest_IsCaseInsensitive()
    {
        TestFileClassifier.IsTest(@"C:\repo\App\WIDGETTESTS.CS", "App").Should().BeTrue();
        TestFileClassifier.IsTest(@"C:\repo\App\Foo.cs", "APP.TESTS").Should().BeTrue();
    }
}
