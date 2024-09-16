using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using SimdDictionary;
using TKey = System.String;
using TValue = System.Int64;

namespace Benchmarks {
    [MemoryDiagnoser()]
    public class BCLInsertion : Insertion<Dictionary<TKey, TValue>> {
    }

    public class BCLLookup : Lookup<Dictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class BCLRemoval : Removal<Dictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class BCLResize : Resize<Dictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class BCLCollisions : Collisions<Dictionary<Collider, Collider>> {
    }

    [MemoryDiagnoser()]
    public class BCLIterate : Iterate<Dictionary<TKey, TValue>> {
        protected override IEnumerable<TKey> GetKeys () => Dict.Keys;
        protected override IEnumerable<TValue> GetValues () => Dict.Values;
    }

    [MemoryDiagnoser()]
    public class SimdInsertion : Insertion<SimdDictionary<TKey, TValue>> {
    }

    [DisassemblyDiagnoser(16, BenchmarkDotNet.Diagnosers.DisassemblySyntax.Intel, true, false, false, true, true, false)]
    public class SimdLookup : Lookup<SimdDictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class SimdRemoval : Removal<SimdDictionary<TKey, TValue>> { 
    }

    [MemoryDiagnoser()]
    public class SimdResize : Resize<SimdDictionary<TKey, TValue>> {
    }

    [MemoryDiagnoser()]
    public class SimdCollisions : Collisions<SimdDictionary<Collider, Collider>> {
    }

    [MemoryDiagnoser()]
    public class SimdIterate : Iterate<SimdDictionary<TKey, TValue>> {
        protected override IEnumerable<TKey> GetKeys () => Dict.Keys;
        protected override IEnumerable<TValue> GetValues () => Dict.Values;
    }
}
