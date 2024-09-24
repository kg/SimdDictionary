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
    public static bool TryGetValue (int i) =>
        Dict.TryGetValue((i % 2) == 0 ? MissingKey : PresentKey, out var result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryAdd () =>
        Dict.TryInsert(PresentKey, 1, D.InsertMode.EnsureUnique) == D.InsertResult.OkAddedNew;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryRemove (int i) =>
        Dict.Remove((i % 2) == 0 ? MissingKey : PresentKey);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Clear () => 
        Dict.Clear();

    public static IEqualityComparer<byte> Comparer = EqualityComparer<byte>.Default;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte VectorLicm (byte scalar) {
        int result = 0;
        for (int i = 0; i < 256; i++) {
            var mask = Vector128.Equals(Vector128.Create(scalar), Vector128.Create(unchecked((byte)i)));
            if (!Comparer.Equals(mask.ToScalar(), 0))
                result++;
        }

        return scalar;
    }
}
