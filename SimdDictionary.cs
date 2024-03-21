using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SimdDictionary {
    public class SimdDictionary<K, V> : IDictionary<K, V> {
        internal enum InsertFailureReason {
            None,
            AlreadyPresent = 1,
            NeedToGrow = 2,
        }

        public const int InitialCapacity = BucketSize * 4;
        public const int BucketSize = 14;

        public struct Enumerator : IEnumerator<KeyValuePair<K, V>> {
            public KeyValuePair<K, V> Current => throw new NotImplementedException();
            object IEnumerator.Current => Current;

            public void Dispose () {
            }

            public bool MoveNext () {
                throw new NotImplementedException();
            }

            public void Reset () {
                throw new NotImplementedException();
            }
        }

        public readonly IEqualityComparer<K>? Comparer;
        private int _Count;
        private Vector128<byte>[] _Buckets;
        private K[] _Keys;
        private V[] _Values;

        public SimdDictionary () 
            : this (InitialCapacity, typeof(K).IsValueType ? null : EqualityComparer<K>.Default) {
        }

        public SimdDictionary (int capacity)
            : this (capacity, typeof(K).IsValueType ? null : EqualityComparer<K>.Default) {
        }

        public SimdDictionary (IEqualityComparer<K>? comparer)
            : this (InitialCapacity, comparer) {
        }

        public SimdDictionary (int capacity, IEqualityComparer<K>? comparer) {
            Unsafe.SkipInit(out _Buckets);
            Unsafe.SkipInit(out _Keys);
            Unsafe.SkipInit(out _Values);
            Comparer = comparer;
            EnsureCapacity(capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            capacity = ((capacity + BucketSize - 1) / BucketSize) * BucketSize;

            if ((_Buckets != null) && (Capacity >= capacity))
                return;

            int nextDoubling = (_Buckets == null)
                ? capacity
                : Capacity * 2;

            if (!TryResize(Math.Max(capacity, nextDoubling)))
                throw new Exception("Internal error: Failed to resize");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureSpaceForNewItem () {
            // FIXME: Maintain good load factor
            EnsureCapacity(_Count + 1);
        }

        internal bool TryResize (int capacity) {
            var oldCount = _Count;
            var bucketCount = capacity / BucketSize;
            var newBuckets = new Vector128<byte>[bucketCount];
            var newKeys = new K[capacity];
            var newValues = new V[capacity];
            if (_Buckets != null) {
                int newCount = TryRehash(newBuckets, newKeys, newValues, _Buckets, _Keys, _Values, oldCount);
                if (newCount != oldCount)
                    return false;
            }
            _Buckets = newBuckets;
            _Keys = newKeys;
            _Values = newValues;
            return true;
        }

        internal int TryRehash (Vector128<byte>[] newBuckets, K[] newKeys, V[] newValues, Vector128<byte>[] oldBuckets, K[] oldKeys, V[] oldValues, int oldCount) {
            int newCount = 0;

            for (int i = 0; i < oldBuckets.Length; i++) {
                var oldBucket = oldBuckets[i];
                var baseIndex = i * BucketSize;
                for (int j = 0; j < BucketSize; j++) {
                    if (oldBucket[j] == 0)
                        continue;

                    int k = baseIndex + j;
                    var insertResult = TryInsert(newBuckets, newKeys, newValues, ref oldKeys[k], ref oldValues[k], false);
                    if (insertResult != InsertFailureReason.None)
                        return newCount;
                    newCount++;
                }
            }

            return newCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static byte ItemCount (Vector128<byte> bucket) =>
            bucket[BucketSize];

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static bool IsCascaded (Vector128<byte> bucket) =>
            bucket[BucketSize + 1] != 0;


        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static byte GetHashSuffix (uint hashCode) =>
            unchecked((byte)((hashCode >> 24) | 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal uint GetHashCode (K key) {
            var comparer = Comparer;
            if (comparer == null)
                return unchecked((uint)key!.GetHashCode());
            else
                return unchecked((uint)comparer.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal ref V FindValue (Vector128<byte>[] _buckets, K[] _keys, V[] values, K key) {
            var comparer = Comparer;

            if (typeof(K).IsValueType && (comparer == null)) {
                var bucketCount = unchecked((uint)_buckets.Length);
                var hashCode = unchecked((uint)key!.GetHashCode());
                var suffix = unchecked((byte)((hashCode >> 24) | 1));
                var firstBucketIndex = unchecked(hashCode % bucketCount);
                ref var searchBucket = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), firstBucketIndex);
                ref var searchKey = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_keys), firstBucketIndex * BucketSize);
                // An ideal searchVector would zero the last two slots, but it's faster to allow
                //  occasional false positives than it is to zero the vector slots :/
                Vector128<byte> searchVector = Vector128.Create(suffix),
                    bucket;
                for (int i = (int)firstBucketIndex; i < bucketCount; i++) {
                    bucket = searchBucket;
                    int count = bucket[BucketSize];
                    
                    var matchVector = Vector128.Equals(bucket, searchVector);
                    // On average this improves over iterating from 0-count, but only a little bit
                    uint notEqualsElements = matchVector.ExtractMostSignificantBits();
                    int firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
                    // this if doesn't seem to help performance, and the for-loop termination does the same thing
                    // if (firstIndex < count) {
                        // On average this is more expensive than just iterating from firstIndex to count...
                        //  calculating the last index is just that expensive
                        /*
                        int matchCount = 32 - (firstIndex + BitOperations.LeadingZeroCount(notEqualsElements));
                        int lastIndex = firstIndex + matchCount;
                        if (lastIndex > count)
                            lastIndex = count;
                        */
                        ref var firstSearchKey = ref Unsafe.Add(ref searchKey, firstIndex);

                        for (int j = firstIndex; j < count; j++) {
                            if (EqualityComparer<K>.Default.Equals(firstSearchKey, key))
                                return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), (j + (i * BucketSize)));
                            firstSearchKey = ref Unsafe.Add(ref firstSearchKey, 1);
                        }
                    // }

                    if (!IsCascaded(bucket))
                        return ref Unsafe.NullRef<V>();

                    searchBucket = ref Unsafe.Add(ref searchBucket, 1);
                    searchKey = ref Unsafe.Add(ref searchKey, BucketSize);
                }
            } else {
                throw new NotImplementedException();
                /*
                for (uint i = firstBucketIndex, l = (uint)keys.Length; i < l; i++) {
                    var bucket = buckets[i];
                    int count = bucket[BucketSize];
                    
                    if (count > 0)
                    do {
                        var matchVector = Vector128.Equals(bucket, searchVector);
                        uint notEqualsElements = matchVector.ExtractMostSignificantBits();
                        int firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
                        if (firstIndex >= count)
                            break;
                        int matchCount = 32 - (firstIndex + BitOperations.LeadingZeroCount(notEqualsElements));
                        int lastIndex = firstIndex + matchCount;
                        uint baseIndex = (i * BucketSize);

                        for (int j = firstIndex; j < lastIndex; j++)
                            if (comparer.Equals(keys[j + baseIndex], key))
                                return ref values[j + baseIndex];
                    } while (false);

                    if (!IsCascaded(bucket))
                        return ref Unsafe.NullRef<V>();
                }
                */
            }
            return ref Unsafe.NullRef<V>();
        }

        internal InsertFailureReason TryInsert (Vector128<byte>[] buckets, K[] keys, V[] values, ref K key, ref V value, bool ensureNotPresent) {
            if (ensureNotPresent)
                if (!Unsafe.IsNullRef(ref FindValue(buckets, keys, values, key)))
                    return InsertFailureReason.AlreadyPresent;

            var hashCode = GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var bucketIndex = GetBucketIndex(buckets, hashCode);

            while (bucketIndex < buckets.Length) {
                ref var newBucket = ref buckets[bucketIndex];
                var localIndex = ItemCount(newBucket);
                if (localIndex >= BucketSize) {
                    newBucket = newBucket.WithElement(BucketSize + 1, (byte)1);
                    bucketIndex++;
                    continue;
                }

                ref var valueBucket = ref values[bucketIndex];
                newBucket = newBucket
                    .WithElement(BucketSize, (byte)(ItemCount(newBucket) + 1))
                    .WithElement(localIndex, suffix);

                var index = (bucketIndex * BucketSize) + localIndex;
                keys[index] = key;
                values[index] = value;

                return InsertFailureReason.None;
            }

            return InsertFailureReason.NeedToGrow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static uint GetBucketIndex (Vector128<byte>[] buckets, uint hashCode) =>
            hashCode % (uint)buckets.Length;

        public V this[K key] { 
            get => throw new NotImplementedException(); 
            set => throw new NotImplementedException(); 
        }

        ICollection<K> IDictionary<K, V>.Keys => throw new NotImplementedException();

        ICollection<V> IDictionary<K, V>.Values => throw new NotImplementedException();

        public int Count => _Count;
        public int Capacity => _Buckets.Length * BucketSize;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        public void Add (K key, V value) {
            var ok = TryAdd(key, value);
            if (!ok)
                throw new ArgumentException($"Key already exists: {key}");
        }

        public bool TryAdd (K key, V value) {
            EnsureSpaceForNewItem();

        retry:
            var insertResult = TryInsert(_Buckets, _Keys, _Values, ref key, ref value, true);
            switch (insertResult) {
                case InsertFailureReason.None:
                    _Count++;
                    return true;
                case InsertFailureReason.AlreadyPresent:
                    return false;
                case InsertFailureReason.NeedToGrow:
                    TryResize(Capacity * 2);
                    goto retry;
                default:
                    return false;
            }
        }

        void ICollection<KeyValuePair<K, V>>.Add (KeyValuePair<K, V> item) =>
            Add(item.Key, item.Value);

        public void Clear () {
            _Count = 0;
            Array.Clear(_Buckets);
            Array.Clear(_Keys);
            Array.Clear(_Values);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) =>
            TryGetValue(item.Key, out var value) &&
            (value?.Equals(item.Value) == true);

        public bool ContainsKey (K key) {
            return !Unsafe.IsNullRef(ref FindValue(_Buckets, _Keys, _Values, key));
        }

        void ICollection<KeyValuePair<K, V>>.CopyTo (KeyValuePair<K, V>[] array, int arrayIndex) {
            throw new NotImplementedException();
        }

        public Enumerator GetEnumerator () =>
            new Enumerator();

        IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator () =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator () =>
            GetEnumerator();

        public bool Remove (K key) =>
            throw new NotImplementedException();

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool TryGetValue (K key, out V value) {
            ref var result = ref FindValue(_Buckets, _Keys, _Values, key);
            if (Unsafe.IsNullRef(ref result)) {
                value = default!;
                return false;
            } else {
                value = result;
                return true;
            }
        }
    }
}
