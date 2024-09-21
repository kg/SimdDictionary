using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using SimdDictionary;
using Windows.Foundation.Metadata;
using D = SimdDictionary.SimdDictionary<string, long>;
using K = string;

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
