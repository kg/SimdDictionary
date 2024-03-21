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
            var test = new SimdDictionary<long, long>();
            var rng = new Random(1234);
            int c = 4096, d = 4096 * 5;
            var keys = new List<long>();
            for (int i = 0; i < c; i++)
                keys.Add(rng.NextInt64());
            for (int i = 0; i < c; i++)
                test.Add(keys[i], i * 2 + 1);

            for (int j = 0; j < d; j++)
                for (int i = 0; i < c; i++)
                    if (!test.TryGetValue(keys[i], out _))
                        throw new Exception();

            var copy = new SimdDictionary<long, long>(test);
            for (int i = 0; i < c; i++)
                if (!copy.TryGetValue(keys[i], out _))
                    throw new Exception();

            for (int i = 0; i < c; i++)
                if (!test.Remove(keys[i]))
                    throw new Exception();

            for (int i = 0; i < c; i++)
                if (test.TryGetValue(keys[i], out _))
                    throw new Exception();

            if (test.Count != 0)
                throw new Exception();

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, GetConfig());
        }

        public static IConfig GetConfig () =>
            DefaultConfig.Instance
                .WithOption(ConfigOptions.JoinSummary, true);
    }
}
