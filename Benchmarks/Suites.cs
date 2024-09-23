using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using SimdDictionary;
using TKey = System.Int64;
using TValue = System.Int64;

namespace Benchmarks {
    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class BCLInsertion : Insertion<Dictionary<TKey, TValue>> {
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class BCLLookup : Lookup<Dictionary<TKey, TValue>> { 
        protected override bool TryGetValue (long key, out long value) =>
            Dict.TryGetValue(key, out value);

        protected override bool ContainsKey (long key) =>
            Dict.ContainsKey(key);

        protected override bool ContainsValue (long value) =>
            Dict.ContainsValue(value);
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class BCLRemoval : Removal<Dictionary<TKey, TValue>> { 
    }

    public class BCLClearing : Clearing<Dictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class BCLResize : Resize<Dictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class BCLCollisions : Collisions<Dictionary<Collider, Collider>, Collider> {
    }

    [MemoryDiagnoser()]
    public class BCLTailCollisions : Collisions<Dictionary<TailCollider, TailCollider>, TailCollider> {
    }

    [MemoryDiagnoser()]
    public class BCLHeadCollisions : Collisions<Dictionary<HeadCollider, HeadCollider>, HeadCollider> {
    }

    [MemoryDiagnoser()]
    public class BCLIterate : Iterate<Dictionary<TKey, TValue>> {
        protected override IEnumerable<TKey> GetKeys () => Dict.Keys;
        protected override IEnumerable<TValue> GetValues () => Dict.Values;
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class SimdInsertion : Insertion<SimdDictionary<TKey, TValue>> {
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class SimdLookup : Lookup<SimdDictionary<TKey, TValue>> {
        protected override bool TryGetValue (long key, out long value) =>
            Dict.TryGetValue(key, out value);

        protected override bool ContainsKey (long key) =>
            Dict.ContainsKey(key);

        protected override bool ContainsValue (long value) =>
            Dict.ContainsValue(value);
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class SimdRemoval : Removal<SimdDictionary<TKey, TValue>> { 
    }

    public class SimdClearing : Clearing<SimdDictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class SimdResize : Resize<SimdDictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class SimdCollisions : Collisions<SimdDictionary<Collider, Collider>, Collider> {
    }

    [MemoryDiagnoser()]
    public class SimdTailCollisions : Collisions<SimdDictionary<TailCollider, TailCollider>, TailCollider> {
    }

    [MemoryDiagnoser()]
    public class SimdHeadCollisions : Collisions<SimdDictionary<HeadCollider, HeadCollider>, HeadCollider> {
    }

    [MemoryDiagnoser()]
    public class SimdIterate : Iterate<SimdDictionary<TKey, TValue>> {
        protected override IEnumerable<TKey> GetKeys () => Dict.Keys;
        protected override IEnumerable<TValue> GetValues () => Dict.Values;
    }

    [MemoryDiagnoser()]
    public class BCLMemoryUsage : MemoryUsage<Dictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class SimdMemoryUsage : MemoryUsage<SimdDictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    [DisassemblyDiagnoser()]
    public class SimdAlternateLookup {
        public sealed class ArrayComparer : IEqualityComparer<char[]> {
            public bool Equals (char[]? x, char[]? y) =>
                x.SequenceEqual(y);

            public int GetHashCode ([DisallowNull] char[] obj) =>
                obj.Length;
        }

        public sealed class Comparer : IAlternateComparer<string, char[]> {
            public bool Equals (string key, char[] other) {
                if (key.Length != other.Length)
                    return false;
                for (int i = 0; i < other.Length; i++)
                    if (key[i] != other[i])
                        return false;
                return true;
            }

            public int GetHashCode (char[] other) {
                // FIXME
                return (new string(other)).GetHashCode();
            }
        }

        public const int Size = 4096;
        SimdDictionary<string, Int64> Dict = new (Size);
        SimdDictionary<string, Int64>.AlternateLookup<char[]> Lookup;
        Random RNG = new Random(1234);
        List<char[]> Keys = new(Size), UnusedKeys = new(Size);
        List<Int64> Values = new (Size);

        public SimdAlternateLookup() {
            var ac = new ArrayComparer();

            for (int i = 0; i < Size; i++) {
                var key = NextKey();
                while (Keys.Contains(key, ac))
                    key = NextKey();
                var value = RNG.NextInt64();
                Keys.Add(key);
                Values.Add(value);
                Dict.Add(new string(key), value);
            }

            for (int i = 0; i < Size; i++) {
                var key = NextKey();
                while (Keys.Contains(key, ac) || UnusedKeys.Contains(key, ac))
                    key = NextKey();
                UnusedKeys.Add(key);
            }

            // FIXME: Comparer
            Lookup = new (Dict, new Comparer());
        }

        private char[] NextKey () {
            var l = RNG.Next(2, 8);
            var result = new char[l];
            for (int i = 0; i < l; i++)
                result[i] = (char)RNG.Next(32, 127);
            return result;
        }

        [Benchmark]
        public void Accessor () {
            for (int i = 0; i < Size; i++) {
                var value = Lookup[Keys[i]];
                if (value != Values[i])
                    throw new Exception();
            }
        }

        [Benchmark]
        public void TryGetValueExisting () {
            for (int i = 0; i < Size; i++) {
                if (!Lookup.TryGetValue(Keys[i], out var value))
                    throw new Exception();
                if (value != Values[i])
                    throw new Exception();
            }
        }

        [Benchmark]
        public void TryGetValueMissing () {
            for (int i = 0; i < Size; i++) {
                if (Lookup.TryGetValue(UnusedKeys[i], out _))
                    throw new Exception();
            }
        }
    }

    public class ClearingWithRefs {
        const int Size = 40960;

        public Dictionary<string, string> BCL = new (Size);
        public SimdDictionary<string, string> SIMD = new (Size);
        public List<string> Strings = new (Size);

        [GlobalSetup]
        public void Setup () {
            for (int i = 0; i < Size; i++) {
                var s = i.ToString();
                Strings.Add(s);
            }
        }

        [Benchmark]
        public void BCLClearAndRefill () {
            BCL.Clear();
            for (int i = 0; i < Size; i++) {
                var s = Strings[i];
                BCL.Add(s, s);
            }
        }

        [Benchmark]
        public void SIMDClearAndRefill () {
            SIMD.Clear();
            for (int i = 0; i < Size; i++) {
                var s = Strings[i];
                SIMD.Add(s, s);
            }
        }
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class StringLookup {
        const int Size = 40960;

        public Dictionary<string, long> BCL = new (Size);
        public SimdDictionary<string, long> SIMD = new (Size);
        public List<string> Strings = new (Size);

        [GlobalSetup]
        public void Setup () {
            for (int i = 0; i < Size; i++) {
                var s = i.ToString();
                Strings.Add(s);
                BCL.Add(s, i);
                SIMD.Add(s, i);
            }
        }

        [Benchmark]
        public void FindExistingSIMD () {
            for (int i = 0; i < Size; i++) {
                var s = Strings[i];
                if (!SIMD.TryGetValue(s, out var value) || (value != i))
                    throw new Exception();
            }
        }

        [Benchmark]
        public void FindExistingBCL () {
            for (int i = 0; i < Size; i++) {
                var s = Strings[i];
                if (!BCL.TryGetValue(s, out var value) || (value != i))
                    throw new Exception();
            }
        }
    }
}
