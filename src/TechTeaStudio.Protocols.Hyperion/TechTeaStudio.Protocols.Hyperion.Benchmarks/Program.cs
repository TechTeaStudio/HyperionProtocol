using BenchmarkDotNet.Running;

namespace TechTeaStudio.Protocols.Hyperion.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
