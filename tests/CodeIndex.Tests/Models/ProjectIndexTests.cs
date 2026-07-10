using CodeIndex.Models;

namespace CodeIndex.Tests.Models;

public sealed class ProjectIndexTests
{
    [Fact]
    public void ProjectIndex_HoldsNameDirAndRelativeSourceFiles()
    {
        ProjectIndex p = new()
        {
            Name = "App",
            ProjectDirPath = @"C:\Repo\App",
            SourceFiles = ["Services/UserService.cs", "Program.cs"],
        };

        p.Name.Should().Be("App");
        p.ProjectDirPath.Should().Be(@"C:\Repo\App");
        p.SourceFiles.Should().BeEquivalentTo(["Services/UserService.cs", "Program.cs"]);
    }

    [Fact]
    public void ProjectIndex_SourceFiles_DefaultsToEmpty()
    {
        ProjectIndex p = new() { Name = "App", ProjectDirPath = @"C:\Repo\App" };

        p.SourceFiles.Should().BeEmpty();
    }
}
