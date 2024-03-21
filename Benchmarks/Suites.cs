using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SimdDictionary;

namespace Benchmarks {
    public class BCLInsertion : Insertion<Dictionary<long, long>> {
    }

    public class BCLLookup : Lookup<Dictionary<long, long>> { 
    }

    public class BCLRemoval : Removal<Dictionary<long, long>> { 
    }

    public class SimdInsertion : Insertion<SimdDictionary<long, long>> {
    }

    public class SimdLookup : Lookup<SimdDictionary<long, long>> { 
    }

    public class SimdRemoval : Removal<SimdDictionary<long, long>> { 
    }
}
