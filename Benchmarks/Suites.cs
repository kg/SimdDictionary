using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SimdDictionary;

namespace Benchmarks {
    public class BCLInsertion : Insertion<Dictionary<int, int>> {
    }

    public class BCLLookup : Lookup<Dictionary<int, int>> { 
    }

    public class SimdInsertion : Insertion<SimdDictionary<int, int>> {
    }

    public class SimdLookup : Lookup<SimdDictionary<int, int>> { 
    }
}
