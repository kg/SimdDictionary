using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Benchmarks {
    public abstract class DictSuiteBase<T>
        where T : IDictionary<int, int> {

        public int Size = 1024;
        public T Dict;
        public List<int> Keys, UnusedKeys, Values;

        public DictSuiteBase () {
            // Setup will initialize it.
            Unsafe.SkipInit(out Dict);
        }

        [GlobalSetup]
        public virtual void Setup () {
            Dict = Activator.CreateInstance<T>();
            Keys = new List<int>(Size);
            UnusedKeys = new List<int>(Size);
            Values = new List<int>(Size);

            var existingKeys = new HashSet<int>();
            var rng = new Random(1234);
            for (int i = 0; i < Size; i++) {
                int v = rng.Next();
                while (true) {
                    int k = rng.Next();
                    if (!existingKeys.Contains(k)) {
                        existingKeys.Add(k);
                        Keys.Add(k);
                        Values.Add(v);
                        Dict.Add(k, v);
                        break;
                    }
                }
            }

            for (int i = 0; i < Size; i++) {
                while (true) {
                    int k = rng.Next();
                    if (!existingKeys.Contains(k)) {
                        existingKeys.Add(k);
                        UnusedKeys.Add(k);
                        break;
                    }
                }
            }
        }
    }

    public class Insertion<T> : DictSuiteBase<T>
        where T : IDictionary<int, int> {

        [Benchmark]
        public void InsertExisting () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[i], Values[i]);
        }

        [Benchmark]
        public void InsertNew () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(UnusedKeys[i], Values[i]);
        }
    }

    public class Lookup<T> : DictSuiteBase<T>
        where T : IDictionary<int, int> {
    }
}
