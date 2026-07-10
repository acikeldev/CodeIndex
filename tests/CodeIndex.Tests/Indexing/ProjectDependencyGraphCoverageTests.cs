using System;
using System.Collections.Generic;
using System.IO;
using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using CodeIndex.Models;

namespace CodeIndex.Tests.Indexing;

public sealed class ProjectDependencyGraphCoverageTests
{
    private static IReadOnlyDictionary<string, ProjectIndex> SingleProject()
    {
        ProjectIndex project = new()
        {
            Name = "P",
            ProjectDirPath = @"C:\repo\P",
        };
        return new Dictionary<string, ProjectIndex>(StringComparer.OrdinalIgnoreCase)
        {
            ["P"] = project,
        };
    }

    [Fact]
    public void Build_WhenReadAllTextThrows_ReturnsEmptyReferences()
    {
        // FindCsproj succeeds (direct path exists) but ReadAllText throws -> ParseReferences catch (lines 82-84).
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        fileSystem.When(x => x.ReadAllText(Arg.Any<string>()))
            .Do(_ => throw new ArgumentException("boom"));

        IReadOnlyDictionary<string, ProjectIndex> projects = SingleProject();

        ProjectDependencyGraph graph = ProjectDependencyGraph.Build(projects, fileSystem);

        graph.GetReferences("P").Should().BeEmpty();
        graph.GetDependents("P").Should().BeEmpty();
    }

    [Fact]
    public void Build_WhenFileExistsThrows_ReturnsEmptyReferences()
    {
        // FindCsproj catch (lines 109-111): FileExists throws -> null csproj -> empty references.
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FileExists(Arg.Any<string>())
            .Returns(_ => throw new ArgumentException("boom"));

        IReadOnlyDictionary<string, ProjectIndex> projects = SingleProject();

        ProjectDependencyGraph graph = ProjectDependencyGraph.Build(projects, fileSystem);

        graph.GetReferences("P").Should().BeEmpty();
        graph.GetDependents("P").Should().BeEmpty();
    }

    [Fact]
    public void Build_WhenEnumerateFilesThrows_ReturnsEmptyReferences()
    {
        // FindCsproj catch (lines 109-111): direct path missing, EnumerateFiles throws -> null csproj -> empty.
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FileExists(Arg.Any<string>()).Returns(false);
        fileSystem.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(_ => throw new ArgumentException("boom"));

        IReadOnlyDictionary<string, ProjectIndex> projects = SingleProject();

        ProjectDependencyGraph graph = ProjectDependencyGraph.Build(projects, fileSystem);

        graph.GetReferences("P").Should().BeEmpty();
        graph.GetDependents("P").Should().BeEmpty();
    }
}
