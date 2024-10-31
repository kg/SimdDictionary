// This is considerably slower than power-of-two bucket counts, but it provides
//  much better collision resistance than power-of-two bucket counts do. However,
// Murmur3 finalization + Power-of-two bucket counts (see below) performs much better and has
//  higher collision resistance due to improving the quality of suffixes
#define PRIME_BUCKET_COUNTS
// Performs a murmur3 finalization mix on hashcodes before using them, for collision resistance
// #define PERMUTE_HASH_CODES
// Use an unrolled slot-clearing routine instead of just doing Pairs = default. Unclear whether this is better.
// Probably *is* better for keys or values that are very big and contain references.
#define UNROLLED_CLEAR_WITH_REFS

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace SimdDictionary {
    public partial class SimdDictionary<K, V> : 
        IDictionary<K, V>, IDictionary, IReadOnlyDictionary<K, V>, 
        ICollection<KeyValuePair<K, V>>, ICloneable, ISerializable, IDeserializationCallback
        where K : notnull
    {
#if PRIME_BUCKET_COUNTS
        private ulong _fastModMultiplier;
#endif

        private const int MinimumEntryCapacity = 6,
            FreeListIndex_Occupied = -1,
            FreeListIndex_EndOfFreeList = int.MaxValue;

        // TODO: Make these reference types initialized on demand / add cache fields for the accessors that box them.
        public readonly KeyCollection Keys;
        public readonly ValueCollection Values;
        public readonly IEqualityComparer<K>? Comparer;
        // It's important for an empty dictionary to have both count and growatcount be 0
        private int _Count = 0, 
            _GrowAtCount = 0,
            _FreeListStart = -2;

#pragma warning disable CA1825
        // HACK: All empty SimdDictionary instances share a single-bucket EmptyBuckets array, so that Find and Remove
        //  operations don't need to do a (_Count == 0) check. This also makes some other uses of ref and MemoryMarshal
        //  safe-by-definition instead of fragile, since we always have a valid reference to the "first" bucket, even when
        //  we're empty.
        private static readonly Bucket[] EmptyBuckets = [ Bucket.Empty ];
        private static readonly Entry[] EmptyEntries = [ Entry.Empty ];
#pragma warning restore CA1825

        private Bucket[] _Buckets = EmptyBuckets;
        private Entry[] _Entries = EmptyEntries;

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
            // HACK: DefaultEqualityComparer<K> for string is really bad
            else if (typeof(K) == typeof(string))
                Comparer = comparer ?? (IEqualityComparer<K>)StringComparer.Ordinal;
            else
                Comparer = comparer ?? EqualityComparer<K>.Default;            
            EnsureCapacity(capacity);
            Keys = new KeyCollection(this);
            Values = new ValueCollection(this);
        }

        public SimdDictionary (SimdDictionary<K, V> source) {
            Keys = new KeyCollection(this);
            Values = new ValueCollection(this);
            Comparer = source.Comparer;
            _Count = source._Count;
            _GrowAtCount = source._GrowAtCount;
            _FreeListStart = source._FreeListStart;
#if PRIME_BUCKET_COUNTS
            _fastModMultiplier = source._fastModMultiplier;
#endif
            if (source._Buckets != EmptyBuckets) {
                _Buckets = new Bucket[source._Buckets.Length];
                _Entries = new Entry[source._Entries.Length];
                Array.Copy(source._Buckets, _Buckets, _Buckets.Length);
                Array.Copy(source._Entries, _Entries, _Count);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            else if (capacity == 0)
                return;

            if (Capacity >= capacity)
                return;

            int nextIncrement = (_Buckets == EmptyBuckets)
                ? capacity
                : Capacity * 2;

            Resize(Math.Max(capacity, nextIncrement));
        }

        internal void Resize (int capacity) {
            int bucketCount;
#if PRIME_BUCKET_COUNTS
            ulong fastModMultiplier;
#endif

            checked {
                capacity = (int)((long)capacity * OversizePercentage / 100);
                if (capacity < MinimumEntryCapacity)
                    capacity = MinimumEntryCapacity;

                bucketCount = ((capacity + BucketSizeI - 1) / BucketSizeI);

#if PRIME_BUCKET_COUNTS
                bucketCount = bucketCount > 1 ? HashHelpers.GetPrime(bucketCount) : 1;
                fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)bucketCount);
#else
                // Power-of-two bucket counts enable using & (count - 1) instead of mod count
                bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)bucketCount);
