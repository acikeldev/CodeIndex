using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using CodeIndex.Benchmarks;

// Run as: dotnet run -c Release -- [filter]
// Examples:
//   dotnet run -c Release                          (all benchmarks)
//   dotnet run -c Release -- --filter *Search*     (search only)
//   dotnet run -c Release -- --filter *Parsing*    (parsing only)
//   dotnet run -c Release -- --filter *Rebuild*    (rebuild only)
//   dotnet run -c Release -- --filter *Cache*      (cache only)

IConfig config = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithBuildTimeout(TimeSpan.FromMinutes(10))
    .AddExporter(MarkdownExporter.GitHub)
    .AddLogger(ConsoleLogger.Default)
    .AddValidator(JitOptimizationsValidator.FailOnError);

BenchmarkSwitcher
    .FromAssembly(typeof(ParsingBenchmarks).Assembly)
    .Run(args, config);
