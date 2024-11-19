﻿// Performs a murmur3 finalization mix on hashcodes before using them, for collision resistance
// #define PERMUTE_HASH_CODES

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Serialization;

namespace SimdDictionary {
    internal static class SimdDictionaryHelpers<K, V>
        where K : notnull {
#pragma warning disable CA1825
        // HACK: Move this readonly field out of the dictionary type so it doesn't have a cctor.
        // This removes a cctor check from some hot paths (I think?)
        // HACK: All empty SimdDictionary instances share a single-bucket EmptyBuckets array, so that Find and Remove
        //  operations don't need to do a (_Count == 0) check. This also makes some other uses of ref and MemoryMarshal
        //  safe-by-definition instead of fragile, since we always have a valid reference to the "first" bucket, even when
        //  we're empty.
        public static readonly UnorderedDictionary<K, V>.Bucket[] EmptyBuckets = new UnorderedDictionary<K, V>.Bucket[1];
#pragma warning restore CA1825
    }

    public partial class UnorderedDictionary<K, V> : 
        IDictionary<K, V>, IDictionary, IReadOnlyDictionary<K, V>, 
        ICollection<KeyValuePair<K, V>>, ICloneable
        where K : notnull
    {
        public readonly IEqualityComparer<K>? Comparer;

        // In SCG.Dictionary, Keys and Values are on-demand-allocated classes. Here, they are on-demand-created structs.
        public KeyCollection Keys => new KeyCollection(this);
        public ValueCollection Values => new ValueCollection(this);
        // These optimize for the scenario where someone uses IDictionary.Keys or IDictionary<K, V>.Keys. Normally this
        //  would have to box the KeyCollection/ValueCollection structs on demand, so we cache the boxed version of them
        //  in these fields to get rid of the per-use allocation. Most application scenarios will never allocate these.
        private ICollection<K>? _BoxedKeys;
        private ICollection<V>? _BoxedValues;
        // Tracking only the # of empty slots makes the Count accessor more expensive, but almost everything else much cheaper
        private int _EmptySlots = 0;
        private ulong _fastModMultiplier;

        private Bucket[] _Buckets = SimdDictionaryHelpers<K, V>.EmptyBuckets;

        public UnorderedDictionary () 
            : this (InitialCapacity, null) {
        }

        public UnorderedDictionary (int capacity)
            : this (capacity, null) {
        }

        public UnorderedDictionary (IEqualityComparer<K>? comparer)
            : this (InitialCapacity, comparer) {
        }

        public UnorderedDictionary (int capacity, IEqualityComparer<K>? comparer) {
            if (typeof(K).IsValueType)
                Comparer = comparer;
            // HACK: DefaultEqualityComparer<K> for string is really bad
            else if (typeof(K) == typeof(string))
                Comparer = comparer ?? (IEqualityComparer<K>)StringComparer.Ordinal;
            else
                Comparer = comparer ?? EqualityComparer<K>.Default;            
            EnsureCapacity(capacity);
        }

        public UnorderedDictionary (UnorderedDictionary<K, V> source) {
            Comparer = source.Comparer;
            _EmptySlots = source._EmptySlots;
            _fastModMultiplier = source._fastModMultiplier;
            if (source._Buckets != SimdDictionaryHelpers<K, V>.EmptyBuckets) {
                _Buckets = new Bucket[source._Buckets.Length];
                Array.Copy(source._Buckets, _Buckets, source._Buckets.Length);
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

            int nextIncrement = (_Buckets == SimdDictionaryHelpers<K, V>.EmptyBuckets)
                ? capacity
                : Capacity * 2;

            Resize(Math.Max(capacity, nextIncrement));
        }

        internal void Resize (int capacity) {
            int bucketCount;
            ulong fastModMultiplier;

            checked {
                if (capacity < 1)
                    capacity = 1;

                bucketCount = ((capacity + BucketSizeI - 1) / BucketSizeI);

                bucketCount = bucketCount > 1 ? HashHelpers.GetPrime(bucketCount) : 1;
                fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)bucketCount);
            }

            var actualCapacity = bucketCount * BucketSizeI;
            var oldBuckets = _Buckets;
            var oldCount = Count;

            // Allocate new array before updating fields so that we don't get corrupted when running out of memory
            var newBuckets = new Bucket[bucketCount];
            _Buckets = newBuckets;
            // HACK: Ensure we store a new larger bucket array before storing the fastModMultiplier for the larger size.
            // This ensures that concurrent modification will not produce a bucket index that is too big.
            Thread.MemoryBarrier();
            _EmptySlots = actualCapacity - oldCount;
            _fastModMultiplier = fastModMultiplier;

            // FIXME: In-place rehashing
            if (oldBuckets != SimdDictionaryHelpers<K, V>.EmptyBuckets) {
                var c = new RehashCallback(this);
                EnumeratePairs(oldBuckets, ref c);
            }
        }

