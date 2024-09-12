using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using TKey = System.Int64;
using TValue = System.Int64;

namespace Benchmarks {
    public abstract class DictSuiteBase<T>
        where T : IDictionary<TKey, TValue> {

        public T Dict;
        public List<TKey> Keys, UnusedKeys;
        public List<TValue> Values;

        public virtual int Size => 8192;
        public virtual bool Populate => true;

        public DictSuiteBase () {
            // Setup will initialize it.
            Unsafe.SkipInit(out Dict);
            Unsafe.SkipInit(out Keys);
            Unsafe.SkipInit(out UnusedKeys);
            Unsafe.SkipInit(out Values);
        }

        [GlobalSetup]
        public virtual void Setup () {
            // HACK: Don't benchmark growth, since we don't have load factor management yet
            // We initialize with Size items and then add Size more during insertion benchmark
            if (Populate) {
                // thanks nativeaot
                if (typeof(T) == typeof(SimdDictionary.SimdDictionary<TKey, TValue>))
                    Dict = (T)(object)new SimdDictionary.SimdDictionary<TKey, TValue>(Size);
                else if (typeof(T) == typeof(Dictionary<TKey, TValue>))
                    Dict = (T)(object)new Dictionary<TKey, TValue>(Size);
                else
                    throw new Exception();
            }
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
                        if (Populate)
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
        public void ClearThenRefill () {
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
        public void RemoveItemsThenRefill () {
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

    public class Resize<T> : DictSuiteBase<T>
        where T : IDictionary<TKey, TValue>, new() {

        public override bool Populate => false;
        public override int Size => 1024 * 16;

        [Benchmark]
        public void CreateDefaultSizeAndFill () {
            var temp = new T();
            for (int i = 0; i < Size; i++)
                temp.Add(Keys[i], Values[i]);
        }
    }

    public abstract class Iterate<T> : DictSuiteBase<T>
        where T : IDictionary<TKey, TValue>, new() {

        protected abstract IEnumerable<TKey> GetKeys ();
        protected abstract IEnumerable<TValue> GetValues ();

        [Benchmark]
        public void EnumeratePairs () {
            var temp = new List<KeyValuePair<TKey, TValue>>(Size);
            foreach (var item in Dict)
                temp.Add(item);
            if (temp.Count != Dict.Count)
                throw new Exception();
            temp.Clear();
        }

        [Benchmark]
        public void EnumerateKeys () {
            var temp = new List<TKey>(Size);
            foreach (var item in GetKeys())
                temp.Add(item);
            if (temp.Count != Dict.Count)
                throw new Exception();
            temp.Clear();
        }

        [Benchmark]
        public void EnumerateValues () {
            var temp = new List<TValue>(Size);
            foreach (var item in GetValues())
                temp.Add(item);
            if (temp.Count != Dict.Count)
                throw new Exception();
            temp.Clear();
        }
    }

    public class Collider {
        public override int GetHashCode () => 0;
        public override bool Equals (object? obj) => object.ReferenceEquals(this, obj);
        public override string ToString () => $"Collider {base.GetHashCode()}";
    }

    public abstract class Collisions<T>
        where T : IDictionary<Collider, Collider> {

        public int Size = 1024;
        public T Dict;
        public List<Collider> Keys, UnusedKeys;

        public Collisions () {
            // Setup will initialize it.
            Unsafe.SkipInit(out Dict);
            Unsafe.SkipInit(out Keys);
            Unsafe.SkipInit(out UnusedKeys);
        }

        [GlobalSetup]
        public virtual void Setup () {
            var ctor = typeof(T).GetConstructor(new [] { typeof(int) });
            if (ctor == null)
                throw new Exception("Ctor is missing????");
            // HACK: Don't benchmark growth, since we don't have load factor management yet
            // We initialize with Size items and then add Size more during insertion benchmark
            Dict = (T)ctor.Invoke(new object[] { Size });
            Keys = new List<Collider>(Size);
            UnusedKeys = new List<Collider>(Size);

            for (int i = 0; i < Size; i++)
                Keys.Add(new Collider());

            for (int i = 0; i < Size; i++)
                UnusedKeys.Add(new Collider());
        }

        [Benchmark]
        public void AddSameRepeatedlyThenClear () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[0], Keys[0]);

            Dict.Clear();
        }

        [Benchmark]
        public void FillThenClear () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[i], Keys[i]);

            Dict.Clear();
        }

        [Benchmark]
        public void FindExistingWithCollisions () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[i], Keys[i]);

            for (int i = 0; i < Size; i++)
                if (!Dict.ContainsKey(Keys[i]))
                    throw new Exception($"Not found: {Keys[i]}");

            Dict.Clear();
        }

        [Benchmark]
        public void FindMissingWithCollisions () {
            for (int i = 0; i < Size; i++)
                Dict.TryAdd(Keys[i], Keys[i]);

            for (int i = 0; i < Size; i++)
                if (Dict.ContainsKey(UnusedKeys[i]))
                    throw new Exception($"Not found: {UnusedKeys[i]}");

            Dict.Clear();
        }
    }
}
