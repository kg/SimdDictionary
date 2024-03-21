using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using SimdDictionary;

namespace Benchmarks {
    public class Program {
        public static void Main (string[] args) {
            var test = new SimdDictionary<int, int>();
            var rng = new Random(1234);
            var keys = new List<int>();
            for (int i = 0; i < 1024; i++)
                keys.Add(rng.Next());
            for (int i = 0; i < 1024; i++)
                test.Add(keys[i], i * 2 + 1);
            for (int i = 0; i < 1024; i++)
                test.TryGetValue(keys[i], out _);
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, GetConfig());
        }

        public static IConfig GetConfig () =>
            DefaultConfig.Instance
                .WithOption(ConfigOptions.JoinSummary, true);
    }
}
