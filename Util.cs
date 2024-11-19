using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SimdDictionary {
    public static class CollectionsMarshal_ {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly V GetValueRefOrNullRef<K, V> (this VectorizedDictionary<K, V> self, K key)
            where K : notnull
        {
            ref var pair = ref self.FindKey(key);
            if (Unsafe.IsNullRef(ref pair))
                return ref Unsafe.NullRef<V>();
            return ref pair.Value;
        }        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly V GetValueRefOrAddDefault<K, V> (this VectorizedDictionary<K, V> self, K key)
            where K : notnull
        {
            ref var pair = ref self.TryInsert(key, default!, VectorizedDictionary<K, V>.InsertMode.EnsureUnique, out _);
            if (Unsafe.IsNullRef(ref pair))
                throw new Exception("Corrupted internal state");
            return ref pair.Value;
        }
    }
}
