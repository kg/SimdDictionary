using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;
using SimdDictionary;
using D = SimdDictionary.SimdDictionary<string, long>;
using K = string;

public static class DisasmHarness
{
    public static D Dict = new(1);
    public static K MissingKey = (typeof(K) == typeof(string)) ? (K)(object)"0" : (K)(object)0L,
        PresentKey = (typeof(K) == typeof(string)) ? (K)(object)"1" : (K)(object)1L;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryFindValue (D dict, int i, K missingKey, K presentKey) =>
        !Unsafe.IsNullRef(in dict.FindValueOrNullRef((i % 2) == 0 ? missingKey : presentKey));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryGetValue (D dict, int i, K missingKey, K presentKey) =>
        dict.TryGetValue((i % 2) == 0 ? missingKey : presentKey, out var result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryAdd (D dict, K presentKey) =>
        dict.TryInsert(presentKey, 1, D.InsertMode.EnsureUnique) == D.InsertResult.OkAddedNew;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryRemove (D dict, int i, K missingKey, K presentKey) =>
        dict.Remove((i % 2) == 0 ? MissingKey : PresentKey);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Clear (D dict) => 
        dict.Clear();
}
