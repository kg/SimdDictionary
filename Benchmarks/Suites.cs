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
    public class InsertionBCL : Insertion<Dictionary<TKey, TValue>> {
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class LookupBCL : Lookup<Dictionary<TKey, TValue>> { 
        protected override bool TryGetValue (long key, out long value) =>
            Dict.TryGetValue(key, out value);

        protected override bool ContainsKey (long key) =>
            Dict.ContainsKey(key);

        protected override bool ContainsValue (long value) =>
            Dict.ContainsValue(value);
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class RemovalBCL : Removal<Dictionary<TKey, TValue>> { 
    }

    public class ClearingBCL : Clearing<Dictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class ResizeBCL : Resize<Dictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class CollisionsBCL : Collisions<Dictionary<Collider, Collider>, Collider> {
    }

    [MemoryDiagnoser()]
    public class TailCollisionsBCL : Collisions<Dictionary<TailCollider, TailCollider>, TailCollider> {
    }

    [MemoryDiagnoser()]
    public class HeadCollisionsBCL : Collisions<Dictionary<HeadCollider, HeadCollider>, HeadCollider> {
    }

    [MemoryDiagnoser()]
    public class IterateBCL : Iterate<Dictionary<TKey, TValue>> {
        protected override IEnumerable<TKey> GetKeys () => Dict.Keys;
        protected override IEnumerable<TValue> GetValues () => Dict.Values;
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class InsertionSimd : Insertion<VectorizedDictionary<TKey, TValue>> {
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class LookupSimd : Lookup<VectorizedDictionary<TKey, TValue>> {
        public override void Setup () {
            base.Setup();

            Dict.AnalyzeBuckets(out int normal, out int overflowed, out int degraded);
            double total = normal + overflowed + degraded;
            if (false) {
                using (var sw = new StreamWriter("E:\\Desktop\\simdlookup.log", true, Encoding.UTF8))
                    sw.WriteLine($"{overflowed} ({overflowed / total * 100}%) Overflowed; {degraded} ({degraded / total * 100}%) Degraded");
            }
        }

        protected override bool TryGetValue (long key, out long value) =>
            Dict.TryGetValue(key, out value);

        protected override bool ContainsKey (long key) =>
            Dict.ContainsKey(key);

        protected override bool ContainsValue (long value) =>
            Dict.ContainsValue(value);
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class RemovalSimd : Removal<VectorizedDictionary<TKey, TValue>> { 
    }

    public class ClearingSimd : Clearing<VectorizedDictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class ResizeSimd : Resize<VectorizedDictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class CollisionsSimd : Collisions<VectorizedDictionary<Collider, Collider>, Collider> {
    }

    [MemoryDiagnoser()]
    public class TailCollisionsSimd : Collisions<VectorizedDictionary<TailCollider, TailCollider>, TailCollider> {
    }

    [MemoryDiagnoser()]
    public class HeadCollisionsSimd : Collisions<VectorizedDictionary<HeadCollider, HeadCollider>, HeadCollider> {
    }

    [MemoryDiagnoser()]
    public class IterateSimd : Iterate<VectorizedDictionary<TKey, TValue>> {
        protected override IEnumerable<TKey> GetKeys () => ((IDictionary<TKey, TValue>)Dict).Keys;
        protected override IEnumerable<TValue> GetValues () => ((IDictionary<TKey, TValue>)Dict).Values;
    }

    [MemoryDiagnoser()]
    public class MemoryUsageBCL : MemoryUsage<Dictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class MemoryUsageSimd : MemoryUsage<VectorizedDictionary<TKey, TValue>> {
    }

    public class ClearingWithRefs {
        const int Size = 40960;

        public Dictionary<string, string> BCL = new (Size);
        public VectorizedDictionary<string, string> SIMD = new (Size);
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
        public VectorizedDictionary<string, long> SIMD = new (Size, new OpaqueComparer());
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
        public VectorizedDictionary<int, long> SIMD = new (Size);

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
            public const int InnerSize = 256;

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

        public HashSet<int> Keys = new HashSet<int>(Size);
        public Dictionary<int, BigStruct> BCL = new (Size);
        public VectorizedDictionary<int, BigStruct> SIMD = new (Size);

        [GlobalSetup]
        public void Setup () {
            var rng = new Random(1);
            for (int i = 0; i < Size; i++) {
                var key = rng.Next();
                while (Keys.Contains(key))
                    key = rng.Next();

                var value = new BigStruct(key);
                BCL.Add(key, value);
                SIMD.Add(key, value);
            }
        }

        [Benchmark]
        public void FindExistingSIMDValueRef () {
            foreach (var key in Keys) {
                ref readonly var value = ref SIMD.GetValueRefOrNullRef(key);
                if (Unsafe.IsNullRef(in value))
                    throw new Exception("Key not found");
                // This is extremely expensive unless InputMatches is a readonly method
                if (!value.InputMatches(key))
                    throw new Exception("Result mismatch");
            }
        }

        [Benchmark]
        public void FindExistingSIMD () {
            foreach (var key in Keys) {
                if (!SIMD.TryGetValue(key, out var value))
                    throw new Exception("Key not found");
                if (!value.InputMatches(key))
                    throw new Exception("Result mismatch");
            }
        }

        [Benchmark]
        public void FindExistingBCL () {
            foreach (var key in Keys) {
                if (!BCL.TryGetValue(key, out var result))
                    throw new Exception("Key not found");
                if (!result.InputMatches(key))
                    throw new Exception("Result mismatch");
            }
        }

        // FindMissing is annoying to write with random keys and we know from other tests that SIMD is faster
        //  for missing keys by a significant amount, so it's omitted now
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class Permutation {
        const int Size = 10240;

        public Dictionary<int, long> BCL = new (Size);
        public VectorizedDictionary<int, long> SIMD = new (Size);
        public Func<int, long, long> AddOrUpdateCallback;

        public Permutation () {
            AddOrUpdateCallback = (k, v) => v + 1;
        }

        [GlobalSetup]
        public void Setup () {
            for (int i = 0; i < Size; i++) {
                BCL.Add(i, i);
                SIMD.Add(i, i);
            }
        }

        [Benchmark]
        public void RegularModifyBCL () {
            for (int i = 0; i < Size; i++)
                BCL[i]++;
        }

        [Benchmark]
        public void RegularModifySIMD () {
            for (int i = 0; i < Size; i++)
                SIMD[i]++;
        }

        [Benchmark]
        public void ForeachModifySIMD () {
            SIMD.ForEach((int _, in int k, ref long v) => {
                v++;
                return true;
            });
        }

        [Benchmark]
        public void AddOrUpdateSIMD () {
            for (int i = 0; i < Size; i++)
                SIMD.AddOrUpdate(i, i, AddOrUpdateCallback);
        }
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class Enumeration {
        const int Size = 10240;

        public VectorizedDictionary<int, long> SIMD = new (Size);

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
            SIMD.ForEach((int _, in int k, ref long v) => {
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

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class GetOrAdd {
        const int Size = 10240;

        public Dictionary<int, long> BCL = new (Size);
        public VectorizedDictionary<int, long> SIMD = new (Size);
        public Func<int, long> ValueFactory;

        public GetOrAdd () {
            ValueFactory = (i) => i;
        }

        [GlobalSetup]
        public void Setup () {
        }

        [Benchmark]
        public void RegularBCL () {
            BCL.Clear();
            for (int i = 0; i < Size; i++) {
                if (!BCL.TryGetValue(i, out var l)) {
                    l = ValueFactory(i);
                    BCL.Add(i, l);
                }
                if (l != i)
                    throw new Exception();
            }
        }

        [Benchmark]
        public void RegularSIMD () {
            SIMD.Clear();
            for (int i = 0; i < Size; i++) {
                if (!SIMD.TryGetValue(i, out var l)) {
                    l = ValueFactory(i);
                    SIMD.Add(i, l);
                }
                if (l != i)
                    throw new Exception();
            }
        }

        [Benchmark]
        public void GetOrAddSIMD () {
            var valueFactory = ValueFactory;
            SIMD.Clear();
            for (int i = 0; i < Size; i++) {
                var l = SIMD.GetOrAdd(i, valueFactory);
                if (l != i)
                    throw new Exception();
            }
        }
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class StringAlternateComparer {
        public sealed class OpaqueComparer : IEqualityComparer<string>, IAlternateEqualityComparer<ReadOnlySpan<char>, string> {
            public readonly StringComparer ActualComparer = StringComparer.Ordinal;
            public readonly IAlternateEqualityComparer<ReadOnlySpan<char>, string> ActualAlternateComparer =
                (IAlternateEqualityComparer<ReadOnlySpan<char>, string>)StringComparer.Ordinal;

            public string Create (ReadOnlySpan<char> alternate) =>
                ActualAlternateComparer.Create(alternate);

            public bool Equals (string? x, string? y) {
                return ActualComparer.Equals(x, y);
            }

            public bool Equals (ReadOnlySpan<char> alternate, string other) =>
                ActualAlternateComparer.Equals(alternate, other);

            public int GetHashCode ([DisallowNull] string obj) {
                return ActualComparer.GetHashCode(obj);
            }

            public int GetHashCode (ReadOnlySpan<char> alternate) =>
                ActualAlternateComparer.GetHashCode(alternate);
        }

        const int Size = 40960;

        public Dictionary<string, long> BCL = new (Size, new OpaqueComparer());
        public Dictionary<string, long>.AlternateLookup<ReadOnlySpan<char>> BCLLookup;
        public VectorizedDictionary<string, long> SIMD = new (Size, new OpaqueComparer());
        public VectorizedDictionary<string, long>.AlternateLookup<ReadOnlySpan<char>> SIMDLookup;
        public HashSet<string> Strings = new (Size);

        [GlobalSetup]
        public void Setup () {
            var rng = new Random(1);
            for (int i = 0; i < Size; i++) {
                var s = NextKey(rng);
                while (Strings.Contains(s))
                    s = NextKey(rng);
                Strings.Add(s);
                BCL.Add(s, i);
                SIMD.Add(s, i);
            }
            BCL.TryGetAlternateLookup(out BCLLookup);
            SIMD.TryGetAlternateLookup(out SIMDLookup);
        }

        private string NextKey (Random rng) {
            var l = rng.Next(2, 10);
            var result = new char[l];
            for (int i = 0; i < l; i++)
                result[i] = (char)rng.Next(32, 127);
            return new string(result);
        }

        [Benchmark]
        public void FindExistingSIMD () {
            foreach (var s in Strings) {
                if (!SIMDLookup.TryGetValue(s, out var value))
                    Environment.FailFast("Failed");
            }
        }

        [Benchmark]
        public void FindExistingBCL () {
            foreach (var s in Strings) {
                if (!BCLLookup.TryGetValue(s, out var value))
                    Environment.FailFast("Failed");
            }
        }
    }
}
