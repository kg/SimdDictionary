using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using SimdDictionary;
using D = SimdDictionary.SimdDictionary<string, long>;
using K = System.String;

public static class DisasmHarness
{
    public static D Dict = new(1);
    public static K Key = (typeof(K) == typeof(string)) ? (K)(object)"0" : (K)(object)0l;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryGetValue () =>
        Dict.TryGetValue(Key, out var result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryAdd () =>
        Dict.TryInsert(Key, 1, D.InsertMode.EnsureUnique) == D.InsertResult.OkAddedNew;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryRemove () =>
        Dict.Remove(Key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Clear () => 
        Dict.Clear();
}
