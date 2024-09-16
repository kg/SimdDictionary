using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Diagnostics;
using SimdDictionary;

namespace Benchmarks {
    public class Program {
        public static void Main (string[] args) {
            // Self-test before running benchmark suite

            Console.WriteLine("Running self-test...");

            var rng = new Random(1234);
            int c = 4096, d = 4096 * (Debugger.IsAttached ? 5 : 50), e = 4096 * (Debugger.IsAttached ? 1 : 15);
            List<long> keys = new (c),
                unusedKeys = new (c),
                values = new (c);
            // Don't pre-allocate capacity, so that we check growth/rehashing
            var test = new SimdDictionary<long, long>(0);

            for (int i = 0; i < c; i++) {
                keys.Add(rng.NextInt64());
                values.Add(i * 2 + 1);
            }

            for (int i = 0; i < c; i++) {
                var unusedKey = rng.NextInt64();
                while (keys.Contains(unusedKey))
                    unusedKey = rng.NextInt64();
                unusedKeys.Add(unusedKey);
            }

            for (int i = 0; i < c; i++)
                test.Add(keys[i], values[i]);

            // Integrity check
            var expectedKeySet = keys.OrderBy(k => k).ToList();
            var enumeratedKeySet = test.Keys.OrderBy(k => k).ToList();
            if (!expectedKeySet.SequenceEqual(enumeratedKeySet)) {
                var missing = new HashSet<long>(keys);
                missing.ExceptWith(enumeratedKeySet);
                throw new KeyNotFoundException();
            }

            for (int j = 0; j < d; j++)
                for (int i = 0; i < c; i++)
                    if (!test.TryGetValue(keys[i], out _))
                        throw new Exception();

            var keyList = test.Keys.ToArray();
            var valueList = test.Values.ToArray();

            for (int j = 0; j < e; j++)
            {
                var copy = new SimdDictionary<long, long>(test);
                for (int i = 0; i < c; i++)
                    if (!copy.TryGetValue(keys[i], out _))
                        throw new Exception();

                for (int i = 0; i < c; i++)
                    if (copy.Remove(unusedKeys[i]))
                        throw new Exception();

                for (int i = 0; i < c; i++)
                    if (!copy.Remove(keys[i]))
                        throw new Exception();

                for (int i = 0; i < c; i++)
                    if (copy.TryGetValue(keys[i], out _))
                        throw new Exception();

                if (copy.Count != 0)
                    throw new Exception();
            }

            // Run benchmark suite
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args, GetConfig());

            Console.ReadLine();
        }

        public static IConfig GetConfig () =>
            DefaultConfig.Instance
                // .AddJob(Job.Default.WithRuntime(NativeAotRuntime.Net80))
                .WithOption(ConfigOptions.JoinSummary, true);
    }
}