#endif
            }

            var actualCapacity = bucketCount * BucketSizeI;
            var oldBuckets = _Buckets;
            var oldEntries = _Entries;
            checked {
                // Under-allocate values array for small (< 1 bucket worth of items) dictionaries
                if (capacity <= BucketSizeI)
                    _GrowAtCount = capacity;
                else
                    _GrowAtCount = (int)(((long)actualCapacity) * 100 / OversizePercentage);
            }

            // Allocate new array before updating fields so that we don't get corrupted when running out of memory
            var newBuckets = new Bucket[bucketCount];
            var newEntries = new Entry[_GrowAtCount];
            // Initialize all the bucket indices to -1, we'll populate them during rehash
            Array.Fill(newBuckets, Bucket.Empty);
            Array.Copy(oldEntries, newEntries, oldEntries.Length);
            // Initialize the new empty entries to not be part of the freelist
            Array.Fill(newEntries, Entry.Empty, oldEntries.Length, _GrowAtCount - oldEntries.Length);
            // Store the larger entries array first before storing a buckets array that could end up referencing an out-of-range index
            _Entries = newEntries;
            Thread.MemoryBarrier();
            // Ensure we store a new larger bucket array before storing the fastModMultiplier for the larger size.
            // This ensures that concurrent modification will not produce a bucket index that is too big.
            _Buckets = newBuckets;
            Thread.MemoryBarrier();
#if PRIME_BUCKET_COUNTS
            // FIXME: How do we guard this against concurrent modification?
            _fastModMultiplier = fastModMultiplier;
