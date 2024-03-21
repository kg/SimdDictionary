using System.Collections;
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

        public const int InitialCapacity = KeyBucket.Length * 4;

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

        internal struct KeyBucket {
            // Must be <= (Data.Count - 2)
            public const int Length = 14;

            // Must be the same as KeyBucket.Length
            [InlineArray(14)]
            public struct KeyArray {
                public K Key;
            }

            public Vector128<byte> HashSuffixes;
            public KeyArray Keys;

            public byte Count {
                get => HashSuffixes[Length];
                set => HashSuffixes = HashSuffixes.WithElement(Length, value);
            }
            public bool Cascaded {
                get => HashSuffixes[Length + 1] != 0;
                set => HashSuffixes = HashSuffixes.WithElement(Length + 1, value ? (byte)1 : (byte)0);
            }
        }

        // Must be the same as KeyBucket.Length
        [InlineArray(14)]
        internal struct ValueBucket {
            public V Value;
        }

        public readonly IEqualityComparer<K> Comparer;
        private int _Count;
        private KeyBucket[] _Keys;
        private ValueBucket[] _Values;

        public SimdDictionary () 
            : this (InitialCapacity, EqualityComparer<K>.Default) {
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

        public void EnsureCapacity (int capacity) {
            capacity = ((capacity + KeyBucket.Length - 1) / KeyBucket.Length) * KeyBucket.Length;

            if ((_Keys != null) && (Capacity >= capacity))
                return;

            int nextDoubling = (_Keys == null)
                ? capacity
                : Capacity * 2;

            if (!TryResize(Math.Max(capacity, nextDoubling)))
                throw new Exception("Internal error: Failed to resize");
        }

        internal void EnsureSpaceForNewItem () {
            // FIXME: Maintain good load factor
            EnsureCapacity(_Count + 1);
        }

        internal bool TryResize (int capacity) {
            var oldCount = _Count;
            var bucketCount = capacity / KeyBucket.Length;
            var newKeys = new KeyBucket[bucketCount];
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

        internal int TryRehash (KeyBucket[] newKeys, ValueBucket[] newValues, KeyBucket[] oldKeys, ValueBucket[] oldValues) {
            int newCount = 0;

            for (int i = 0, l = oldKeys.Length; i < l; i++) {
                ref var oldBucket = ref oldKeys[i];
                ref var oldValueBucket = ref oldValues[i];
                for (int j = 0, l2 = oldBucket.Count; j < l2; j++) {
                    ref var oldKey = ref oldBucket.Keys[j]!;
                    ref var oldValue = ref oldValueBucket[j];
                    var insertResult = TryInsert(newKeys, newValues, ref oldKey, ref oldValue, false);
                    if (insertResult != InsertFailureReason.None)
                        return newCount;
                    newCount++;
                }
            }

            return newCount;
        }

        internal static byte GetHashSuffix (uint hashCode) {
            var result = hashCode & 0xFF;
            if (result == 0)
                return 1;
            else
                return (byte)result;
        }

        internal int FindInBucket (ref KeyBucket bucket, byte suffix, ref K key) {
            var searchVector = Vector128.Create(suffix);
            var matchVector = Vector128.Equals(bucket.HashSuffixes, searchVector);
            if (matchVector.Equals(Vector128<byte>.Zero))
                return -1;
            for (int i = 0; i < KeyBucket.Length; i++) {
                if (matchVector[i] == 0)
                    continue;
                if (Comparer.Equals(bucket.Keys[i], key))
                    return i;
            }
            return -1;
        }

        internal bool FindExisting (KeyBucket[] keys, uint firstBucketIndex, byte suffix, ref K key) {
            for (uint i = firstBucketIndex; i < keys.Length; i++) {
                ref var bucket = ref keys[i];
                var index = FindInBucket(ref bucket, suffix, ref key);
                if (index >= 0)
                    return true;
                if (!bucket.Cascaded)
                    return false;
            }
            return false;
        }

        internal InsertFailureReason TryInsert (KeyBucket[] keys, ValueBucket[] values, ref K key, ref V value, bool ensureNotPresent) {
            var hashCode = unchecked((uint)Comparer.GetHashCode(key!));
            var suffix = GetHashSuffix(hashCode);
            var bucketIndex = GetBucketIndex(keys, hashCode);

            while (bucketIndex < keys.Length) {
                if (ensureNotPresent) {
                    if (FindExisting(keys, bucketIndex, suffix, ref key))
                        return InsertFailureReason.AlreadyPresent;
                }

                ref var newBucket = ref keys[bucketIndex];
                var index = newBucket.Count;
                if (index >= KeyBucket.Length) {
                    newBucket.Cascaded = true;
                    bucketIndex++;
                    continue;
                }

                ref var valueBucket = ref values[bucketIndex];
                newBucket.Count++;
                newBucket.Keys[index] = key;
                newBucket.HashSuffixes = newBucket.HashSuffixes.WithElement(index, suffix);
                valueBucket[index] = value;

                return InsertFailureReason.None;
            }

            return InsertFailureReason.NeedToGrow;
        }

        internal static ref KeyBucket GetBucket (KeyBucket[] keys, uint hashCode) =>
            ref keys[hashCode % (uint)keys.Length];

        internal static uint GetBucketIndex (KeyBucket[] keys, uint hashCode) =>
            hashCode % (uint)keys.Length;

        public V this[K key] { 
            get => throw new NotImplementedException(); 
            set => throw new NotImplementedException(); 
        }

        ICollection<K> IDictionary<K, V>.Keys => throw new NotImplementedException();

        ICollection<V> IDictionary<K, V>.Values => throw new NotImplementedException();

        public int Count => _Count;
        public int Capacity => _Keys.Length * KeyBucket.Length;

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
            return FindExisting(keys, firstBucketIndex, suffix, ref key);
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
            var firstBucketIndex = GetBucketIndex(keys, hashCode);

            for (uint i = firstBucketIndex; i < keys.Length; i++) {
                ref var bucket = ref keys[i];
                var index = FindInBucket(ref bucket, suffix, ref key);
                if (index >= 0) {
                    value = _Values[i][index];
                    return true;
                }
                if (!bucket.Cascaded)
                    break;
            }
            value = default!;
            return false;
        }
    }
}
