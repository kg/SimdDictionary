//#define ENABLE_ALTERNATE_LOOKUP

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace SimdDictionary
{
#if ENABLE_ALTERNATE_LOOKUP
    public partial class SimdDictionary<K, V> {
        // Unfinished
        private readonly struct AlternateLookup<TAlternateKey>
            where TAlternateKey : notnull /*, allows ref struct */ {

            public readonly SimdDictionary<K, V> Dictionary;

            internal AlternateLookup (SimdDictionary<K, V> dictionary) {
                Debug.Assert(dictionary != null);
                Debug.Assert(IsCompatibleKey(dictionary));
                Dictionary = dictionary;
            }

            public V this [TAlternateKey key] {
                get {
                    ref V value = ref FindKey(key, out _);
                    if (Unsafe.IsNullRef(ref value))
                        throw new KeyNotFoundException($"Key {key} not found");
                    return value;
                }
                set {
                retry:
                    // FIXME: How does the alternate key get converted to the default key type?
                    var insertResult = TryInsert(key, value, InsertMode.OverwriteValue);
                    switch (insertResult) {
                        case InsertResult.OkAddedNew:
                            Dictionary._Count++;
                            return;
                        case InsertResult.NeedToGrow:
                            Dictionary.Resize(Dictionary._GrowAtCount * 2);
                            goto retry;
                    }
                }
            }

            internal static bool IsCompatibleKey (SimdDictionary<K, V> dictionary) {
                Debug.Assert(dictionary != null);
                // FIXME
                return true;
            }
        }
    }
#endif
}
