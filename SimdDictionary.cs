using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SimdDictionary {
    public class SimdDictionary<K, V> : IDictionary<K, V>, ICloneable {
        internal enum InsertFailureReason {
            None,
            AlreadyPresent = 1,
            NeedToGrow = 2,
        }

        public const int InitialCapacity = BucketSize * 4;
        public const int BucketSize = 14;

        [InlineArray(14)]
        internal struct KeyArray {
            public K Key;
        }

        internal struct Bucket {
            public Vector128<byte> Suffixes;
            public KeyArray Keys;
        }

        public struct Enumerator : IEnumerator<KeyValuePair<K, V>> {
            private int _bucketIndex, _valueIndex, _valueIndexLocal;
            private Bucket[] _buckets;
            private V[] _values;
            public KeyValuePair<K, V> Current {
                get {
                    if (_valueIndex < 0)
                        throw new InvalidOperationException("No value");
                    return new KeyValuePair<K, V>(_buckets[_bucketIndex].Keys[_valueIndexLocal], _values[_valueIndex]);
                }
            }
            object IEnumerator.Current => Current;

            public Enumerator (SimdDictionary<K, V> dictionary) {
                _bucketIndex = -1;
                _valueIndex = -1;
                _valueIndexLocal = BucketSize;
                _buckets = dictionary._Buckets;
                _values = dictionary._Values;
            }

            public void Dispose () {
            }

            public bool MoveNext () {
                _valueIndex++;
                _valueIndexLocal++;

                while (_bucketIndex < _buckets.Length) {
                    if (_valueIndexLocal >= BucketSize) {
                        _valueIndexLocal = 0;
                        _bucketIndex++;
                        if (_bucketIndex >= _buckets.Length)
                            return false;
                    }

                    ref var bucket = ref _buckets[_bucketIndex];
                    // We iterate over the whole bucket including empty slots to keep the indices in sync
                    while (_valueIndexLocal < BucketSize) {
                        var suffix = bucket.Suffixes[_valueIndexLocal];
                        if (suffix != 0)
                            return true;
                        _valueIndexLocal++;
                        _valueIndex++;
                    }
                }

                return false;
            }

            public void Reset () {
                _bucketIndex = -1;
                _valueIndex = -1;
                _valueIndexLocal = BucketSize;
            }
        }

        public readonly IEqualityComparer<K>? Comparer;
        private int _Count;
        private Bucket[] _Buckets;
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
            Unsafe.SkipInit(out _Values);
            Comparer = comparer;
            EnsureCapacity(capacity);
        }

        public SimdDictionary (SimdDictionary<K, V> source) 
            : this (source.Count, source.Comparer) {
            // FIXME: Optimize this
            foreach (var kvp in source)
                Add(kvp.Key, kvp.Value);
        }

        static int RoundedCapacity (int capacity) =>
            ((capacity + BucketSize - 1) / BucketSize) * BucketSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            capacity = RoundedCapacity(capacity * 150 / 100);

            if ((_Buckets != null) && (Capacity >= capacity))
                return;

            int nextIncrement = (_Buckets == null)
                ? capacity
                : RoundedCapacity(Capacity * 150 / 100);

            if (!TryResize(Math.Max(capacity, nextIncrement)))
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
            var newBuckets = new Bucket[bucketCount];
            var newValues = new V[capacity];
            if (_Buckets != null) {
                int newCount = TryRehash(newBuckets, newValues, _Buckets, _Values);
                if (newCount != oldCount)
                    return false;
            }
            _Buckets = newBuckets;
            _Values = newValues;
            return true;
        }

        internal int TryRehash (Bucket[] newBuckets, V[] newValues, Bucket[] oldBuckets, V[] oldValues) {
            int newCount = 0;

            for (int i = 0; i < oldBuckets.Length; i++) {
                ref var oldBucket = ref oldBuckets[i];
                var baseIndex = i * BucketSize;
                for (int j = 0; j < BucketSize; j++) {
                    if (oldBucket.Suffixes[j] == 0)
                        continue;

                    int k = baseIndex + j;
                    var insertResult = TryInsert(newBuckets, newValues, ref oldBucket.Keys[j], ref oldValues[k], false);
                    if (insertResult != InsertFailureReason.None)
                        return newCount;
                    newCount++;
                }
            }

            return newCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static byte ItemCount (Vector128<byte> suffixes) =>
            suffixes[BucketSize];

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static bool IsCascaded (Vector128<byte> suffixes) =>
            suffixes[BucketSize + 1] != 0;


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
        internal ref V FindValue (Bucket[] _buckets, V[] values, ref K key) {
            var comparer = Comparer;

            if (typeof(K).IsValueType && (comparer == null)) {
                var bucketCount = unchecked((uint)_buckets.Length);
                var hashCode = unchecked((uint)key!.GetHashCode());
                var suffix = unchecked((byte)((hashCode >> 24) | 1));
                var firstBucketIndex = unchecked(hashCode % bucketCount);
                ref var searchBucket = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), firstBucketIndex);
                // An ideal searchVector would zero the last two slots, but it's faster to allow
                //  occasional false positives than it is to zero the vector slots :/
                Vector128<byte> searchVector = Vector128.Create(suffix),
                    suffixes;
                for (int i = (int)firstBucketIndex; i < bucketCount; i++) {
                    suffixes = searchBucket.Suffixes;
                    int count = suffixes[BucketSize];
                    
                    var matchVector = Vector128.Equals(suffixes, searchVector);
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
                        ref var firstSearchKey = ref Unsafe.Add(ref searchBucket.Keys.Key, firstIndex);

                        for (int j = firstIndex; j < count; j++) {
                            if (EqualityComparer<K>.Default.Equals(firstSearchKey, key))
                                return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), (j + (i * BucketSize)));
                            firstSearchKey = ref Unsafe.Add(ref firstSearchKey, 1);
                        }
                    // }

                    if (!IsCascaded(suffixes))
                        return ref Unsafe.NullRef<V>();

                    searchBucket = ref Unsafe.Add(ref searchBucket, 1);
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal InsertFailureReason TryInsert (Bucket[] buckets, V[] values, ref K key, ref V value, bool ensureNotPresent) {
            if (ensureNotPresent)
                if (!Unsafe.IsNullRef(ref FindValue(buckets, values, ref key)))
                    return InsertFailureReason.AlreadyPresent;

            var hashCode = GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var bucketIndex = hashCode % buckets.Length;

            while (bucketIndex < buckets.Length) {
                ref var newBucket = ref buckets[bucketIndex];
                var localIndex = ItemCount(newBucket.Suffixes);
                if (localIndex >= BucketSize) {
                    newBucket.Suffixes = newBucket.Suffixes.WithElement(BucketSize + 1, (byte)1);
                    bucketIndex++;
                    continue;
                }

                ref var valueBucket = ref values[bucketIndex];
                newBucket.Suffixes = newBucket.Suffixes
                    .WithElement(BucketSize, (byte)(ItemCount(newBucket.Suffixes) + 1))
                    .WithElement(localIndex, suffix);

                var index = (bucketIndex * BucketSize) + localIndex;
                newBucket.Keys[localIndex] = key;
                values[index] = value;

                return InsertFailureReason.None;
            }

            return InsertFailureReason.NeedToGrow;
        }

        public V this[K key] { 
            get {
                if (!TryGetValue(key, out var result))
                    throw new KeyNotFoundException();
                else
                    return result;
            }
            // FIXME: In-place modification
            set => Add(key, value);
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
            var insertResult = TryInsert(_Buckets, _Values, ref key, ref value, true);
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
            Array.Clear(_Values);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) =>
            TryGetValue(item.Key, out var value) &&
            (value?.Equals(item.Value) == true);

        public bool ContainsKey (K key) {
            return !Unsafe.IsNullRef(ref FindValue(_Buckets, _Values, ref key));
        }

        void ICollection<KeyValuePair<K, V>>.CopyTo (KeyValuePair<K, V>[] array, int arrayIndex) {
            throw new NotImplementedException();
        }

        public Enumerator GetEnumerator () =>
            new Enumerator(this);

        IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator () =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator () =>
            GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool Remove (K key) {
            ref var oldValueSlot = ref FindValue(_Buckets, _Values, ref key);
            if (Unsafe.IsNullRef(ref oldValueSlot))
                return false;

            var valueOffsetBytes = Unsafe.ByteOffset(ref _Values[0], ref oldValueSlot);
            var index = (int)(valueOffsetBytes / Unsafe.SizeOf<V>());
            var bucketIndex = index / BucketSize;
            var slotInBucket = index % BucketSize;

            ref var bucket = ref _Buckets[bucketIndex];
            var bucketCount = ItemCount(bucket.Suffixes);
            ref var oldKeySlot = ref bucket.Keys[slotInBucket];
            var newCount = bucketCount - 1;
            var newIndex = (bucketIndex * BucketSize) + newCount;
            ref var newKeySlot = ref bucket.Keys[newCount];
            ref var newValueSlot = ref _Values[newIndex];
            oldKeySlot = newKeySlot;
            oldValueSlot = newValueSlot;
            newKeySlot = default;
            newValueSlot = default;
            bucket.Suffixes = bucket.Suffixes.WithElement((int)slotInBucket, (byte)bucket.Suffixes[newCount])
                .WithElement((int)(bucketCount - 1), (byte)0)
                .WithElement((int)BucketSize, (byte)newCount);

            _Count--;
            return true;
        }

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool TryGetValue (K key, out V value) {
            ref var result = ref FindValue(_Buckets, _Values, ref key);
            if (Unsafe.IsNullRef(ref result)) {
                value = default!;
                return false;
            } else {
                value = result;
                return true;
            }
        }

        public object Clone () =>
            new SimdDictionary<K, V>(this);
    }
}
