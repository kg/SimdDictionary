using System;
using System.Collections.Generic;
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
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class BCLRemoval : Removal<Dictionary<TKey, TValue>> { 
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
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    [MemoryDiagnoser()]
    public class SimdRemoval : Removal<SimdDictionary<TKey, TValue>> { 
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
}
