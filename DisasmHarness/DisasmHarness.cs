using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using SimdDictionary;

public static class DisasmHarness
{
    public static SimdDictionary<long, long> Dict = new(1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryGetValue () =>
        Dict.TryGetValue(0, out var result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryAdd () =>
        Dict.TryInsert(0, 1, SimdDictionary<long, long>.InsertMode.EnsureUnique) == SimdDictionary<long, long>.InsertResult.OkAddedNew;
}
