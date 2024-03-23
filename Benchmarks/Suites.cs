using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using SimdDictionary;

namespace Benchmarks {
    [MemoryDiagnoser()]
    public class BCLInsertion : Insertion<Dictionary<long, long>> {
    }

    public class BCLLookup : Lookup<Dictionary<long, long>> { 
    }

    [MemoryDiagnoser()]
    public class BCLRemoval : Removal<Dictionary<long, long>> { 
    }

    [MemoryDiagnoser()]
    public class BCLResize : Resize<Dictionary<long, long>> {
    }

    [MemoryDiagnoser()]
    public class BCLCollisions : Collisions<Dictionary<Collider, Collider>> {
    }

    [MemoryDiagnoser()]
    public class BCLIterate : Iterate<Dictionary<long, long>> {
    }

    [MemoryDiagnoser()]
    public class SimdInsertion : Insertion<SimdDictionary<long, long>> {
    }

    public class SimdLookup : Lookup<SimdDictionary<long, long>> { 
    }

    [MemoryDiagnoser()]
    public class SimdRemoval : Removal<SimdDictionary<long, long>> { 
    }

    [MemoryDiagnoser()]
    public class SimdResize : Resize<SimdDictionary<long, long>> {
    }

    [MemoryDiagnoser()]
    public class SimdCollisions : Collisions<SimdDictionary<Collider, Collider>> {
    }

    [MemoryDiagnoser()]
    public class SimdIterate : Iterate<SimdDictionary<long, long>> {
    }
}
