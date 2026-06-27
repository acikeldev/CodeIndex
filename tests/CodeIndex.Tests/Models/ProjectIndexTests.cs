using CodeIndex.Models;

namespace CodeIndex.Tests.Models;

public sealed class ProjectIndexTests
{
    [Fact]
    public void ProjectIndex_Properties_RoundTrip()
    {
        SourceFileIndex file = new()
        {
            FileName = "Foo.cs",
            FullPath = @"C:\Repo\Foo.cs",
            Namespace = "App",
            IndexedAtUtc = DateTime.UtcNow,
        };

        ProjectIndex project = new()
        {
            Name = "MyApp",
            Directory = @"C:\Repo",
            ProjectFilePath = @"C:\Repo\MyApp.csproj",
            SourceFiles = [file],
        };

        project.Name.Should().Be("MyApp");
        project.Directory.Should().Be(@"C:\Repo");
        project.ProjectFilePath.Should().Be(@"C:\Repo\MyApp.csproj");
        project.SourceFiles.Should().ContainSingle().Which.FileName.Should().Be("Foo.cs");
    }

    [Fact]
    public void ProjectIndex_DefaultSourceFiles_IsEmpty()
    {
        ProjectIndex project = new()
        {
            Name = "MyApp",
            Directory = @"C:\Repo",
            ProjectFilePath = @"C:\Repo\MyApp.csproj",
        };

        project.SourceFiles.Should().BeEmpty();
    }
}
