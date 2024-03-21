using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using TKey = System.Int64;
using TValue = System.Int64;

namespace Benchmarks {
    public abstract class DictSuiteBase<T>
        where T : IDictionary<TKey, TValue> {

        public int Size = 4096;
        public T Dict;
        public List<TKey> Keys, UnusedKeys;
        public List<TValue> Values;

        public DictSuiteBase () {
            // Setup will initialize it.
            Unsafe.SkipInit(out Dict);
            Unsafe.SkipInit(out Keys);
            Unsafe.SkipInit(out UnusedKeys);
            Unsafe.SkipInit(out Values);
        }

        [GlobalSetup]
        public virtual void Setup () {
            var ctor = typeof(T).GetConstructor(new [] { typeof(int) });
            if (ctor == null)
                throw new Exception("Ctor is missing????");
            // HACK: Don't benchmark growth, since we don't have load factor management yet
            // We initialize with Size items and then add Size more during insertion benchmark
            Dict = (T)ctor.Invoke(new object[] { Size });
            Keys = new List<TKey>(Size);
            UnusedKeys = new List<TKey>(Size);
            Values = new List<TValue>(Size);

            var existingKeys = new HashSet<TKey>();
            var rng = new Random(1234);
            for (int i = 0; i < Size; i++) {
                var v = rng.NextInt64();
                while (true) {
                    var k = rng.NextInt64();
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
        where T : IDictionary<TKey, TValue> {

        [Benchmark]
        public void InsertExisting () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[i], Values[i]);
        }

        [Benchmark]
        public void Fill () {
            Dict.Clear();
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[i], Values[i]);
        }
    }

    public class Lookup<T> : DictSuiteBase<T>
        where T : IDictionary<TKey, TValue> {

        [Benchmark]
        public void FindExisting () {
            for (int i = 0; i < Size; i++) {
                if (!Dict.TryGetValue(Keys[i], out var value))
                    throw new Exception($"Key {Keys[i]} not found");
                if (value != Values[i])
                    throw new Exception($"Found value did not match: {value} != {Values[i]}");
            }
        }

        [Benchmark]
        public void FindMissing () {
            for (int i = 0; i < Size; i++) {
                if (Dict.TryGetValue(UnusedKeys[i], out _))
                    throw new Exception("Found missing item");
            }
        }
    }

    public class Removal<T> : DictSuiteBase<T>
        where T : IDictionary<TKey, TValue> {

        [Benchmark]
        public void EmptyThenRefill () {
            for (int i = 0; i < Size; i++) {
                if (!Dict.Remove(Keys[i]))
                    throw new Exception($"Key {Keys[i]} not removed");
            }

            for (int i = 0; i < Size; i++)
                Dict.Add(Keys[i], Values[i]);

            if (Dict.Count != Size)
                throw new Exception("Dict size changed");
        }

        [Benchmark]
        public void RemoveMissing () {
            for (int i = 0; i < Size; i++) {
                if (Dict.Remove(UnusedKeys[i]))
                    throw new Exception($"Key {UnusedKeys[i]} removed though it was present");
            }

            if (Dict.Count != Size)
                throw new Exception("Dict size changed");
        }
    }
}
