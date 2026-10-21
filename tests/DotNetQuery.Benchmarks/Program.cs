using BenchmarkDotNet.Running;

// Runs all *Benchmarks classes in this assembly. Filter from the CLI, e.g.:
//   dotnet run -c Release -- --filter *QueryKey*
//   dotnet run -c Release -- --list flat
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