        internal readonly struct RehashCallback : IPairCallback {
            public readonly UnorderedDictionary<K, V> Self;

            public RehashCallback (UnorderedDictionary<K, V> self) {
                Self = self;
            }

            public bool Pair (ref Pair pair) {
                if (Self.TryInsert(pair.Key, pair.Value, InsertMode.Rehashing) != InsertResult.OkAddedNew)
                    ThrowCorrupted();
                return true;
            }
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
            var result = unchecked((byte)hashCode);
            // Assuming the JIT turns this into a cmov, this should be better on average
            //  since it nearly doubles the number of possible suffixes, improving collision
            //  resistance and reducing the odds of having to check multiple keys.
            return result == 0 ? (byte)255 : result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int BucketIndexForHashCode (uint hashCode, Span<Bucket> buckets) =>
            // NOTE: If the caller observes a new _fastModMultiplier before seeing a larger buckets array,
            //  this can overrun the end of the array.
            unchecked((int)HashHelpers.FastMod(
                hashCode, (uint)buckets.Length, 
                // Volatile.Read to ensure that the load of _fastModMultiplier can't get moved before load of _Buckets
                // This doesn't appear to generate a memory barrier or anything.
                Volatile.Read(ref _fastModMultiplier)
            ));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref Pair FindKey (K key) {
            var comparer = Comparer;
            if (typeof(K).IsValueType && (comparer == null))
                return ref FindKey<DefaultComparerKeySearcher>(key, null);
            else
                return ref FindKey<ComparerKeySearcher>(key, comparer);
        }

        // Performance is much worse unless this method is inlined, I'm not sure why.
        // If we disable inlining for it, our generated code size is roughly halved.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref Pair FindKey<TKeySearcher> (K key, IEqualityComparer<K>? comparer)
            where TKeySearcher : struct, IKeySearcher 
        {
            var hashCode = TKeySearcher.GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            // We eagerly create the search vector here before we need it, because in many cases it would get LICM'd here
            //  anyway. On some architectures create's latency is very low but on others it isn't, so on average it is better
            //  to put it outside of the loop.
            var searchVector = Vector128.Create(suffix);
            var enumerator = new LoopingBucketEnumerator(this, hashCode);
            do {
                // Eagerly load the bucket count early for pipelining purposes, so we don't stall when using it later.
                int bucketCount = enumerator.bucket.Count, 
                    startIndex = FindSuffixInBucket(ref enumerator.bucket, searchVector, bucketCount);
                ref var pair = ref TKeySearcher.FindKeyInBucket(ref enumerator.bucket, startIndex, bucketCount, comparer, key, out _);
                if (Unsafe.IsNullRef(ref pair)) {
                    if (enumerator.bucket.CascadeCount == 0)
                        return ref Unsafe.NullRef<Pair>();
                } else
                    return ref pair;
            } while (enumerator.Advance(this));

            return ref Unsafe.NullRef<Pair>();
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
        internal static bool TryInsertIntoBucket (ref Bucket bucket, byte suffix, int bucketCount, K key, V value) {
            if (bucketCount >= BucketSizeI)
                return false;

            unchecked {
                ref var destination = ref Unsafe.Add(ref bucket.Pairs.Pair0, bucketCount);
                bucket.Count = (byte)(bucketCount + 1);
                bucket.SetSlot((nuint)bucketCount, suffix);
                destination.Key = key;
                destination.Value = value;
            }

            return true;
        }

        // Inlining required for acceptable codegen
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InsertResult TryInsert<TKeySearcher> (K key, V value, InsertMode mode, IEqualityComparer<K>? comparer) 
            where TKeySearcher : struct, IKeySearcher 
        {
            var needToGrow = _EmptySlots <= 0;
            var hashCode = TKeySearcher.GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var searchVector = Vector128.Create(suffix);
            // Pipelining: Perform the actual branch later, since in the common case we won't need to grow.
            if (needToGrow)
                return InsertResult.NeedToGrow;
            var enumerator = new LoopingBucketEnumerator(this, hashCode);
            do {
                int bucketCount = enumerator.bucket.Count;
                if (mode != InsertMode.Rehashing) {
                    int startIndex = FindSuffixInBucket(ref enumerator.bucket, searchVector, bucketCount);
                    ref var pair = ref TKeySearcher.FindKeyInBucket(ref enumerator.bucket, startIndex, bucketCount, comparer, key, out _);

                    if (!Unsafe.IsNullRef(ref pair)) {
                        if (mode == InsertMode.EnsureUnique)
                            return InsertResult.KeyAlreadyPresent;
                        else {
                            pair.Value = value;
                            return InsertResult.OkOverwroteExisting;
                        }
                    } else if (startIndex < BucketSizeI) {
                        // FIXME: Suffix collision. Track these for string rehashing anti-DoS mitigation!
                    }
                }

                if (TryInsertIntoBucket(ref enumerator.bucket, suffix, bucketCount, key, value)) {
                    // Increase the cascade counters for the buckets we checked before this one.
                    AdjustCascadeCounts(enumerator, true);

                    return InsertResult.OkAddedNew;
                }
            } while (enumerator.Advance(this));

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
        internal static void RemoveFromBucket (ref Bucket bucket, int indexInBucket, int bucketCount, ref Pair toRemove) {
            Debug.Assert(bucketCount > 0);
            unchecked {
                int replacementIndexInBucket = bucketCount - 1;
                bucket.Count = (byte)replacementIndexInBucket;
                ref var replacement = ref Unsafe.Add(ref bucket.Pairs.Pair0, replacementIndexInBucket);
                // This rotate-back algorithm makes removes more expensive than if we were to just always zero the slot.
                // But then other algorithms like insertion get more expensive, since we have to search for a zero to replace...
                if (!Unsafe.AreSame(ref toRemove, ref replacement)) {
                    // TODO: This is the only place in the find/insert/remove algorithms that actually needs indexInBucket.
                    // Can we refactor it away? The good news is RyuJIT optimizes it out entirely in find/insert.
                    bucket.SetSlot((uint)indexInBucket, bucket.GetSlot(replacementIndexInBucket));
                    bucket.SetSlot((uint)replacementIndexInBucket, 0);
                    toRemove = replacement;
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Pair>())
                        replacement = default;
                } else {
                    bucket.SetSlot((uint)indexInBucket, 0);
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Pair>())
                        toRemove = default;
                }
            }
        }

        // Inlining required for acceptable codegen
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryRemove<TKeySearcher> (K key, IEqualityComparer<K>? comparer)
            where TKeySearcher : struct, IKeySearcher
        {
            var hashCode = TKeySearcher.GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var searchVector = Vector128.Create(suffix);
            var enumerator = new LoopingBucketEnumerator(this, hashCode);
            do {
                int bucketCount = enumerator.bucket.Count,
                    startIndex = FindSuffixInBucket(ref enumerator.bucket, searchVector, bucketCount);
                ref var pair = ref TKeySearcher.FindKeyInBucket(ref enumerator.bucket, startIndex, bucketCount, comparer, key, out int indexInBucket);

                if (!Unsafe.IsNullRef(ref pair)) {
                    _EmptySlots++;
                    RemoveFromBucket(ref enumerator.bucket, indexInBucket, bucketCount, ref pair);
                    // If we had to check multiple buckets before we found the match, go back and decrement cascade counters.
                    AdjustCascadeCounts(enumerator, false);
                    return true;
                }

                // Important: If the cascade counter is 0 and we didn't find the item, we don't want to check any other buckets.
                // Otherwise, we'd scan the whole table fruitlessly looking for a matching key.
                if (enumerator.bucket.CascadeCount == 0)
                    return false;
            } while (enumerator.Advance(this));

            return false;
        }

        public V this[K key] { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                ref var pair = ref FindKey(key);
                if (Unsafe.IsNullRef(ref pair))
                    throw new KeyNotFoundException($"Key not found: {key}");
                return pair.Value;
            }
            set {
            retry:
                var insertResult = TryInsert(key, value, InsertMode.OverwriteValue);
                switch (insertResult) {
                    case InsertResult.OkAddedNew:
                        _EmptySlots--;
                        return;
                    case InsertResult.NeedToGrow:
                        EnsureCapacity(Capacity + 1);
                        goto retry;
                    case InsertResult.CorruptedInternalState:
                        throw new Exception("Corrupted internal state");
                }
            }
        }

