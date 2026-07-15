using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using CodeIndex.Benchmarks;
using CodeIndex.Benchmarks.ContextCost;

// Run as: dotnet run -c Release -- [filter]
// Examples:
//   dotnet run -c Release                          (all BenchmarkDotNet latency benchmarks)
//   dotnet run -c Release -- --filter *Search*     (search only)
//   dotnet run -c Release -- --filter *Parsing*    (parsing only)
//   dotnet run -c Release -- --filter *Rebuild*    (rebuild only)
//   dotnet run -c Release -- --filter *Cache*      (cache only)
//   dotnet run -c Release -- context-cost [root]   (token cost vs a grep+read agent — README numbers)

// The context-cost report is not a BenchmarkDotNet job (it measures tokens ingested, not wall time),
// so it is dispatched before the switcher.
if (args.Length > 0 && args[0] == "context-cost")
{
    ContextCostReport.Run(args);
    return;
}

IConfig config = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithBuildTimeout(TimeSpan.FromMinutes(10))
    .AddExporter(MarkdownExporter.GitHub)
    .AddLogger(ConsoleLogger.Default)
    .AddValidator(JitOptimizationsValidator.FailOnError);

BenchmarkSwitcher
    .FromAssembly(typeof(ParsingBenchmarks).Assembly)
    .Run(args, config);
