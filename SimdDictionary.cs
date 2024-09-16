using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
        public static byte GetSlot (this ref readonly Bucket self, int index) {
            Debug.Assert(index < 16);
            // index &= 15;
            return Unsafe.AddByteOffset(ref Unsafe.As<Bucket, byte>(ref Unsafe.AsRef(in self)), index);
            // the extract-lane opcode this generates is slower than doing a byte load from memory,
            //  even if we already have the bucket in a register. not sure why.
            // self[index];
        }

        // Use when modifying one or two slots, to avoid a whole-vector-load-then-store
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetSlot (this ref Bucket self, nuint index, byte value) {
            Debug.Assert(index < 16);
            // index &= 15;
            Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref self), index) = value;
        }
    }

    public partial class SimdDictionary<K, V> : IDictionary<K, V>, ICloneable {
        internal record struct Entry (K Key, V Value);

        public enum InsertMode {
            // Fail the insertion if a matching key is found
            EnsureUnique,
            // Overwrite the value if a matching key is found
            OverwriteValue,
            // Don't scan for existing matches before inserting into the bucket. This is only
            //  safe to do when copying an existing dictionary or rehashing an existing dictionary
            Rehashing
        }

        public enum InsertResult {
            // The specified key did not exist in the dictionary, and a key/value pair was inserted
            OkAddedNew,
            // The specified key was found in the dictionary and we overwrote the value
            OkOverwroteExisting,
            // The dictionary is full and needs to be grown before you can perform an insert
            NeedToGrow,
            // The specified key already exists in the dictionary, so nothing was done
            KeyAlreadyPresent,
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
        private Bucket[]? _Buckets;
        private Entry[]? _Entries;

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
            if (typeof(K).IsValueType)
                Comparer = comparer;
            else if (typeof(K) == typeof(string))
                Comparer = comparer ?? (IEqualityComparer<K>)StringComparer.Ordinal;
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
                    Environment.FailFast("Failed to insert key/value pair while copying dictionary");
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
            else if (capacity == 0)
                return;

            if ((_Buckets != null) && (Capacity >= capacity))
                return;

            int nextIncrement = (_Buckets == null)
                ? capacity
                : Capacity * 2;

            Resize(Math.Max(capacity, nextIncrement));
        }

        internal void Resize (int capacity) {
            checked {
                capacity = AdjustCapacity((int)((long)capacity * OversizePercentage / 100));
            }

            var oldCount = _Count;
            var bucketCount = (capacity + BucketSizeI - 1) / BucketSizeI;
            var actualCapacity = bucketCount * BucketSizeI;
            var oldBuckets = _Buckets;
            var oldEntries = _Entries;
            checked {
                _GrowAtCount = (int)(((long)actualCapacity) * 100 / OversizePercentage);
            }
            _Buckets = new Bucket[bucketCount];
            _Entries = new Entry[actualCapacity];
            // FIXME: In-place rehashing
            if ((oldBuckets != null) && (oldBuckets.Length > 0) && (oldEntries != null))
                if (!TryRehash(oldBuckets, oldEntries))
                    Environment.FailFast("Failed to rehash dictionary for resize operation");
        }

        internal bool TryRehash (Bucket[] _oldBuckets, Entry[] _oldEntries) {
            var oldBuckets = (Span<Bucket>)_oldBuckets;
            var oldEntries = (Span<Entry>)_oldEntries;
            for (int i = 0; i < oldBuckets.Length; i++) {
                var baseIndex = i * BucketSizeI;
                ref var bucket = ref oldBuckets[i];

                for (int j = 0, c = bucket.GetSlot(CountSlot); j < c; j++) {
                    Debug.Assert(c <= BucketSizeI);
                    if (bucket.GetSlot(j) == 0)
                        continue;

                    ref var entry = ref oldEntries[baseIndex + j];
                    if (TryInsert(entry.Key, entry.Value, InsertMode.Rehashing) != InsertResult.OkAddedNew)
                        return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
            if (typeof(K).IsValueType) {
                if (comparer == null)
                    return unchecked((uint)EqualityComparer<K>.Default.GetHashCode(key!));
            }

            return unchecked((uint)comparer!.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetHashSuffix (uint hashCode) {
            // The bottom bits of the hash form the bucket index, so we
            //  use the top bits of the hash as a suffix
            // var result = unchecked((byte)((hashCode >> 24) | SuffixSalt));
            var result = unchecked((byte)(hashCode >> 24));
            // Assuming the JIT turns this into a cmov, this should be better on average
            //  since it nearly doubles the number of possible suffixes, improving collision
            //  resistance and reducing the odds of having to check multiple keys.
            return result == 0 ? (byte)255 : result;
        }

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
            } else {
                // FIXME: Scalar implementation like dn_simdhash's
                Environment.FailFast("Scalar search not implemented");
                return 32;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref Entry FindKeyInBucketWithDefaultComparer (byte count, ref Entry firstEntryInBucket, int indexInBucket, K needle) {
            Debug.Assert(indexInBucket >= 0);
            Debug.Assert(count <= BucketSizeI);

            // We need to duplicate the loop header logic and move it inside the if, otherwise
            //  count gets spilled to the stack.
            if (typeof(K).IsValueType) {
                // We do this instead of a for-loop so we can skip ReadKey when there's no match,
                //  which improves performance for missing items and/or hash collisions
                if (indexInBucket >= count)
                    return ref Unsafe.NullRef<Entry>();

                ref Entry entry = ref Unsafe.Add(ref firstEntryInBucket, indexInBucket);
                do {
                    if (EqualityComparer<K>.Default.Equals(needle, entry.Key))
                        return ref entry;
                    indexInBucket++;
                    entry = ref Unsafe.Add(ref entry, 1);
                } while (indexInBucket < count);
            } else {
                Environment.FailFast("FindKeyInBucketWithDefaultComparer called for non-struct key type");
            }

            return ref Unsafe.NullRef<Entry>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref Entry FindKeyInBucket (byte count, ref Entry firstEntryInBucket, int indexInBucket, IEqualityComparer<K> comparer, K needle) {
            Debug.Assert(indexInBucket >= 0);
            Debug.Assert(count <= BucketSizeI);
            Debug.Assert(comparer != null);

            if (indexInBucket >= count)
                return ref Unsafe.NullRef<Entry>();

            ref Entry entry = ref Unsafe.Add(ref firstEntryInBucket, indexInBucket);
            do {
                if (comparer.Equals(needle, entry.Key))
                    return ref entry;
                indexInBucket++;
                entry = ref Unsafe.Add(ref entry, 1);
            } while (indexInBucket < count);

            return ref Unsafe.NullRef<Entry>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref Entry FindKey (K key) {
            if (_Count == 0)
                return ref Unsafe.NullRef<Entry>();

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var buckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);
            var initialBucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
            var bucketIndex = initialBucketIndex;
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref buckets[initialBucketIndex];
            ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

            // Optimize for VT with default comparer. We need this outer check to pick the right loop, and then an inner check
            //  to keep ryujit happy
            if (typeof(K).IsValueType && (comparer == null)) {
                // Separate loop and separate find function to avoid the comparer null check per-bucket (yes, it seems to matter)
                do {
                    int startIndex = FindSuffixInBucket(bucket, searchVector);
                    // Checking whether startIndex < 32 would theoretically make this faster, but in practice, it doesn't
                    ref var entry = ref FindKeyInBucketWithDefaultComparer(bucket.GetSlot(CountSlot), ref firstBucketEntry, startIndex, key);
                    if (Unsafe.IsNullRef(ref entry)) {
                        if (bucket.GetSlot(CascadeSlot) == 0)
                            return ref Unsafe.NullRef<Entry>();
                    } else
                        return ref entry;

                    bucketIndex++;
                    if (bucketIndex >= buckets.Length) {
                        bucketIndex = 0;
                        bucket = ref buckets[0];
                        firstBucketEntry = ref entries[0];
                    } else {
                        bucket = ref Unsafe.Add(ref bucket, 1);
                        firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                    }
                } while (bucketIndex != initialBucketIndex);
            } else {
                Debug.Assert(comparer != null);
                do {
                    int startIndex = FindSuffixInBucket(bucket, searchVector);
                    // Checking whether startIndex < 32 would theoretically make this faster, but in practice, it doesn't
                    ref var entry = ref FindKeyInBucket(bucket.GetSlot(CountSlot), ref firstBucketEntry, startIndex, comparer!, key);
                    if (Unsafe.IsNullRef(ref entry)) {
                        if (bucket.GetSlot(CascadeSlot) == 0)
                            return ref Unsafe.NullRef<Entry>();
                    } else
                        return ref entry;

                    bucketIndex++;
                    if (bucketIndex >= buckets.Length) {
                        bucketIndex = 0;
                        bucket = ref buckets[0];
                        firstBucketEntry = ref entries[0];
                    } else {
                        bucket = ref Unsafe.Add(ref bucket, 1);
                        firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                    }
                } while (bucketIndex != initialBucketIndex);
            }

            return ref Unsafe.NullRef<Entry>();
        }

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // Public for disasmo
        public InsertResult TryInsert (K key, V value, InsertMode mode) {
            // FIXME: Load factor
            if ((_Entries == null) || (_Count >= _GrowAtCount))
                return InsertResult.NeedToGrow;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var buckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);
            var initialBucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
            var bucketIndex = initialBucketIndex;
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref buckets[initialBucketIndex];
            ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

            do {
                byte bucketCount = bucket.GetSlot(CountSlot);
                if (mode != InsertMode.Rehashing) {
                    int startIndex = FindSuffixInBucket(bucket, searchVector);
                    ref Entry entry = ref (typeof(K).IsValueType && (comparer == null))
                            ? ref FindKeyInBucketWithDefaultComparer(bucketCount, ref firstBucketEntry, startIndex, key)
                            : ref FindKeyInBucket(bucketCount, ref firstBucketEntry, startIndex, comparer!, key);

                    if (!Unsafe.IsNullRef(ref entry)) {
                        if (mode == InsertMode.EnsureUnique)
                            return InsertResult.KeyAlreadyPresent;
                        else {
                            entry.Value = value;
                            return InsertResult.OkOverwroteExisting;
                        }
                    } else if (startIndex < BucketSizeI) {
                        // FIXME: Suffix collision. Track these for string rehashing anti-DoS mitigation!
                    }
                }

                if (bucketCount < BucketSizeU) {
                    unchecked {
                        ref var entry = ref Unsafe.Add(ref firstBucketEntry, bucketCount);
                        bucket.SetSlot(CountSlot, (byte)(bucketCount + 1));
                        bucket.SetSlot(bucketCount, suffix);
                        entry = new Entry(key, value);
                    }

                    // We may have cascaded out of a previous bucket; if so, scan backwards and update
                    //  the cascade count for every bucket we previously scanned.
                    while (bucketIndex != initialBucketIndex) {
                        // FIXME: Track number of times we cascade out of a bucket for string rehashing anti-DoS mitigation!
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
                    bucketIndex = 0;
                    bucket = ref buckets[0];
                    firstBucketEntry = ref entries[0];
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                }
            } while (bucketIndex != initialBucketIndex);

            // FIXME: This shouldn't be possible
            return InsertResult.NeedToGrow;
        }

        public V this[K key] { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                if (!TryGetValue(key, out var result))
                    throw new KeyNotFoundException($"Key not found: {key}");
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
                    Resize(_GrowAtCount * 2);
                    goto retry;
                default:
                    return false;
            }
        }

        void ICollection<KeyValuePair<K, V>>.Add (KeyValuePair<K, V> item) =>
            Add(item.Key, item.Value);

        public void Clear () {
            _Count = 0;
            if (_Buckets != null)
                Array.Clear(_Buckets);
            if ((_Entries != null) && RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                Array.Clear(_Entries);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) =>
            TryGetValue(item.Key, out var value) &&
            (value?.Equals(item.Value) == true);

        public bool ContainsKey (K key) =>
            !Unsafe.IsNullRef(ref FindKey(key));

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // Unsafe for sizeof
        public unsafe bool Remove (K key) {
            if (_Count <= 0)
                return false;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var buckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);
            var initialBucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
            var bucketIndex = initialBucketIndex;
            var searchVector = Vector128.Create(suffix);
            ref var bucket = ref buckets[initialBucketIndex];
            ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

            do {
                byte bucketCount = bucket.GetSlot(CountSlot);
                int startIndex = FindSuffixInBucket(bucket, searchVector);

                ref var entry = ref 
                    (typeof(K).IsValueType && (comparer == null))
                        ? ref FindKeyInBucketWithDefaultComparer(bucketCount, ref firstBucketEntry, startIndex, key)
                        : ref FindKeyInBucket(bucketCount, ref firstBucketEntry, startIndex, comparer!, key);

                if (!Unsafe.IsNullRef(ref entry)) {
                    Debug.Assert(bucketCount > 0);

                    unchecked {
#pragma warning disable CS8500
                        // FIXME: Why isn't there an Unsafe.Offset?
                        int indexInBucket = (int)(Unsafe.ByteOffset(ref firstBucketEntry, ref entry) / sizeof(Entry));
                        Debug.Assert((Unsafe.ByteOffset(ref firstBucketEntry, ref entry) % sizeof(Entry)) == 0);
#pragma warning restore CS8500
                        int replacementIndexInBucket = bucketCount - 1;
                        bucket.SetSlot(CountSlot, (byte)replacementIndexInBucket);
                        bucket.SetSlot((uint)indexInBucket, bucket.GetSlot(replacementIndexInBucket));
                        bucket.SetSlot((uint)replacementIndexInBucket, 0);
                        ref var replacementEntry = ref Unsafe.Add(ref firstBucketEntry, replacementIndexInBucket);
                        entry = replacementEntry;
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                            replacementEntry = default;
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
                            Environment.FailFast("Corrupted dictionary bucket cascade slot");
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
                    bucketIndex = 0;
                    bucket = ref buckets[0];
                    firstBucketEntry = ref entries[0];
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                }
            } while (bucketIndex != initialBucketIndex);

            return false;
        }

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue (K key, out V value) {
            ref var entry = ref FindKey(key);
            if (Unsafe.IsNullRef(ref entry)) {
                value = default!;
                return false;
            } else {
                value = entry.Value;
                return true;
            }
        }

        public object Clone () =>
            new SimdDictionary<K, V>(this);
    }
}
