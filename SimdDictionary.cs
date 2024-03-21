using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography.X509Certificates;

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

        // Must be the same as KeyBucket.Length
        [InlineArray(14)]
        public struct KeyArray {
            public K Key;
        }

        // Must be the same as KeyBucket.Length
        [InlineArray(14)]
        internal struct ValueArray {
            public V Value;
        }

        internal struct ValueBucket {
            public KeyArray Keys;
            public ValueArray Values;
        }

        public readonly IEqualityComparer<K>? Comparer;
        private int _Count;
        private Vector128<byte>[] _Keys;
        private ValueBucket[] _Values;

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
            Unsafe.SkipInit(out _Keys);
            Unsafe.SkipInit(out _Values);
            Comparer = comparer;
            EnsureCapacity(capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            // HACK: Maintain a decent load factor
            capacity *= 2;

            capacity = ((capacity + BucketSize - 1) / BucketSize) * BucketSize;

            if ((_Keys != null) && (Capacity >= capacity))
                return;

            int nextDoubling = (_Keys == null)
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
            var newKeys = new Vector128<byte>[bucketCount];
            var newValues = new ValueBucket[bucketCount];
            if (_Values != null) {
                int newCount = TryRehash(newKeys, newValues, _Keys, _Values);
                if (newCount != oldCount)
                    return false;
            }
            _Keys = newKeys;
            _Values = newValues;
            return true;
        }

        internal int TryRehash (Vector128<byte>[] newKeys, ValueBucket[] newValues, Vector128<byte>[] oldKeys, ValueBucket[] oldValues) {
            int newCount = 0;

            for (int i = 0, l = oldKeys.Length; i < l; i++) {
                ref var oldBucket = ref oldKeys[i];
                ref var oldValueBucket = ref oldValues[i];
                for (int j = 0, l2 = ItemCount(oldBucket); j < l2; j++) {
                    ref var oldKey = ref oldValueBucket.Keys[j]!;
                    ref var oldValue = ref oldValueBucket.Values[j];
                    var insertResult = TryInsert(newKeys, newValues, ref oldKey, ref oldValue, false);
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
        internal static byte GetHashSuffix (uint hashCode) {
            return unchecked((byte)((hashCode & 0xFF000000) >> 24));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal uint GetHashCode (K key) {
            var comparer = Comparer;
            if (comparer == null)
                return unchecked((uint)key!.GetHashCode());
            else
                return unchecked((uint)comparer.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal Vector128<byte> GetSearchVector (byte suffix) {
            // Fill the first 14 slots with the suffix, and the last two slots with 255 (impossible)
            return Vector128.Create(suffix)
                .WithElement(BucketSize, (byte)255)
                .WithElement(BucketSize + 1, (byte)255);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal ref V FindValue (Vector128<byte>[] keys, ValueBucket[] values, uint firstBucketIndex, byte suffix, K key) {
            var searchVector = GetSearchVector(suffix);
            ref var keyBucket = ref keys[firstBucketIndex];
            ref var valueBucket = ref values[firstBucketIndex];
            var comparer = Comparer;

            if (typeof(K).IsValueType && (comparer == null)) {
                for (uint i = firstBucketIndex, l = (uint)keys.Length; i < l; i++) {
                    var bucket = keyBucket;
                    int count = keyBucket[BucketSize];            
                    
                    if (count > 0)
                    do {
                        var matchVector = Vector128.Equals(bucket, searchVector);
                        uint notEqualsElements = matchVector.ExtractMostSignificantBits();
                        int firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
                        if (firstIndex >= count)
                            break;
                        int matchCount = 32 - (firstIndex + BitOperations.LeadingZeroCount(notEqualsElements));
                        int lastIndex = firstIndex + matchCount;

                        for (int j = firstIndex; j < lastIndex; j++)
                            if (EqualityComparer<K>.Default.Equals(valueBucket.Keys[j], key))
                                return ref valueBucket.Values[j];
                    } while (false);

                    if (!IsCascaded(bucket))
                        return ref Unsafe.NullRef<V>();
                    keyBucket = ref Unsafe.Add(ref keyBucket, 1);
                    valueBucket = ref Unsafe.Add(ref valueBucket, 1);
                }
            } else {
                for (uint i = firstBucketIndex, l = (uint)keys.Length; i < l; i++) {
                    var bucket = keyBucket;
                    int count = keyBucket[BucketSize];       
                    
                    if (count > 0)
                    do {
                        var matchVector = Vector128.Equals(bucket, searchVector);
                        uint notEqualsElements = matchVector.ExtractMostSignificantBits();
                        int firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
                        if (firstIndex >= count)
                            break;
                        int matchCount = 32 - (firstIndex + BitOperations.LeadingZeroCount(notEqualsElements));
                        int lastIndex = firstIndex + matchCount;

                        for (int j = firstIndex; j < lastIndex; j++)
                            if (comparer.Equals(valueBucket.Keys[j], key))
                                return ref valueBucket.Values[j];
                    } while (false);

                    if (!IsCascaded(bucket))
                        return ref Unsafe.NullRef<V>();
                    keyBucket = ref Unsafe.Add(ref keyBucket, 1);
                    valueBucket = ref Unsafe.Add(ref valueBucket, 1);
                }
            }
            return ref Unsafe.NullRef<V>();
        }

        internal InsertFailureReason TryInsert (Vector128<byte>[] keys, ValueBucket[] values, ref K key, ref V value, bool ensureNotPresent) {
            var hashCode = GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var bucketIndex = GetBucketIndex(keys, hashCode);

            if (ensureNotPresent)
                if (!Unsafe.IsNullRef(ref FindValue(keys, values, bucketIndex, suffix, key)))
                    return InsertFailureReason.AlreadyPresent;

            while (bucketIndex < keys.Length) {
                ref var newBucket = ref keys[bucketIndex];
                var index = ItemCount(newBucket);
                if (index >= BucketSize) {
                    newBucket = newBucket.WithElement(BucketSize + 1, (byte)1);
                    bucketIndex++;
                    continue;
                }

                ref var valueBucket = ref values[bucketIndex];
                newBucket = newBucket
                    .WithElement(BucketSize, (byte)(ItemCount(newBucket) + 1))
                    .WithElement(index, suffix);
                valueBucket.Keys[index] = key;
                valueBucket.Values[index] = value;

                return InsertFailureReason.None;
            }

            return InsertFailureReason.NeedToGrow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static ref Vector128<byte> GetBucket (Vector128<byte>[] keys, uint hashCode) =>
            ref keys[hashCode % (uint)keys.Length];

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static uint GetBucketIndex (Vector128<byte>[] keys, uint hashCode) =>
            hashCode % (uint)keys.Length;

        public V this[K key] { 
            get => throw new NotImplementedException(); 
            set => throw new NotImplementedException(); 
        }

        ICollection<K> IDictionary<K, V>.Keys => throw new NotImplementedException();

        ICollection<V> IDictionary<K, V>.Values => throw new NotImplementedException();

        public int Count => _Count;
        public int Capacity => _Keys.Length * BucketSize;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        public void Add (K key, V value) {
            var ok = TryAdd(key, value);
            if (!ok)
                throw new ArgumentException($"Key already exists: {key}");
        }

        public bool TryAdd (K key, V value) {
            EnsureSpaceForNewItem();

        retry:
            var insertResult = TryInsert(_Keys, _Values, ref key, ref value, true);
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
            Array.Clear(_Keys);
            Array.Clear(_Values);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) =>
            TryGetValue(item.Key, out var value) &&
            (value?.Equals(item.Value) == true);

        public bool ContainsKey (K key) {
            var hashCode = GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var keys = _Keys;
            var firstBucketIndex = GetBucketIndex(keys, hashCode);
            return !Unsafe.IsNullRef(ref FindValue(keys, _Values, firstBucketIndex, suffix, key));
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

        public bool TryGetValue (K key, out V value) {
            var hashCode = GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var keys = _Keys;
            var values = _Values;
            var firstBucketIndex = GetBucketIndex(keys, hashCode);
            var searchVector = GetSearchVector(suffix);
            ref var bucket = ref keys[firstBucketIndex];
            ref var valueBucket = ref values[firstBucketIndex];

            ref var result = ref FindValue(keys, values, firstBucketIndex, suffix, key);
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
