using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimdDictionary {
    public static class CollectionsMarshal_ {
        public static ref readonly V GetValueRefOrNullRef<K, V> (this VectorizedDictionary<K, V> self, K key)
            where K : notnull
        {
            ref var pair = ref self.FindKey(key);
            if (Unsafe.IsNullRef(ref pair))
                return ref Unsafe.NullRef<V>();
            return ref pair.Value;
        }
        
        public static ref readonly V GetValueRefOrAddDefault<K, V> (this VectorizedDictionary<K, V> self, K key, V defaultValue = default!)
            where K : notnull
        {
retry:
            ref var pair = ref self.TryInsert(key, defaultValue, VectorizedDictionary<K, V>.InsertMode.EnsureUnique, out var result);
            if (result == VectorizedDictionary<K, V>.InsertResult.NeedToGrow) {
                self.EnsureCapacity(self.Count + 1);
                goto retry;
            }
            if (Unsafe.IsNullRef(ref pair))
                throw new Exception("Corrupted internal state");
            return ref pair.Value;
        }
    }
}
