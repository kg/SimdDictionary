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

        public const int InitialCapacity = SearchBucket.Length * 4;

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

        internal struct SearchBucket {
            // Must be <= (Data.Count - 2)
            public const int Length = 14;

            public Vector128<byte> HashSuffixes;

            public byte Count {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => HashSuffixes[Length];
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => HashSuffixes = HashSuffixes.WithElement(Length, value);
            }
            public bool Cascaded {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => HashSuffixes[Length + 1] != 0;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => HashSuffixes = HashSuffixes.WithElement(Length + 1, value ? (byte)1 : (byte)0);
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

        public readonly IEqualityComparer<K> Comparer;
        private int _Count;
        private SearchBucket[] _Keys;
        private ValueBucket[] _Values;

        public SimdDictionary () 
            : this (InitialCapacity, EqualityComparer<K>.Default) {
        }

        public SimdDictionary (int capacity)
            : this (capacity, EqualityComparer<K>.Default) {
        }

        public SimdDictionary (IEqualityComparer<K> comparer)
            : this (InitialCapacity, comparer) {
        }

        public SimdDictionary (int capacity, IEqualityComparer<K> comparer) {
            Unsafe.SkipInit(out _Keys);
            Unsafe.SkipInit(out _Values);
            Comparer = comparer;
            EnsureCapacity(capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            // HACK: Maintain a decent load factor
            capacity *= 2;

            capacity = ((capacity + SearchBucket.Length - 1) / SearchBucket.Length) * SearchBucket.Length;

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
            var bucketCount = capacity / SearchBucket.Length;
            var newKeys = new SearchBucket[bucketCount];
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

        internal int TryRehash (SearchBucket[] newKeys, ValueBucket[] newValues, SearchBucket[] oldKeys, ValueBucket[] oldValues) {
            int newCount = 0;

            for (int i = 0, l = oldKeys.Length; i < l; i++) {
                ref var oldBucket = ref oldKeys[i];
                ref var oldValueBucket = ref oldValues[i];
                for (int j = 0, l2 = oldBucket.Count; j < l2; j++) {
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetHashSuffix (uint hashCode) {
            return unchecked((byte)((hashCode & 0xFF000000) >> 24));
        }

        internal int FindInBucket (Vector128<byte> hashSuffixes, ref KeyArray keys, Vector128<byte> searchVector, ref K key) {
            var matchVector = Vector128.Equals(hashSuffixes, searchVector);
            uint notEqualsElements = matchVector.ExtractMostSignificantBits();
            int firstIndex = BitOperations.TrailingZeroCount(notEqualsElements);
            ref K firstKey = ref keys[firstIndex];
            for (int i = firstIndex, c = hashSuffixes[SearchBucket.Length]; i < c; i++) {
                if (Comparer.Equals(firstKey, key))
                    return i;
                firstKey = ref Unsafe.Add(ref firstKey, 1);
            }
            return -1;
        }

        internal bool FindExisting (SearchBucket[] keys, ValueBucket[] values, uint firstBucketIndex, byte suffix, ref K key) {
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref keys[firstBucketIndex];
            ref var valueBucket = ref values[firstBucketIndex];
            for (uint i = firstBucketIndex; i < keys.Length; i++) {
                var index = FindInBucket(bucket.HashSuffixes, ref valueBucket.Keys, searchVector, ref key);
                if (index >= 0)
                    return true;
                if (!bucket.Cascaded)
                    return false;
                bucket = ref Unsafe.Add(ref bucket, 1);
                valueBucket = ref Unsafe.Add(ref valueBucket, 1);
            }
            return false;
        }

        internal InsertFailureReason TryInsert (SearchBucket[] keys, ValueBucket[] values, ref K key, ref V value, bool ensureNotPresent) {
            var hashCode = unchecked((uint)Comparer.GetHashCode(key!));
            var suffix = GetHashSuffix(hashCode);
            var bucketIndex = GetBucketIndex(keys, hashCode);

            while (bucketIndex < keys.Length) {
                if (ensureNotPresent) {
                    if (FindExisting(keys, values, bucketIndex, suffix, ref key))
                        return InsertFailureReason.AlreadyPresent;
                }

                ref var newBucket = ref keys[bucketIndex];
                var index = newBucket.Count;
                if (index >= SearchBucket.Length) {
                    newBucket.Cascaded = true;
                    bucketIndex++;
                    continue;
                }

                ref var valueBucket = ref values[bucketIndex];
                newBucket.Count++;
                newBucket.HashSuffixes = newBucket.HashSuffixes.WithElement(index, suffix);
                valueBucket.Keys[index] = key;
                valueBucket.Values[index] = value;

                return InsertFailureReason.None;
            }

            return InsertFailureReason.NeedToGrow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref SearchBucket GetBucket (SearchBucket[] keys, uint hashCode) =>
            ref keys[hashCode % (uint)keys.Length];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint GetBucketIndex (SearchBucket[] keys, uint hashCode) =>
            hashCode % (uint)keys.Length;

        public V this[K key] { 
            get => throw new NotImplementedException(); 
            set => throw new NotImplementedException(); 
        }

        ICollection<K> IDictionary<K, V>.Keys => throw new NotImplementedException();

        ICollection<V> IDictionary<K, V>.Values => throw new NotImplementedException();

        public int Count => _Count;
        public int Capacity => _Keys.Length * SearchBucket.Length;

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
            var hashCode = unchecked((uint)Comparer.GetHashCode(key!));
            var suffix = GetHashSuffix(hashCode);
            var keys = _Keys;
            var firstBucketIndex = GetBucketIndex(keys, hashCode);
            return FindExisting(keys, _Values, firstBucketIndex, suffix, ref key);
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
            var hashCode = unchecked((uint)Comparer.GetHashCode(key!));
            var suffix = GetHashSuffix(hashCode);
            var keys = _Keys;
            var values = _Values;
            var firstBucketIndex = GetBucketIndex(keys, hashCode);
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref keys[firstBucketIndex];
            ref var valueBucket = ref values[firstBucketIndex];

            for (uint i = firstBucketIndex; i < keys.Length; i++) {
                var index = FindInBucket(bucket.HashSuffixes, ref valueBucket.Keys, searchVector, ref key);
                if (index >= 0) {
                    value = valueBucket.Values[index];
                    return true;
                }
                if (!bucket.Cascaded)
                    break;
                bucket = ref Unsafe.Add(ref bucket, 1);
                valueBucket = ref Unsafe.Add(ref valueBucket, 1);
            }
            value = default!;
            return false;
        }
    }
}