#endif
            if ((oldBuckets != EmptyBuckets) && (_Count > 0))
                if (!TryRehash(oldBuckets))
                    Environment.FailFast("Failed to rehash dictionary for resize operation");
        }

        internal bool TryRehash (Bucket[] _oldBuckets) {
            var oldBuckets = (Span<Bucket>)_oldBuckets;
            var newBuckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            var comparer = Comparer;
            var count = _Count;

            for (int i = 0; i < count; i++) {
                ref var entry = ref entries[i];

                uint hashCode;
                if (typeof(K).IsValueType && (comparer == null)) {
                    hashCode = DefaultComparerKeySearcher.GetHashCode(comparer, entry.Key);
                } else {
                    hashCode = ComparerKeySearcher.GetHashCode(comparer, entry.Key);
                }

                var enumerator = new LoopingBucketEnumerator(this, hashCode);
                var suffix = GetHashSuffix(hashCode);
                var ok = false;
                do {
                    if (TryInsertIntoBucket(ref enumerator.bucket, suffix, enumerator.bucket.Count, i)) {
                        // Increase the cascade counters for the buckets we checked before this one.
                        AdjustCascadeCounts(enumerator, true);
                        ok = true;
                        break;
                    }
                } while (enumerator.Advance());

                if (!ok)
                    return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint FinalizeHashCode (uint hashCode) {
            // TODO: Use static interface methods to determine whether we need to finalize the hash for type K.
            // For BCL types like int32/int64, we need to, but for types with strong hashes like string, we don't,
            //  and for custom comparers, we don't need to do it since the caller is kind of responsible for 
            //  bringing their own quality hash if they want good performance.
            // Doing this would improve best-case performance for key types like Object or String.
#if PERMUTE_HASH_CODES
            // MurmurHash3 was written by Austin Appleby, and is placed in the public
            // domain. The author hereby disclaims copyright to this source code.
            // Finalization mix - force all bits of a hash block to avalanche
            unchecked {
	            hashCode ^= hashCode >> 16;
	            hashCode *= 0x85ebca6b;
	            hashCode ^= hashCode >> 13;
	            hashCode *= 0xc2b2ae35;
	            hashCode ^= hashCode >> 16;
            }
#endif
            return hashCode;
        }

        // The hash suffix is selected from 8 bits of the hash, and then modified to ensure
        //  it is never zero (because a zero suffix indicates an empty slot.)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetHashSuffix (uint hashCode) {
            // The bottom bits of the hash form the bucket index, so we use the top bits as a suffix
            // We could use a lower shift to try and improve tail collision performance,
            //  but the improvement from that is pretty small and it makes head collisions worse.
            // The right solution is just to use a good hash function.
            const int hashShift = 24;
            var result = unchecked((byte)(hashCode >> hashShift));
            // Assuming the JIT turns this into a cmov, this should be better on average
            //  since it nearly doubles the number of possible suffixes, improving collision
            //  resistance and reducing the odds of having to check multiple keys.
            return result == 0 ? (byte)255 : result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int BucketIndexForHashCode (uint hashCode, Span<Bucket> buckets) =>
#if PRIME_BUCKET_COUNTS
            // NOTE: If the caller observes a new _fastModMultiplier before seeing a larger buckets array,
            //  this can overrun the end of the array.
            unchecked((int)HashHelpers.FastMod(
                hashCode, (uint)buckets.Length, 
                // Volatile.Read to ensure that the load of _fastModMultiplier can't get moved before load of _Buckets
                // This doesn't appear to generate a memory barrier or anything.
                Volatile.Read(ref _fastModMultiplier)
            ));
#else
            unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int FindKey (K key) {
            var comparer = Comparer;
            if (typeof(K).IsValueType && (comparer == null))
                return FindKey<DefaultComparerKeySearcher>(key, null);
            else
                return FindKey<ComparerKeySearcher>(key, comparer);
        }

        // Performance is much worse unless this method is inlined, I'm not sure why.
        // If we disable inlining for it, our generated code size is roughly halved.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int FindKey<TKeySearcher> (K key, IEqualityComparer<K>? comparer)
            where TKeySearcher : struct, IKeySearcher 
        {
            var hashCode = TKeySearcher.GetHashCode(comparer, key);
            // It's important to construct the enumerator before computing the suffix, to avoid stalls
            var enumerator = new LoopingBucketEnumerator(this, hashCode);
            // Pre-creating the search vector here using Vector128.Create is possible, but it gets stored into xmm6 and causes
            //  an unconditional stack spill at entry, which outweighs any performance benefit from creating it early.
            // vpbroadcastb is 1op/1c latency on most x86-64 chips, so doing it early isn't very useful either.
            // Unfortunately in some cases Vector128.Create(suffix) gets LICM'd into xmm6 too... https://github.com/dotnet/runtime/issues/108092
            var suffix = GetHashSuffix(hashCode);
            Span<Entry> pairs = _Entries;
            do {
                // Eagerly load the bucket count early for pipelining purposes, so we don't stall when using it later.
                int bucketCount = enumerator.bucket.Count, 
                    startIndex = FindSuffixInBucket(ref enumerator.bucket, suffix, bucketCount);
                var result = TKeySearcher.FindKeyInBucket(ref enumerator.bucket, pairs, startIndex, bucketCount, comparer, key, out _);
                if (result < 0) {
                    if (enumerator.bucket.CascadeCount == 0)
                        return result;
                } else
                    return result;
            } while (enumerator.Advance());

            return -1;
        }

        // public for Disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryInsert (K key, V value, InsertMode mode) {
            var comparer = Comparer;
            if (typeof(K).IsValueType && (comparer == null))
                return TryInsert<DefaultComparerKeySearcher>(key, value, mode, null);
            else
                return TryInsert<ComparerKeySearcher>(key, value, mode, comparer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryInsertIntoBucket (ref Bucket bucket, byte suffix, int bucketCount, int entryIndex) {
            if (bucketCount >= BucketSizeI)
                return false;

            unchecked {
                ref var destination = ref Unsafe.Add(ref bucket.Indices.Index0, bucketCount);
                bucket.Count = (byte)(bucketCount + 1);
                bucket.SetSlot((nuint)bucketCount, suffix);
                destination = entryIndex;
#if DEBUG
                if (destination != entryIndex)
                    throw new Exception();
                if (bucket.Indices[bucketCount] != entryIndex)
                    throw new Exception();
#endif
            }

            return true;
        }

        // Inlining required for acceptable codegen
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InsertResult TryInsert<TKeySearcher> (K key, V value, InsertMode mode, IEqualityComparer<K>? comparer) 
            where TKeySearcher : struct, IKeySearcher 
        {
            var freeListStart = _FreeListStart;
            var newEntryIndex = freeListStart >= 0 ? freeListStart : _Count;
            var needToGrow = (newEntryIndex >= _GrowAtCount);
            var hashCode = TKeySearcher.GetHashCode(comparer, key);
            // Pipelining: This is almost never true, so don't branch off it immediately.
            if (needToGrow)
                return InsertResult.NeedToGrow;
            // You should have already made sure there was enough space for your value - entryIndex must be valid
            Debug.Assert((newEntryIndex >= 0) && (newEntryIndex < _Entries.Length));
            var enumerator = new LoopingBucketEnumerator(this, hashCode);
            var suffix = GetHashSuffix(hashCode);
            Span<Entry> pairs = _Entries;
            do {
                int bucketCount = enumerator.bucket.Count;
                int startIndex = FindSuffixInBucket(ref enumerator.bucket, suffix, bucketCount);
                int entryIndex = TKeySearcher.FindKeyInBucket(ref enumerator.bucket, pairs, startIndex, bucketCount, comparer, key, out _);

                if (entryIndex >= 0) {
                    if (mode == InsertMode.OverwriteValue) {
                        ref var entry = ref pairs[entryIndex];
                        entry.Value = value;
                        return InsertResult.OkOverwroteExisting;
                    } else
                        return InsertResult.KeyAlreadyPresent;
                } else if (startIndex < BucketSizeI) {
                    // FIXME: Suffix collision. Track these for string rehashing anti-DoS mitigation!
                }

                if (TryInsertIntoBucket(ref enumerator.bucket, suffix, bucketCount, newEntryIndex)) {
                    _Count++;

                    ref var entry = ref pairs[newEntryIndex];
#if DEBUG
                    if (!entry.IsEmpty)
                        throw new Exception();
#endif
                    entry.Key = key;
                    entry.Value = value;
                    var nextFreeSlot = entry.NextFreeSlot;
                    if (nextFreeSlot >= 0) {
                        _FreeListStart = (nextFreeSlot != FreeListIndex_EndOfFreeList) ? nextFreeSlot : -1;
                        entry.NextFreeSlot = -1;
                    }

                    // Increase the cascade counters for the buckets we checked before this one.
                    AdjustCascadeCounts(enumerator, true);

                    return InsertResult.OkAddedNew;
                }
            } while (enumerator.Advance());

            return InsertResult.CorruptedInternalState;
        }

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove (K key) {
            var comparer = Comparer;
            if (typeof(K).IsValueType && (comparer == null))
                return TryRemove<DefaultComparerKeySearcher>(key, null);
            else
                return TryRemove<ComparerKeySearcher>(key, comparer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveFromBucket (ref Bucket bucket, int indexInBucket, int bucketCount) {
            Debug.Assert(bucketCount > 0);
            unchecked {
                int replacementIndexInBucket = bucketCount - 1;
                bucket.Count = (byte)replacementIndexInBucket;
                ref var toRemove = ref Unsafe.Add(ref bucket.Indices.Index0, indexInBucket);
                ref var replacement = ref Unsafe.Add(ref bucket.Indices.Index0, replacementIndexInBucket);
                // This rotate-back algorithm makes removes more expensive than if we were to just always zero the slot.
                // But then other algorithms like insertion get more expensive, since we have to search for a zero to replace...
                if (!Unsafe.AreSame(ref toRemove, ref replacement)) {
                    // TODO: This is the only place in the find/insert/remove algorithms that actually needs indexInBucket.
                    // Can we refactor it away? The good news is RyuJIT optimizes it out entirely in find/insert.
                    bucket.SetSlot((uint)indexInBucket, bucket.GetSlot(replacementIndexInBucket));
                    bucket.SetSlot((uint)replacementIndexInBucket, 0);
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>()) {
                        ref var entry = ref _Entries[toRemove];
                        entry = default;
                    }
                    toRemove = replacement;
                } else {
                    bucket.SetSlot((uint)indexInBucket, 0);
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>()) {
                        ref var entry = ref _Entries[toRemove];
                        entry = default;
                    }
                    toRemove = -1;
                }
            }
        }

        // Inlining required for acceptable codegen
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryRemove<TKeySearcher> (K key, IEqualityComparer<K>? comparer)
            where TKeySearcher : struct, IKeySearcher
        {
            var hashCode = TKeySearcher.GetHashCode(comparer, key);
            var enumerator = new LoopingBucketEnumerator(this, hashCode);
            var suffix = GetHashSuffix(hashCode);
            Span<Entry> entries = _Entries;
            do {
                int bucketCount = enumerator.bucket.Count,
                    startIndex = FindSuffixInBucket(ref enumerator.bucket, suffix, bucketCount);
                int entryIndex = TKeySearcher.FindKeyInBucket(ref enumerator.bucket, entries, startIndex, bucketCount, comparer, key, out int indexInBucket);

                if (entryIndex >= 0) {
                    _Count--;
                    ref var entry = ref entries[entryIndex];
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                        entry = default;
                    var fls = _FreeListStart;
                    entry.NextFreeSlot = fls >= 0 ? fls : FreeListIndex_EndOfFreeList;
                    _FreeListStart = entryIndex;
                    RemoveFromBucket(ref enumerator.bucket, indexInBucket, bucketCount);
                    // If we had to check multiple buckets before we found the match, go back and decrement cascade counters.
                    AdjustCascadeCounts(enumerator, false);
                    return true;
                }

                // Important: If the cascade counter is 0 and we didn't find the item, we don't want to check any other buckets.
                // Otherwise, we'd scan the whole table fruitlessly looking for a matching key.
                if (enumerator.bucket.CascadeCount == 0)
                    return false;
            } while (enumerator.Advance());

            return false;
        }

        public V this[K key] { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                int index = FindKey(key);
                if (index < 0)
                    throw new KeyNotFoundException($"Key not found: {key}");
                return _Entries[index].Value;
            }
            set {
            retry:
                var insertResult = TryInsert(key, value, InsertMode.OverwriteValue);
                switch (insertResult) {
                    case InsertResult.OkAddedNew:
                        return;
                    case InsertResult.NeedToGrow:
                        Resize(_GrowAtCount * 2);
                        goto retry;
                    case InsertResult.CorruptedInternalState:
                        throw new Exception("Corrupted internal state");
                }
            }
        }

        ICollection<K> IDictionary<K, V>.Keys => Keys;
        ICollection<V> IDictionary<K, V>.Values => Values;

        public int Count => _Count;
        public int Capacity => _GrowAtCount;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        bool IDictionary.IsFixedSize => false;

        bool IDictionary.IsReadOnly => false;

        ICollection IDictionary.Keys => Keys;

        ICollection IDictionary.Values => Values;

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        IEnumerable<K> IReadOnlyDictionary<K, V>.Keys => Keys;

        IEnumerable<V> IReadOnlyDictionary<K, V>.Values => Values;

        object? IDictionary.this[object key] {
            get => this[(K)key];
#pragma warning disable CS8600
#pragma warning disable CS8601
            set => this[(K)key] = (V)value;
#pragma warning restore CS8600
#pragma warning restore CS8601
        }

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
                    return true;
                case InsertResult.NeedToGrow:
                    Resize(_GrowAtCount * 2);
                    goto retry;
                case InsertResult.CorruptedInternalState:
                    throw new Exception("Corrupted internal state");
                default:
                    return false;
            }
        }

        void ICollection<KeyValuePair<K, V>>.Add (KeyValuePair<K, V> item) =>
            Add(item.Key, item.Value);


        // NOTE: In benchmarks this looks much slower than SCG clear, but that's because our backing array at 4096 is
        //  much bigger than SCG's, so we're just measuring how much slower Array.Clear is on a bigger array
        public void Clear () {
            if (_Count == 0)
                return;

            if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                // FIXME: Only clear occupied slots
                Array.Fill(_Entries, Entry.Empty);

            _Count = 0;
            _FreeListStart = -1;
            // We can't clear buckets with Array.Clear, because the indices have to be -1
            Array.Fill(_Buckets, Bucket.Empty);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) {
            var index = FindKey(item.Key);
            return (index >= 0) && 
                EqualityComparer<V>.Default.Equals(_Entries[index].Value, item.Value);
        }

        public bool ContainsKey (K key) =>
            FindKey(key) >= 0;

        internal struct ContainsValueCallback : IBucketCallback {
            public readonly V Value;
            public bool Result;

            public ContainsValueCallback (V value) {
                Value = value;
                Result = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Bucket (ref Bucket bucket, Span<Entry> entries) {
                // We could micro-optimize this, but we don't need to - it's already faster than SCG
                for (int j = 0; j < bucket.Count; j++) {
                    var index = bucket.Indices[j];
                    ref var entry = ref entries[index];
                    if (EqualityComparer<V>.Default.Equals(entry.Value, Value)) {
                        Result = true;
                        return false;
                    }
                }
                return true;
            }
        }

        public bool ContainsValue (V value) {
            if (_Count == 0)
                return false;

            var callback = new ContainsValueCallback(value);
            EnumerateBuckets(ref callback);
            return callback.Result;
        }

        void ICollection<KeyValuePair<K, V>>.CopyTo (KeyValuePair<K, V>[] array, int arrayIndex) {
            CopyToArray(array, arrayIndex);
        }

        public Enumerator GetEnumerator () =>
            new Enumerator(this);

        IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator () =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator () =>
            GetEnumerator();

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue (K key, out V value) {
            var index = FindKey(key);
            if (index < 0) {
                value = default!;
                return false;
            } else {
                value = _Entries[index].Value;
                return true;
            }
        }

        public object Clone () =>
            new SimdDictionary<K, V>(this);

        private struct CopyToKvp : IBucketCallback {
            public readonly KeyValuePair<K, V>[] Array;
            public int Index;

            public CopyToKvp (KeyValuePair<K, V>[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Bucket (ref Bucket bucket, Span<Entry> entries) {
                for (int j = 0; j < bucket.Count; j++) {
                    var index = bucket.Indices[j];
                    ref var entry = ref entries[index];
                    Array[Index++] = new KeyValuePair<K, V>(entry.Key, entry.Value);
                }

                return true;
            }
        }

        private struct CopyToDictionaryEntry : IBucketCallback {
            public readonly DictionaryEntry[] Array;
            public int Index;

            public CopyToDictionaryEntry (DictionaryEntry[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Bucket (ref Bucket bucket, Span<Entry> entries) {
                for (int j = 0; j < bucket.Count; j++) {
                    var index = bucket.Indices[j];
                    ref var entry = ref entries[index];
                    Array[Index++] = new DictionaryEntry(entry.Key, entry.Value);
                }

                return true;
            }
        }

        private struct CopyToObject : IBucketCallback {
            public readonly object[] Array;
            public int Index;

            public CopyToObject (object[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Bucket (ref Bucket bucket, Span<Entry> entries) {
                for (int j = 0; j < bucket.Count; j++) {
                    var index = bucket.Indices[j];
                    ref var entry = ref entries[index];
                    Array[Index++] = new KeyValuePair<K, V>(entry.Key, entry.Value);
                }

                return true;
            }
        }

        private void CopyToArray<T> (T[] array, int index) {
            if (array == null)
                throw new ArgumentNullException(nameof(array));
            if ((uint)index > (uint)array.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < Count)
                throw new ArgumentException("Destination array too small", nameof(index));

            if (array is KeyValuePair<K, V>[] kvp) {
                var c = new CopyToKvp(kvp, index);
                EnumerateBuckets(ref c);
            } else if (array is DictionaryEntry[] de) {
                var c = new CopyToDictionaryEntry(de, index);
                EnumerateBuckets(ref c);
            } else if (array is object[] o) {
                var c = new CopyToObject(o, index);
                EnumerateBuckets(ref c);
            } else
                throw new ArgumentException("Unsupported destination array type");
        }

        public void CopyTo (KeyValuePair<K, V>[] array, int index) {
            CopyToArray(array, index);
        }

        private void CopyTo (object[] array, int index) {
            CopyToArray(array, index);
        }

        void IDictionary.Add (object key, object? value) =>
#pragma warning disable CS8600
#pragma warning disable CS8604
            Add((K)key, (V)value);
#pragma warning restore CS8600
#pragma warning restore CS8604

        bool IDictionary.Contains (object key) =>
            ContainsKey((K)key);

        IDictionaryEnumerator IDictionary.GetEnumerator () =>
            new Enumerator(this);

        void IDictionary.Remove (object key) =>
            Remove((K)key);

        void ICollection.CopyTo (Array array, int index) {
            if (array is KeyValuePair<K, V>[] kvpa)
                CopyTo(kvpa, 0);
            else if (array is object[] oa)
                CopyTo(oa, 0);
            else
                throw new ArgumentException("Unsupported destination array type", nameof(array));
        }

        void ISerializable.GetObjectData (SerializationInfo info, StreamingContext context) {
            throw new NotImplementedException();
        }

        void IDeserializationCallback.OnDeserialization (object? sender) {
            throw new NotImplementedException();
        }
    }
}
