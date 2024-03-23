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

        // Ideal bucket size for 8-byte keys is either 14 or 6.
        // Ideal bucket size for 4-byte keys is 12.
        public const uint BucketSize = 14;
        public const int InitialCapacity = (int)BucketSize * 4,
            OversizePercentage = 150;

        // We need to make sure suffixes are never zero, and it's
        //  more likely that a bad hash will collide at the top bit
        //  than at the bottom (i.e. using an int/ptr as its own hash)
        public const uint SuffixSalt = 0b10000000;

        [InlineArray(14)]
        internal struct KeyArray {
            public K Key;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        internal struct Bucket {
            public Vector128<byte> Suffixes;
            public KeyArray Keys;

            internal byte GetSlot (nuint index) =>
                Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref Suffixes), index);

            // Use when modifying one or two slots, to avoid a whole-vector-load-then-store
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void SetSlot (nuint index, byte value) =>
                Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref Suffixes), index) = value;

            public byte Count {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => GetSlot(BucketSize);
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => SetSlot(BucketSize, value);
            }

            public bool IsCascaded {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => GetSlot(BucketSize + 1) != 0;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => SetSlot(BucketSize + 1, value ? (byte)1 : (byte)0);
            }
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
                _valueIndexLocal = (int)BucketSize;
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
                        var suffix = bucket.GetSlot(unchecked((nuint)_valueIndexLocal));
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
                _valueIndexLocal = (int)BucketSize;
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
            if (typeof(K).IsValueType)
                Comparer = comparer;
            else
                Comparer = comparer ?? EqualityComparer<K>.Default;
            EnsureCapacity(capacity);
        }

        public SimdDictionary (SimdDictionary<K, V> source) 
            : this (source.Count, source.Comparer) {
            // FIXME: Optimize this
            foreach (var kvp in source)
                Add(kvp.Key, kvp.Value);
        }

        static int AdjustCapacity (int capacity) {
            if (capacity < 1)
                capacity = 1;
            var bucketCount = ((capacity + BucketSize - 1) / BucketSize);
            // Power-of-two bucket counts enable using & (count - 1) instead of mod count
            var npot = BitOperations.RoundUpToPowerOf2((uint)bucketCount);
            return (int)(npot * BucketSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            capacity = AdjustCapacity(capacity * OversizePercentage / 100);

            if ((_Buckets != null) && (Capacity >= capacity))
                return;

            int nextIncrement = (_Buckets == null)
                ? capacity
                : Capacity * 2;

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

            for (uint i = 0; i < oldBuckets.Length; i++) {
                ref var oldBucket = ref oldBuckets[i];
                var oldBucketSuffixes = oldBucket.Suffixes;
                var baseIndex = i * BucketSize;
                for (int j = 0; j < BucketSize; j++) {
                    if (oldBucketSuffixes[j] == 0)
                        continue;

                    uint k = baseIndex + unchecked((uint)j);
                    var insertResult = TryInsert(newBuckets, newValues, oldBucket.Keys[j], ref oldValues[k], false);
                    if (insertResult != InsertFailureReason.None)
                        return newCount;
                    newCount++;
                }
            }

            return newCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static byte GetHashSuffix (uint hashCode) =>
            // The bottom bits of the hash form the bucket index, so we
            //  use the top bits of the hash as a suffix
            unchecked((byte)((hashCode >> 24) | SuffixSalt));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal uint GetHashCode (K key) {
            var comparer = Comparer;
            if (comparer == null)
                return unchecked((uint)key!.GetHashCode());
            else
                return unchecked((uint)comparer.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal ref V FindValue (Bucket[] _buckets, V[] values, K key) {
            Vector128<byte> searchVector;
            var comparer = Comparer;
            var bucketCount = unchecked((uint)_buckets.Length);

            if (typeof(K).IsValueType && (comparer == null)) {
                var hashCode = unchecked((uint)key!.GetHashCode());
                var suffix = unchecked((byte)((hashCode >> 24) | SuffixSalt));
                var firstBucketIndex = unchecked(hashCode & (bucketCount - 1));
                ref var searchBucket = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), firstBucketIndex);
                // An ideal searchVector would zero the last two slots, but it's faster to allow
                //  occasional false positives than it is to zero the vector slots :/
                searchVector = Vector128.Create(suffix);
                for (nuint i = firstBucketIndex; i < bucketCount; i++) {
                    var count = searchBucket.Count;
                    
                    var matchVector = Vector128.Equals(searchBucket.Suffixes, searchVector);
                    // On average this improves over iterating from 0-count, but only a little bit
                    uint notEqualsElements = matchVector.ExtractMostSignificantBits();
                    // the first index is almost always the correct one
                    nint firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
                    ref var firstSearchKey = ref Unsafe.Add(ref searchBucket.Keys.Key, firstIndex);

                    for (nuint j = unchecked((nuint)firstIndex); j < count; j++) {
                        if (EqualityComparer<K>.Default.Equals(firstSearchKey, key)) {
                            var valueIndex = j + (i * BucketSize);
                            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), valueIndex);
                        }
                        firstSearchKey = ref Unsafe.Add(ref firstSearchKey, 1);
                    }

                    if (!searchBucket.IsCascaded)
                        return ref Unsafe.NullRef<V>();

                    searchBucket = ref Unsafe.Add(ref searchBucket, 1);
                }
            } else {
                var hashCode = unchecked((uint)comparer.GetHashCode(key!));
                var suffix = unchecked((byte)((hashCode >> 24) | SuffixSalt));
                var firstBucketIndex = unchecked(hashCode & (bucketCount - 1));
                ref var searchBucket = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), firstBucketIndex);
                // An ideal searchVector would zero the last two slots, but it's faster to allow
                //  occasional false positives than it is to zero the vector slots :/
                searchVector = Vector128.Create(suffix);
                for (nuint i = firstBucketIndex; i < bucketCount; i++) {
                    var count = searchBucket.Count;
                    
                    var matchVector = Vector128.Equals(searchBucket.Suffixes, searchVector);
                    // On average this improves over iterating from 0-count, but only a little bit
                    uint notEqualsElements = matchVector.ExtractMostSignificantBits();
                    // the first index is almost always the correct one
                    nint firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
                    ref var firstSearchKey = ref Unsafe.Add(ref searchBucket.Keys.Key, firstIndex);

                    for (nuint j = unchecked((nuint)firstIndex); j < count; j++) {
                        if (comparer!.Equals(firstSearchKey, key)) {
                            var valueIndex = j + (i * BucketSize);
                            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), valueIndex);
                        }
                        firstSearchKey = ref Unsafe.Add(ref firstSearchKey, 1);
                    }

                    if (!searchBucket.IsCascaded)
                        return ref Unsafe.NullRef<V>();

                    searchBucket = ref Unsafe.Add(ref searchBucket, 1);
                }
            }
            return ref Unsafe.NullRef<V>();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal InsertFailureReason TryInsert (Bucket[] buckets, V[] values, K key, ref V value, bool ensureNotPresent) {
            if (ensureNotPresent)
                if (!Unsafe.IsNullRef(ref FindValue(buckets, values, key)))
                    return InsertFailureReason.AlreadyPresent;

            var hashCode = GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var bucketIndex = hashCode & (buckets.Length - 1);

            while (bucketIndex < buckets.Length) {
                ref var newBucket = ref buckets[bucketIndex];
                var localIndex = newBucket.Count;
                if (localIndex >= BucketSize) {
                    newBucket.IsCascaded = true;
                    bucketIndex++;
                    continue;
                }

                newBucket.Count = unchecked((byte)(localIndex + 1));
                newBucket.SetSlot(localIndex, suffix);

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
        public int Capacity => _Buckets.Length * (int)BucketSize;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        public void Add (K key, V value) {
            var ok = TryAdd(key, value);
            if (!ok)
                throw new ArgumentException($"Key already exists: {key}");
        }

        public bool TryAdd (K key, V value) {
            EnsureSpaceForNewItem();

        retry:
            var insertResult = TryInsert(_Buckets, _Values, key, ref value, true);
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
            return !Unsafe.IsNullRef(ref FindValue(_Buckets, _Values, key));
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
            ref var oldValueSlot = ref FindValue(_Buckets, _Values, key);
            if (Unsafe.IsNullRef(ref oldValueSlot))
                return false;

            var valueOffsetBytes = Unsafe.ByteOffset(ref _Values[0], ref oldValueSlot);
            var index = unchecked((uint)(valueOffsetBytes / Unsafe.SizeOf<V>()));
            var bucketIndex = index / BucketSize;
            var slotInBucket = index % BucketSize;

            ref var bucket = ref _Buckets[bucketIndex];
            var bucketCount = bucket.Count;
            ref var oldKeySlot = ref Unsafe.Add(ref bucket.Keys.Key, slotInBucket);
            var newCount = bucketCount - 1;
            var newIndex = (bucketIndex * BucketSize) + newCount;
            ref var newKeySlot = ref Unsafe.Add(ref bucket.Keys.Key, newCount);
            ref var newValueSlot = ref _Values[newIndex];
            oldKeySlot = newKeySlot;
            oldValueSlot = newValueSlot;
            newKeySlot = default;
            newValueSlot = default;
            bucket.SetSlot(unchecked((nuint)slotInBucket), bucket.GetSlot(unchecked((nuint)newCount)));
            bucket.SetSlot(unchecked((nuint)newCount), 0);
            bucket.Count = unchecked((byte)newCount);

            _Count--;
            return true;
        }

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool TryGetValue (K key, out V value) {
            ref var result = ref FindValue(_Buckets, _Values, key);
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
