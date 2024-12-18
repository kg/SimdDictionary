﻿using System;
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
using TKey = System.Int64;
using TValue = System.Int64;

namespace Benchmarks {
    public class Program {
        private unsafe static TKey NextKey (Random rng) {
            return rng.NextInt64();
        }

        private unsafe static TValue NextValue (Random rng) =>
            rng.NextInt64();
        
        public static void Main (string[] args) {
            // Self-test before running benchmark suite

            Console.WriteLine("Running self-test...");

            var rng = new Random(1234);
            int c = 16 * 1024,
                d = 4096 * (Debugger.IsAttached ? 1 : 5),
                e = 1024 * (Debugger.IsAttached ? 1 : 5),
                f = 40960;
            List<TKey> keys = new(c),
                unusedKeys = new(c);
            List<TValue> values = new (c);

            Console.WriteLine("TailCollider...");
            List<TailCollider> tcs = new();
            var tcTest = new VectorizedDictionary<TailCollider, int>();
            for (int i = 0; i < f; i++) {
                var tc = new TailCollider();
                tcs.Add(tc);
                tcTest.Add(tc, i);
            }
            if (tcTest.Count != tcs.Count)
                throw new Exception();
            for (int i = 0; i < tcs.Count; i++) {
                if (tcTest[tcs[i]] != i)
                    throw new Exception();
            }
            tcTest.Clear();
            if (tcTest.Count != 0)
                throw new Exception();
            for (int i = 0; i < tcs.Count; i++) {
                if (tcTest.TryGetValue(tcs[i], out _))
                    throw new Exception();
            }

            Console.WriteLine("Int64 init...");
            // Validate that pre-sized dictionaries don't grow
            var capTest = new VectorizedDictionary<TKey, TValue>(VectorizedDictionary<TKey, TValue>.BucketSizeI);
            // FIXME: This value below needs to change based on the default load factor of the dictionary!
            int expectedCapacity = 32; // Not BucketSizeI due to load factor and prime bucket counts
            if (capTest.Capacity != expectedCapacity)
                throw new Exception($"Pre-sized dict capacity is wrong: {capTest.Capacity} != {expectedCapacity}");

            for (int i = 0; i < capTest.Capacity; i++)
                capTest.Add(i, i);
            if (capTest.Capacity != expectedCapacity)
                throw new Exception($"Pre-sized dict grew after filling {capTest.Capacity} != {expectedCapacity}");

            // Don't pre-allocate capacity, so that we check growth/rehashing
            var test = new VectorizedDictionary<TKey, TValue>(0);

            for (int i = 0; i < c; i++) {
                var key = NextKey(rng);
                while (keys.Contains(key))
                    key = NextKey(rng);
                keys.Add(key);
                values.Add(i * 2 + 1);
            }

            for (int i = 0; i < c; i++) {
                var unusedKey = NextKey(rng);
                while (keys.Contains(unusedKey) || unusedKeys.Contains(unusedKey))
                    unusedKey = NextKey(rng);
                unusedKeys.Add(unusedKey);
            }

            for (int i = 0; i < c; i++)
                test.Add(keys[i], values[i]);

            Console.WriteLine("Int64 integrity check...");
            // Integrity check
            var expectedKeySet = keys.OrderBy(k => k).ToList();
            var enumeratedKeySet = test.Keys.OrderBy(k => k).ToList();
            if (!expectedKeySet.SequenceEqual(enumeratedKeySet)) {
                var missing = new HashSet<TKey>(expectedKeySet);
                missing.ExceptWith(enumeratedKeySet);
                throw new KeyNotFoundException();
            }

            for (int i = 0; i < c; i++)
                if (!test.ContainsValue(values[i]))
                    throw new Exception();

            for (int j = 0; j < d; j++)
                for (int i = 0; i < c; i++)
                    if (!test.TryGetValue(keys[i], out _))
                        throw new Exception();

            var keyList = test.Keys.ToArray();
            var valueList = test.Values.ToArray();

            Console.WriteLine("Int64 cloning and mutation...");
            var copy = new VectorizedDictionary<TKey, TValue>(test);
            for (int j = 0; j < e; j++)
            {
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

                copy.TrimExcess();
                if (copy.Capacity >= 14)
                    throw new Exception();

                for (int i = 0; i < c; i++) {
                    copy.Add(keys[i], values[i]);
                    if (!copy.TryGetValue(keys[i], out _))
                        throw new Exception();
                }

                for (int i = 0; i < c; i++)
                    if (!copy.TryGetValue(keys[i], out _))
                        throw new Exception();

                if (copy.Count != c)
                    throw new Exception();
            }

            Console.Clear();
            Console.WriteLine("Integrity check done.");

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
