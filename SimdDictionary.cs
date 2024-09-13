using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Bucket = System.Runtime.Intrinsics.Vector128<byte>;

namespace SimdDictionary {

    internal unsafe static class BucketExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetSlot (this ref readonly Bucket self, int index) =>
            self[index];

        // Use when modifying one or two slots, to avoid a whole-vector-load-then-store
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetSlot (this ref Bucket self, nuint index, byte value) =>
            Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref self), index) = value;
    }

    public partial class SimdDictionary<K, V> : IDictionary<K, V>, ICloneable {
        public enum InsertMode {
            // Fail the insertion if a matching key is found
            EnsureUnique,
            // Overwrite the value if a matching key is found
            OverwriteValue,
            // Overwrite both the key and value if a matching key is found
            OverwriteKeyAndValue,
            // Don't scan for existing matches before inserting into the bucket
            Rehashing
        }

        public enum InsertResult {
            OkAddedNew,
            OkOverwroteExisting,
            NeedToGrow,
            KeyAlreadyPresent,
        }

        // Special result indexes when the key is not found in the bucket
        internal enum ScanBucketResult : int {
            // One or more items cascaded out of the bucket so we need to keep scanning
            Overflowed = 33,
            // Nothing cascaded out of the bucket so we can stop scanning
            NoOverflow = 34,
        }

        public const int InitialCapacity = 0,
            // User-specified capacity values will be increased to this percentage in order
            //  to maintain an ideal load factor. FIXME: 120 isn't right
            OversizePercentage = 120,
            BucketSizeI = 14,
            CountSlot = 14,
            CascadeSlot = 15;

        public const uint
            // We need to make sure suffixes are never zero, and it's
            //  more likely that a bad hash will collide at the top bit
            //  than at the bottom (i.e. using an int/ptr as its own hash)
            SuffixSalt = 0b10000000,
            BucketSizeU = 14;

        public readonly KeyCollection Keys;
        public readonly ValueCollection Values;
        public readonly IEqualityComparer<K>? Comparer;
        private int _Count, _GrowAtCount;
        private Bucket[] _Buckets;
        private K[] _Keys;
        private V[] _Values;

        public SimdDictionary () 
            : this (InitialCapacity, null) {
        }

        public SimdDictionary (int capacity)
            : this (capacity, null) {
        }

        public SimdDictionary (IEqualityComparer<K>? comparer)
            : this (InitialCapacity, comparer) {
        }

        public SimdDictionary (int capacity, IEqualityComparer<K>? comparer) {
            Unsafe.SkipInit(out _Buckets);
            Unsafe.SkipInit(out _Keys);
            Unsafe.SkipInit(out _Values);
            if (typeof(K).IsValueType)
                Comparer = comparer;
            else
                Comparer = comparer ?? EqualityComparer<K>.Default;            
            EnsureCapacity(capacity);
            Keys = new KeyCollection(this);
            Values = new ValueCollection(this);
        }

        public SimdDictionary (SimdDictionary<K, V> source) 
            : this (source.Count, source.Comparer) {
            _Count = source.Count;
            // FIXME: Optimize this
            foreach (var kvp in source)
                if (TryInsert(kvp.Key, kvp.Value, InsertMode.Rehashing) != InsertResult.OkAddedNew)
                    throw new Exception();
        }

        static int AdjustCapacity (int capacity) {
            if (capacity < 1)
                capacity = 1;
            var bucketCount = ((capacity + BucketSizeI - 1) / BucketSizeI);
            // Power-of-two bucket counts enable using & (count - 1) instead of mod count
            var npot = BitOperations.RoundUpToPowerOf2((uint)bucketCount);
            return (int)(npot * BucketSizeI);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            checked {
                capacity = AdjustCapacity((int)((long)capacity * OversizePercentage / 100));
            }

            if ((_Buckets != null) && (Capacity >= capacity))
                return;

            int nextIncrement = (_Buckets == null)
                ? capacity
                : Capacity * 2;

            if (!TryResize(Math.Max(capacity, nextIncrement)))
                throw new Exception("Internal error: Failed to resize");
        }

        internal bool TryResize (int capacity) {
            var oldCount = _Count;
            var bucketCount = capacity / BucketSizeI;
            var oldBuckets = _Buckets;
            var oldKeys = _Keys;
            var oldValues = _Values;
            checked {
                _GrowAtCount = (int)(((long)capacity) * 100 / OversizePercentage);
            }
            _Buckets = new Bucket[bucketCount];
            _Keys = new K[capacity];
            _Values = new V[capacity];
            // FIXME: In-place rehashing
            if (oldBuckets != null)
                if (!TryRehash(oldBuckets, oldKeys, oldValues))
                    return false;
            return true;
        }

        internal bool TryRehash (Bucket[] oldBuckets, K[] oldKeys, V[] oldValues) {
            for (uint i = 0; i < oldBuckets.Length; i++) {
                ref var bucket = ref oldBuckets[i];
                var baseIndex = i * BucketSizeU;

                for (int j = 0, c = bucket.GetSlot(CountSlot); j < c; j++) {
                    if (bucket.GetSlot(j) == 0)
                        continue;

                    if (TryInsert(oldKeys[baseIndex + j], oldValues[baseIndex + j], InsertMode.Rehashing) != InsertResult.OkAddedNew)
                        return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
            if (comparer == null)
                return unchecked((uint)key!.GetHashCode());
            else
                return unchecked((uint)comparer.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetHashSuffix (uint hashCode) =>
            // The bottom bits of the hash form the bucket index, so we
            //  use the top bits of the hash as a suffix
            unchecked((byte)((hashCode >> 24) | SuffixSalt));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindSuffixInBucket (Bucket bucket, Bucket searchVector) {
            // We create the search vector on the fly with Vector128.Create(suffix) because passing it as a param from outside
            //  seems to cause RyuJIT to generate inferior code instead of flowing it through a register even when inlined
            if (Sse2.IsSupported) {
                return BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.CompareEqual(searchVector, bucket)));
            } else if (AdvSimd.Arm64.IsSupported) {
                // Completely untested
                var matchVector = AdvSimd.CompareEqual(searchVector, bucket);
                var masked = AdvSimd.And(matchVector, Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128));
                var bits = AdvSimd.Arm64.AddAcross(masked.GetLower()).ToScalar() |
                    (AdvSimd.Arm64.AddAcross(masked.GetUpper()).ToScalar() << 8);
                return BitOperations.TrailingZeroCount(bits);
            } else
                // FIXME: Scalar implementation like dn_simdhash's
                throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindKeyInBucket (byte count, Span<K> keys, int elementIndex, int indexInBucket, IEqualityComparer<K>? comparer, K needle) {
            // We need to duplicate the loop header logic and move it inside the if, otherwise
            //  count gets spilled to the stack.
            if (typeof(K).IsValueType && (comparer == null)) {
                // We do this instead of a for-loop so we can skip ReadKey when there's no match,
                //  which improves performance for missing items and/or hash collisions
                if (indexInBucket >= count)
                    return -1;

                ref K key = ref keys[elementIndex + indexInBucket];
                do {
                    if (EqualityComparer<K>.Default.Equals(needle, key))
                        return indexInBucket;
                    indexInBucket++;
                    key = ref Unsafe.Add(ref key, 1);
                } while (indexInBucket < count);
            } else {
                if (indexInBucket >= count)
                    return -1;

                ref K key = ref keys[elementIndex + indexInBucket];
                do {
                    if (comparer!.Equals(needle, key))
                        return indexInBucket;
                    indexInBucket++;
                    key = ref Unsafe.Add(ref key, 1);
                } while (indexInBucket < count);
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int FindKey (K key) {
            if (_Count == 0)
                return -1;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var buckets = (Span<Bucket>)_Buckets;
            var keys = (Span<K>)_Keys;
            var initialBucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
            var bucketIndex = initialBucketIndex;
            var elementIndex = bucketIndex * BucketSizeI;
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref buckets[initialBucketIndex];

            do {
                // Eagerly load the bucket into a local, otherwise each reference to 'Bucket' will do an indirect load
                // var bucketRegister = bucket;
                int startIndex = FindSuffixInBucket(bucket, searchVector);
                int index = FindKeyInBucket(bucket[CountSlot], keys, elementIndex, startIndex, comparer, key);
                if (index < 0) {
                    if (bucket[CascadeSlot] == 0)
                        return -1;
                } else
                    return elementIndex + index;

                bucketIndex++;
                if (bucketIndex >= buckets.Length) {
                    bucketIndex = elementIndex = 0;
                    bucket = ref buckets[0];
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    elementIndex += BucketSizeI;
                }
            } while (bucketIndex != initialBucketIndex);

            return -1;
        }

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // Public for disasmo
        public InsertResult TryInsert (K key, V value, InsertMode mode) {
            // FIXME: Load factor
            if ((_Keys == null) || (_Count >= _GrowAtCount))
                return InsertResult.NeedToGrow;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var buckets = (Span<Bucket>)_Buckets;
            var keys = (Span<K>)_Keys;
            var values = (Span<V>)_Values;
            var initialBucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
            var bucketIndex = initialBucketIndex;
            var elementIndex = bucketIndex * BucketSizeI;
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref buckets[initialBucketIndex];

            do {
                byte bucketCount = bucket[CountSlot];
                if (mode != InsertMode.Rehashing) {
                    // Eagerly load the bucket into a local, otherwise each reference to 'Bucket' will do an indirect load
                    // var bucketRegister = bucket;
                    int startIndex = FindSuffixInBucket(bucket, searchVector);
                    int index = FindKeyInBucket(bucketCount, keys, elementIndex, startIndex, comparer, key);
                    if (index >= 0) {
                        if (mode == InsertMode.EnsureUnique)
                            return InsertResult.KeyAlreadyPresent;
                        else {
                            if (mode == InsertMode.OverwriteKeyAndValue)
                                keys[index] = key;
                            values[index] = value;
                            return InsertResult.OkOverwroteExisting;
                        }
                    }
                }

                if (bucketCount < BucketSizeU) {
                    unchecked {
                        var valueIndex = elementIndex + bucketCount;
                        bucket.SetSlot(CountSlot, (byte)(bucketCount + 1));
                        bucket.SetSlot(bucketCount, suffix);
                        keys[valueIndex] = key;
                        values[valueIndex] = value;
                    }

                    // We may have cascaded out of a previous bucket; if so, scan backwards and update
                    //  the cascade count for every bucket we previously scanned.
                    while (bucketIndex != initialBucketIndex) {
                        bucketIndex--;
                        if (bucketIndex < 0)
                            bucketIndex = buckets.Length - 1;
                        bucket = ref buckets[bucketIndex];
                        var cascadeCount = bucket[CascadeSlot];
                        if (cascadeCount < 255)
                            bucket.SetSlot(CascadeSlot, (byte)(cascadeCount + 1));
                    }

                    return InsertResult.OkAddedNew;
                }

                bucketIndex++;
                if (bucketIndex >= buckets.Length) {
                    bucketIndex = elementIndex = 0;
                    bucket = ref buckets[0];
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    elementIndex += BucketSizeI;
                }
            } while (bucketIndex != initialBucketIndex);

            // FIXME: This shouldn't be possible
            return InsertResult.NeedToGrow;
        }

        public V this[K key] { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                if (!TryGetValue(key, out var result))
                    throw new KeyNotFoundException();
                else
                    return result;
            }
            // FIXME: In-place modification
            set => Add(key, value);
        }

        ICollection<K> IDictionary<K, V>.Keys => Keys;
        ICollection<V> IDictionary<K, V>.Values => Values;

        public int Count => _Count;
        public int Capacity => _GrowAtCount;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        public void Add (K key, V value) {
            var ok = TryAdd(key, value);
            if (!ok)
                throw new ArgumentException($"Key already exists: {key}");
        }

        public bool TryAdd (K key, V value) {
        retry:
            var insertResult = TryInsert(key, value, InsertMode.EnsureUnique);
            switch (insertResult) {
                case InsertResult.OkAddedNew:
                    _Count++;
                    return true;
                case InsertResult.NeedToGrow:
                    TryResize(_Count + 1);
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

        public bool ContainsKey (K key) =>
            FindKey(key) >= 0;

        void ICollection<KeyValuePair<K, V>>.CopyTo (KeyValuePair<K, V>[] array, int arrayIndex) {
            using (var e = GetEnumerator())
                while (e.MoveNext())
                    array[arrayIndex++] = e.Current;
        }

        public Enumerator GetEnumerator () =>
            new Enumerator(this);

        IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator () =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator () =>
            GetEnumerator();

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Remove (K key) {
            if (_Count <= 0)
                return false;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var buckets = (Span<Bucket>)_Buckets;
            var keys = (Span<K>)_Keys;
            var values = (Span<V>)_Values;
            var initialBucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
            var bucketIndex = initialBucketIndex;
            var elementIndex = bucketIndex * BucketSizeI;
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref buckets[initialBucketIndex];

            do {
                byte bucketCount = bucket[CountSlot];
                // Eagerly load the bucket into a local, otherwise each reference to 'Bucket' will do an indirect load
                // var bucketRegister = bucket;
                int startIndex = FindSuffixInBucket(bucket, searchVector);
                int index = FindKeyInBucket(bucketCount, keys, elementIndex, startIndex, comparer, key);
                if (index >= 0) {
                    unchecked {
                        int replacementIndexInBucket = bucketCount - 1;
                        bucket.SetSlot(CountSlot, (byte)replacementIndexInBucket);
                        bucket.SetSlot((uint)index, bucket.GetSlot(replacementIndexInBucket));
                        bucket.SetSlot((uint)replacementIndexInBucket, 0);
                        ref var replacementKey = ref keys[elementIndex + replacementIndexInBucket];
                        ref var replacementValue = ref values[elementIndex + replacementIndexInBucket];
                        keys[elementIndex + index] = replacementKey;
                        values[elementIndex + index] = replacementValue;
                        replacementKey = default!;
                        replacementValue = default!;
                        _Count--;
                    }

                    // We may have cascaded out of a previous bucket; if so, scan backwards and update
                    //  the cascade count for every bucket we previously scanned.
                    while (bucketIndex != initialBucketIndex) {
                        bucketIndex--;
                        if (bucketIndex < 0)
                            bucketIndex = buckets.Length - 1;
                        bucket = ref buckets[bucketIndex];

                        var cascadeCount = bucket[CascadeSlot];
                        if (cascadeCount == 0)
                            throw new Exception();
                        // If the cascade counter hit 255, it's possible the actual cascade count through here is >255,
                        //  so it's no longer safe to decrement. This is a very rare scenario.
                        else if (cascadeCount < 255)
                            bucket.SetSlot(CascadeSlot, (byte)(cascadeCount - 1));
                    }

                    return true;
                }

                if (bucket[CascadeSlot] == 0)
                    return false;

                bucketIndex++;
                if (bucketIndex >= buckets.Length) {
                    bucketIndex = elementIndex = 0;
                    bucket = ref buckets[0];
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    elementIndex += BucketSizeI;
                }
            } while (bucketIndex != initialBucketIndex);

            return false;
        }

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool TryGetValue (K key, out V value) {
            var index = FindKey(key);
            if (index < 0) {
                value = default!;
                return false;
            } else {
                value = _Values[index];
                return true;
            }
        }

        public object Clone () =>
            new SimdDictionary<K, V>(this);
    }
}