        ICollection<K> IDictionary<K, V>.Keys => (_BoxedKeys ??= Keys);
        ICollection<V> IDictionary<K, V>.Values => (_BoxedValues ??= Values);

        public int Count => Capacity - _EmptySlots;
        public int Capacity => (_Buckets == SimdDictionaryHelpers<K, V>.EmptyBuckets) 
            ? 0 
            : _Buckets.Length * BucketSizeI;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        bool IDictionary.IsFixedSize => false;

        bool IDictionary.IsReadOnly => false;

        ICollection IDictionary.Keys => (ICollection)(_BoxedKeys ??= Keys);

        ICollection IDictionary.Values => (ICollection)(_BoxedValues ??= Values);

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        IEnumerable<K> IReadOnlyDictionary<K, V>.Keys => (_BoxedKeys ??= Keys);

        IEnumerable<V> IReadOnlyDictionary<K, V>.Values => (_BoxedValues ??= Values);

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
                    _EmptySlots--;
                    return true;
                case InsertResult.NeedToGrow:
                    EnsureCapacity(Capacity + 1);
                    goto retry;
                case InsertResult.CorruptedInternalState:
                    throw new Exception("Corrupted internal state");
                default:
                    return false;
            }
        }

        void ICollection<KeyValuePair<K, V>>.Add (KeyValuePair<K, V> item) =>
            Add(item.Key, item.Value);

        internal readonly struct ClearCallback : IBucketCallback {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Bucket (ref Bucket bucket) {
                int c = bucket.Count;
                if (c == 0) {
                    bucket.CascadeCount = 0;
                    return true;
                }

                bucket.Suffixes = default;
                if (RuntimeHelpers.IsReferenceOrContainsReferences<Pair>()) {
                    // FIXME: Performs a method call for the clear instead of being inlined
                    // var pairs = (Span<Pair>)bucket.Pairs;
                    // pairs.Clear();
                    ref var pair = ref bucket.Pairs.Pair0;
                    // 4-wide unrolled bucket clear
                    while (c >= 4) {
                        pair = default;
                        Unsafe.Add(ref pair, 1) = default;
                        Unsafe.Add(ref pair, 2) = default;
                        Unsafe.Add(ref pair, 3) = default;
                        pair = ref Unsafe.Add(ref pair, 4);
                        c -= 4;
                    }
                    while (c != 0) {
                        pair = default;
                        pair = ref Unsafe.Add(ref pair, 1);
                        c--;
                    }
                }

                return true;
            }
        }

        // NOTE: In benchmarks this looks much slower than SCG clear, but that's because our backing array at 4096 is
        //  much bigger than SCG's, so we're just measuring how much slower Array.Clear is on a bigger array
        public void Clear () {
            if (Count == 0)
                return;

            _EmptySlots = Capacity;
            // FIXME: Only do this if _Count is below say 0.5x?
            var c = new ClearCallback();
            EnumerateBuckets(_Buckets, ref c);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) {
            ref var pair = ref FindKey(item.Key);
            return !Unsafe.IsNullRef(ref pair) && (pair.Value?.Equals(item.Value) == true);
        }

        public bool ContainsKey (K key) =>
            !Unsafe.IsNullRef(ref FindKey(key));

        internal struct ContainsValueCallback : IPairCallback {
            public readonly V Value;
            public bool Result;

            public ContainsValueCallback (V value) {
                Value = value;
                Result = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Pair (ref Pair pair) {
                if (EqualityComparer<V>.Default.Equals(pair.Value, Value)) {
                    Result = true;
                    return false;
                }
                return true;
            }
        }

        public bool ContainsValue (V value) {
            if (Count == 0)
                return false;

            var callback = new ContainsValueCallback(value);
            EnumeratePairs(_Buckets, ref callback);
            return callback.Result;
        }

        internal struct ForEachImpl : IPairCallback {
            private readonly ForEachCallback Callback;
            private int Index;

            public ForEachImpl (ForEachCallback callback) {
                Callback = callback;
                Index = 0;
            }

            public bool Pair (ref Pair pair) {
                return Callback(Index++, in pair.Key, ref pair.Value);
            }
        }

        public void ForEach (ForEachCallback callback) {
            var state = new ForEachImpl(callback);
            EnumeratePairs(_Buckets, ref state);
        }

        void ICollection<KeyValuePair<K, V>>.CopyTo (KeyValuePair<K, V>[] array, int arrayIndex) {
            CopyToArray(array, arrayIndex);
        }

        public Enumerator GetEnumerator () =>
            new Enumerator(this);

        public RefEnumerator GetRefEnumerator () =>
            new RefEnumerator(this);

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
            ref var pair = ref FindKey(key);
            if (Unsafe.IsNullRef(ref pair)) {
                value = default!;
                return false;
            } else {
                value = pair.Value;
                return true;
            }
        }

        // Inlining required for disasmo
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly V GetValueRefOrNullRef (K key) {
            ref var pair = ref FindKey(key);
            if (Unsafe.IsNullRef(ref pair))
                return ref Unsafe.NullRef<V>();
            return ref pair.Value;
        }

        public V AddOrUpdate (K key, V addValue, Func<K, V, V> updateValueFactory) {
            // FIXME: Write a dedicated implementation that works in a single pass, based on TryInsert?
            ref var pair = ref FindKey(key);
            if (Unsafe.IsNullRef(ref pair)) {
                if (TryInsert(key, addValue, InsertMode.EnsureUnique) != InsertResult.OkAddedNew)
                    ThrowConcurrentModification();
                return addValue;
            } else {
                return pair.Value = updateValueFactory(key, pair.Value);
            }
        }

        public V GetOrAdd (K key, Func<K, V> valueFactory) {
            // FIXME: Write a dedicated implementation that works in a single pass, based on TryInsert?
            ref var pair = ref FindKey(key);
            if (Unsafe.IsNullRef(ref pair)) {
                var value = valueFactory(key);
                TryInsert(key, value, InsertMode.GetOrAdd);
                return value;
            } else
                return pair.Value;
        }

        public object Clone () =>
            new UnorderedDictionary<K, V>(this);

        private struct CopyToKvp : IPairCallback {
            public readonly KeyValuePair<K, V>[] Array;
            public int Index;

            public CopyToKvp (KeyValuePair<K, V>[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Pair (ref Pair pair) {
                Array[Index++] = new KeyValuePair<K, V>(pair.Key, pair.Value);
                return true;
            }
        }

        private struct CopyToDictionaryEntry : IPairCallback {
            public readonly DictionaryEntry[] Array;
            public int Index;

            public CopyToDictionaryEntry (DictionaryEntry[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Pair (ref Pair pair) {
                Array[Index++] = new DictionaryEntry(pair.Key, pair.Value);
                return true;
            }
        }

        private struct CopyToObject : IPairCallback {
            public readonly object[] Array;
            public int Index;

            public CopyToObject (object[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Pair (ref Pair pair) {
                Array[Index++] = new KeyValuePair<K, V>(pair.Key, pair.Value);
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
                EnumeratePairs(_Buckets, ref c);
            } else if (array is DictionaryEntry[] de) {
                var c = new CopyToDictionaryEntry(de, index);
                EnumeratePairs(_Buckets, ref c);
            } else if (array is object[] o) {
                var c = new CopyToObject(o, index);
                EnumeratePairs(_Buckets, ref c);
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

        public bool TryGetAlternateLookup<TAlternateKey> (out AlternateLookup<TAlternateKey> result)
            where TAlternateKey : notnull, allows ref struct 
        {
            if (Comparer is IAlternateEqualityComparer<TAlternateKey, K> aec) {
                result = new AlternateLookup<TAlternateKey>(this, aec);
                return true;
            }

            result = default;
            return false;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void ThrowInvalidOperation () {
            throw new InvalidOperationException();
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void ThrowCorrupted () {
            throw new Exception("Corrupted dictionary internal state detected");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void ThrowConcurrentModification () {
            throw new Exception("Concurrent modification of dictionary detected");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static void ThrowKeyNotFound () {
            throw new KeyNotFoundException();
        }
    }
}
