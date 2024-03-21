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
    public class SimdInsertion : Insertion<SimdDictionary<long, long>> {
    }

    public class SimdLookup : Lookup<SimdDictionary<long, long>> { 
    }

    [MemoryDiagnoser()]
    public class SimdRemoval : Removal<SimdDictionary<long, long>> { 
    }
}
