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
    [InlineData(@"C:\repo\App.Tests\Foo.cs", "App.Tests", null, true)]                         // last segment 'Tests'
    [InlineData(@"C:\repo\App.IntegrationTests\Foo.cs", "App.IntegrationTests", null, true)]   // EndsWith 'Tests'
    [InlineData(@"C:\repo\Foo.Test\Bar.cs", "Foo.Test", null, true)]                           // equals 'Test'
    [InlineData(@"C:\repo\App\WidgetTests.cs", "App", null, true)]                             // filename '*Tests.cs'
    [InlineData(@"C:\repo\App\tests\Helpers.cs", "App", @"C:\repo\App", true)]                 // 'tests' dir under project
    [InlineData(@"C:\repo\App\Latest.cs", "App", null, false)]                                 // NOT '*Tests.cs'
    [InlineData(@"C:\repo\App\Greatest.cs", "Greatest", null, false)]                          // 'Greatest' project not a FP
    [InlineData(@"C:\repo\App\Service.cs", "App", null, false)]                                // plain production
    [InlineData(@"C:\repo\App\Service.cs", null, null, false)]                                 // null project name
    [InlineData(@"C:\test\repo\App\Service.cs", "App", @"C:\test\repo\App", false)]            // ambient 'test' ancestor ignored
    public void IsTest_AppliesProjectFileAndDirHeuristics(string path, string? project, string? projectDir, bool expected)
    {
        TestFileClassifier.IsTest(path, project, projectDir).Should().Be(expected);
    }

    [Fact]
    public void IsTest_IsCaseInsensitive()
    {
        TestFileClassifier.IsTest(@"C:\repo\App\WIDGETTESTS.CS", "App").Should().BeTrue();
        TestFileClassifier.IsTest(@"C:\repo\App\Foo.cs", "APP.TESTS").Should().BeTrue();
    }
}
