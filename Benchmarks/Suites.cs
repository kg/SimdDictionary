using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
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

    // FIXME
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
        public sealed class OpaqueComparer : IEqualityComparer<string> {
            public readonly StringComparer ActualComparer = StringComparer.Ordinal;

            public bool Equals (string? x, string? y) {
                return ActualComparer.Equals(x, y);
            }

            public int GetHashCode ([DisallowNull] string obj) {
                return ActualComparer.GetHashCode(obj);
            }
        }

        const int Size = 40960;

        public Dictionary<string, long> BCL = new (Size, new OpaqueComparer());
        public SimdDictionary<string, long> SIMD = new (Size, new OpaqueComparer());
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
                    Environment.FailFast("Failed");
            }
        }

        [Benchmark]
        public void FindExistingBCL () {
            for (int i = 0; i < Size; i++) {
                var s = Strings[i];
                if (!BCL.TryGetValue(s, out var value) || (value != i))
                    Environment.FailFast("Failed");
            }
        }
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class IntLookup {
        const int Size = 102400;

        public Dictionary<int, long> BCL = new (Size);
        public SimdDictionary<int, long> SIMD = new (Size);

        [GlobalSetup]
        public void Setup () {
            for (int i = 0; i < Size; i++) {
                BCL.Add(i, i);
                SIMD.Add(i, i);
            }
        }

        [Benchmark]
        public void FindExistingSIMD () {
            for (int i = 0; i < Size; i++) {
                if (!SIMD.TryGetValue(i, out var value) || (value != i))
                    Environment.FailFast("Failed");
            }
        }

        [Benchmark]
        public void FindExistingBCL () {
            for (int i = 0; i < Size; i++) {
                if (!BCL.TryGetValue(i, out var value) || (value != i))
                    Environment.FailFast("Failed");
            }
        }
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class BigStructLookup {
        public unsafe struct BigStruct {
            // Note that if InnerSize is too big, Bucket will hit an internal limitation in the CLR, since it has to contain 14* K/V pairs.
            // Array of type 'Bucket[...]' from assembly 'SimdDictionary, ...' cannot be created because base value type is too large.
            public const int InnerSize = 128;

            public fixed int Values[InnerSize];

            public BigStruct (int i) {
                for (int j = 0; j < InnerSize; j++)
                    Values[j] = i + j;
            }

            // This needs to be readonly, otherwise calling it on a 'ref readonly' clones the struct and crushes us
            public readonly bool InputMatches (int i) {
                if (Values[0] != i)
                    return false;
                int j = InnerSize - 1;
                if (Values[j] != i + j)
                    return false;
                return true;
            }
        }

        const int Size = 10240;

        public Dictionary<int, BigStruct> BCL = new (Size);
        public SimdDictionary<int, BigStruct> SIMD = new (Size);

        [GlobalSetup]
        public void Setup () {
            for (int i = 0; i < Size; i++) {
                var value = new BigStruct(i);
                BCL.Add(i, value);
                SIMD.Add(i, value);
            }
        }

        [Benchmark]
        public void FindExistingSIMD () {
            for (int i = 0; i < Size; i++) {
                ref readonly var value = ref SIMD.FindValueOrNullRef(i);
                if (Unsafe.IsNullRef(in value))
                    throw new Exception("Key not found");
                // This is extremely expensive unless InputMatches is a readonly method
                if (!value.InputMatches(i))
                    throw new Exception("Result mismatch");
            }
        }

        [Benchmark]
        public void FindExistingBCL () {
            for (int i = 0; i < Size; i++) {
                if (!BCL.TryGetValue(i, out var result))
                    throw new Exception("Key not found");
                if (!result.InputMatches(i))
                    throw new Exception("Result mismatch");
            }
        }

        [Benchmark]
        public void FindMissingSIMD () {
            for (int i = 1; i < Size; i++) {
                ref readonly var value = ref SIMD.FindValueOrNullRef(-i);
                if (!Unsafe.IsNullRef(in value))
                    throw new Exception("Key found");
            }
        }

        [Benchmark]
        public void FindMissingBCL () {
            for (int i = 1; i < Size; i++) {
                if (BCL.TryGetValue(-i, out var result))
                    throw new Exception("Key found");
            }
        }
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class Enumeration {
        const int Size = 10240;

        public SimdDictionary<int, long> SIMD = new (Size);

        [GlobalSetup]
        public void Setup () {
            for (int i = 0; i < Size; i++)
                SIMD.Add(i, i);
        }

        [Benchmark]
        public void RegularForeach () {
            int i = 0;
            foreach (var kvp in SIMD) {
                if (kvp.Value != kvp.Key)
                    throw new Exception("Invalid value");
                i++;
            }
            if (i != Size)
                throw new Exception("Wrong number of items");
        }

        [Benchmark]
        public void CallbackForeach () {
            int i = 0;
            SIMD.ForEach((int _, in int k, in long v) => {
                if (k != v)
                    throw new Exception("Invalid value");
                i++;
                return true;
            });
            if (i != Size)
                throw new Exception("Wrong number of items");
        }

        [Benchmark]
        public void RefForeach () {
            int i = 0;
            var e = SIMD.GetRefEnumerator();
            while (e.MoveNext()) {
                if (e.CurrentValue != e.CurrentKey)
                    throw new Exception("Invalid value");
                i++;
            }
            if (i != Size)
                throw new Exception("Wrong number of items");
        }
    }
}
