using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

public sealed class FileReaderTests
{
    [Fact]
    public void ReadFileLines_MissingFile_ReturnsMissingFailure()
    {
        InMemoryFileSystem fs = new();
        FileReader reader = new(fs);

        FileReader.ReadResult result = reader.ReadFileLines(@"C:\repo\Absent.cs");

        result.Success.Should().BeFalse();
        result.Lines.Should().BeNull();
        result.FailureReason.Should().Be("missing");
    }

    [Fact]
    public void ReadFileLines_ExistingFile_ReturnsLinesAndSuccess()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\File.cs", "line one\nline two\nline three");
        FileReader reader = new(fs);

        FileReader.ReadResult result = reader.ReadFileLines(@"C:\repo\File.cs");

        result.Success.Should().BeTrue();
        result.FailureReason.Should().BeNull();
        result.Lines.Should().Equal("line one", "line two", "line three");
    }

    [Fact]
    public void ReadFileLines_ExistingFileWithCrlf_SplitsWithoutTrailingEmptyLine()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\Crlf.cs", "alpha\r\nbeta\r\n");
        FileReader reader = new(fs);

        FileReader.ReadResult result = reader.ReadFileLines(@"C:\repo\Crlf.cs");

        result.Success.Should().BeTrue();
        result.Lines.Should().Equal("alpha", "beta");
    }

    [Fact]
    public void ReadFileLines_EmptyFile_ReturnsSuccessWithEmptyLines()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\Empty.cs", string.Empty);
        FileReader reader = new(fs);

        FileReader.ReadResult result = reader.ReadFileLines(@"C:\repo\Empty.cs");

        result.Success.Should().BeTrue();
        result.FailureReason.Should().BeNull();
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public void ReadFileLines_ReadThrows_ReturnsExceptionTypeNameAsFailureReason()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.FileExists(@"C:\repo\Locked.cs").Returns(true);
        fs.ReadAllLines(@"C:\repo\Locked.cs").Returns(_ => throw new IOException("locked"));
        FileReader reader = new(fs);

        FileReader.ReadResult result = reader.ReadFileLines(@"C:\repo\Locked.cs");

        result.Success.Should().BeFalse();
        result.Lines.Should().BeNull();
        result.FailureReason.Should().Be("IOException");
    }

    [Fact]
    public void ReadFileLines_UnauthorizedAccess_PreservesSpecificExceptionName()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.FileExists(@"C:\repo\Denied.cs").Returns(true);
        fs.ReadAllLines(@"C:\repo\Denied.cs").Returns(_ => throw new UnauthorizedAccessException());
        FileReader reader = new(fs);

        FileReader.ReadResult result = reader.ReadFileLines(@"C:\repo\Denied.cs");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("UnauthorizedAccessException");
    }
}
