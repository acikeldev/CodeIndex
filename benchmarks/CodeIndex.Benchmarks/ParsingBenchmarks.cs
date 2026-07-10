using System.Text;
using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Models;
using CodeIndex.Parsing;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures SourceFileParser.Parse() in isolation — the Roslyn SyntaxTree
/// construction and member extraction for a single C# file.
///
/// Varying TypeCount controls the number of types and members per file,
/// which is the primary driver of Roslyn parsing cost.
/// I/O is eliminated via InMemoryFileSystem.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[MinIterationCount(20)]
[MaxIterationCount(100)]
public class ParsingBenchmarks
{
    private const string ProjectName = "BenchApp";

    private InMemoryFileSystem _fs = null!;
    private SourceFileParser _parser = null!;

    /// <summary>
    /// Approximate number of types in the generated file.
    /// Small = 1, Medium = 5, Large = 15 types with full method/property sets.
    /// </summary>
    [Params(1, 5, 15)]
    public int TypeCount { get; set; }

    private string _filePath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fs = new InMemoryFileSystem();
        _parser = new SourceFileParser(_fs);

        _filePath = @"C:\Repo\Bench.cs";

        // Generate a file with exactly TypeCount types, each with ~5 props + ~6 methods.
        StringBuilder sb = new();
        sb.AppendLine("namespace BenchApp;");
        sb.AppendLine();
        for (int i = 0; i < TypeCount; i++)
        {
            sb.AppendLine($"public class BenchType{i} : IDisposable");
            sb.AppendLine("{");
            for (int p = 0; p < 5; p++)
            {
                sb.AppendLine($"    public string Prop{p} {{ get; set; }} = string.Empty;");
            }

            for (int m = 0; m < 6; m++)
            {
                sb.AppendLine($"    public void Method{m}(int x) {{ }}");
            }

            sb.AppendLine("    public void Dispose() { }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        _fs.AddFile(_filePath, sb.ToString());
    }

    [Benchmark(Description = "Parse single file")]
    public SourceFileIndex? ParseFile() => _parser.Parse(_filePath, ProjectName);
}
