﻿// This is considerably slower than power-of-two bucket counts, but it provides
//  much better collision resistance than power-of-two bucket counts do. However,
// Murmur3 finalization + Power-of-two bucket counts (see below) performs much better and has
//  higher collision resistance due to improving the quality of suffixes
// #define PRIME_BUCKET_COUNTS
// Performs a murmur3 finalization mix on hashcodes before using them, for collision resistance
// #define PERMUTE_HASH_CODES
// Force disables the vectorized suffix search implementations so you can test/benchmark the scalar one
// #define FORCE_SCALAR_IMPLEMENTATION
// Walk buckets instead of Array.Clear. Improves clear performance when mostly empty, slight regression when full
#define SMART_CLEAR

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace SimdDictionary {
    public partial class SimdDictionary<K, V> : 
        IDictionary<K, V>, IDictionary, IReadOnlyDictionary<K, V>, 
        ICollection<KeyValuePair<K, V>>, ICloneable, ISerializable, IDeserializationCallback
        where K : notnull
    {
#if PRIME_BUCKET_COUNTS
        private ulong _fastModMultiplier;
#endif

        public readonly KeyCollection Keys;
        public readonly ValueCollection Values;
        public readonly IEqualityComparer<K>? Comparer;
        private int _Count, _GrowAtCount;

#pragma warning disable CA1825
        private static readonly Bucket[] EmptyBuckets = [];
        private static readonly Entry[] EmptyEntries = [];
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            else if (capacity == 0)
                return;

            if (Capacity >= capacity)
                return;

            int nextIncrement = (_Buckets.Length == 0)
                ? capacity
                : Capacity * 2;

            Resize(Math.Max(capacity, nextIncrement));
        }

        internal void Resize (int capacity) {
            Debug.Assert(BucketSizeI == BucketSizeU);

            int bucketCount;
            checked {
                capacity = (int)((long)capacity * OversizePercentage / 100);
                if (capacity < 1)
                    capacity = 1;

                bucketCount = ((capacity + BucketSizeI - 1) / BucketSizeI);

#if PRIME_BUCKET_COUNTS
                bucketCount = HashHelpers.GetPrime(capacity);
                _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)bucketCount);
#else
                // Power-of-two bucket counts enable using & (count - 1) instead of mod count
                bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)bucketCount);
#endif
            }

            var actualCapacity = bucketCount * BucketSizeI;
            var oldBuckets = _Buckets;
            var oldEntries = _Entries;
            checked {
                _GrowAtCount = (int)(((long)actualCapacity) * 100 / OversizePercentage);
            }

            // Allocate both new arrays before updating the fields so that we don't get corrupted
            //  when running out of memory
            // HACK: Allocate an extra entry slot at the end so that we can safely do a trick when scanning keys
            var newEntries = new Entry[actualCapacity + 1];
            var newBuckets = new Bucket[bucketCount];
            Thread.MemoryBarrier();
            // Ensure that when growing we store the new bigger entries array before storing the new
            //  bigger buckets array, so that other threads will never observe an entries array that
            //  is too small.
            _Entries = newEntries;
            Thread.MemoryBarrier();
            _Buckets = newBuckets;
            // FIXME: In-place rehashing
            if ((oldBuckets.Length > 0) && (oldEntries.Length > 0) && (_Count > 0))
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
                    if (TryInsert(bucket.Keys[j], entry.Value, InsertMode.Rehashing) != InsertResult.OkAddedNew)
                        return false;
                }
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
            unchecked((int)HashHelpers.FastMod(hashCode, (uint)buckets.Length, _fastModMultiplier));
#else
            unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe int FindSuffixInBucket (ref Bucket bucket, byte suffix) {
#if !FORCE_SCALAR_IMPLEMENTATION
            if (Sse2.IsSupported) {
                // FIXME: It would be nice to precompute the search vector outside of the loop, to hide the latency of vpbroadcastb.
                // Right now if we do that, ryujit places the search vector in xmm6 which forces stack spills, and that's worse.
                // So we have to compute it on-demand here. On modern x86-64 chips the latency of vpbroadcastb is 1, at least.
                return BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.CompareEqual(Vector128.Create(suffix), bucket.Suffixes)));
            } else if (AdvSimd.Arm64.IsSupported) {
                // Completely untested
                var laneBits = AdvSimd.And(
                    AdvSimd.CompareEqual(Vector128.Create(suffix), bucket.Suffixes), 
                    Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128)
                );
                var moveMask = AdvSimd.Arm64.AddAcross(laneBits.GetLower()).ToScalar() |
                    (AdvSimd.Arm64.AddAcross(laneBits.GetUpper()).ToScalar() << 8);
                return BitOperations.TrailingZeroCount(moveMask);
            } else if (PackedSimd.IsSupported) {
                // Completely untested
                return BitOperations.TrailingZeroCount(PackedSimd.Bitmask(PackedSimd.CompareEqual(Vector128.Create(suffix), bucket.Suffixes)));
            } else {
#else
            {
#endif
                var haystack = (byte*)Unsafe.AsPointer(ref bucket);
                // FIXME: Hand-unrolling into a chain of cmovs like in dn_simdhash doesn't work.
                for (int i = 0, c = bucket.GetSlot(CountSlot); i < c; i++) {
                    if (haystack[i] == suffix)
                        return i;
                }
                return 32;
            }
        }

#pragma warning disable CS8619
        // These have to be structs so that the JIT will specialize callers instead of Canonizing them
        internal struct DefaultComparerKeySearcher : IKeySearcher {

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
                return FinalizeHashCode(unchecked((uint)EqualityComparer<K>.Default.GetHashCode(key!)));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ref Entry FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization of the key reference below.
                [UnscopedRef] ref Bucket bucket, [UnscopedRef] ref Entry firstEntryInBucket, 
                int indexInBucket, IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            ) {
                Debug.Assert(indexInBucket >= 0);

                int count = bucket.GetSlot(CountSlot);
                // It might be faster on some targets to early-out before the address computation(s) below
                //  by doing a direct comparison between indexInBucket and count. In my local testing, it's not faster,
                //  and this implementation generates smaller code
                if (typeof(K).IsValueType) {
                    // It's impossible to properly initialize this reference until indexInBucket has been range-checked.
                    ref var key = ref Unsafe.NullRef<K>();
                    for (; indexInBucket < count; indexInBucket++, key = ref Unsafe.Add(ref key, 1)) {
                        // It might be good to find a way to compile this down to a cmov instead of the current branch
                        if (Unsafe.IsNullRef(ref key))
                            key = ref Unsafe.Add(ref Unsafe.As<InlineKeyArray, K>(ref bucket.Keys), indexInBucket);
                        if (EqualityComparer<K>.Default.Equals(needle, key)) {
                            matchIndexInBucket = indexInBucket;
                            return ref Unsafe.Add(ref firstEntryInBucket, indexInBucket);
                        }
                    }
                } else {
                    Environment.FailFast("FindKeyInBucketWithDefaultComparer called for non-struct key type");
                }

                matchIndexInBucket = -1;
                return ref Unsafe.NullRef<Entry>();
            }
        }

        internal struct ComparerKeySearcher : IKeySearcher {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
                return FinalizeHashCode(unchecked((uint)comparer!.GetHashCode(key!)));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ref Entry FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization of the key reference below.
                [UnscopedRef] ref Bucket bucket, [UnscopedRef] ref Entry firstEntryInBucket, 
                int indexInBucket, IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            ) {
                Debug.Assert(indexInBucket >= 0);
                Debug.Assert(comparer != null);

                int count = bucket.GetSlot(CountSlot);
                // It might be faster on some targets to early-out before the address computation(s) below
                //  by doing a direct comparison between indexInBucket and count. In my local testing, it's not faster,
                //  and this implementation generates smaller code

                // It's impossible to properly initialize this reference until indexInBucket has been range-checked.
                ref var key = ref Unsafe.NullRef<K>();
                for (; indexInBucket < count; indexInBucket++, key = ref Unsafe.Add(ref key, 1)) {
                    if (Unsafe.IsNullRef(ref key))
                        key = ref Unsafe.Add(ref Unsafe.As<InlineKeyArray, K>(ref bucket.Keys), indexInBucket);
                    if (comparer!.Equals(needle, key)) {
                        matchIndexInBucket = indexInBucket;
                        return ref Unsafe.Add(ref firstEntryInBucket, indexInBucket);
                    }
                }

                matchIndexInBucket = -1;
                return ref Unsafe.NullRef<Entry>();
            }
        }
#pragma warning restore CS8619

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref Entry FindKey (K key) {
            var comparer = Comparer;
            if (typeof(K).IsValueType && (comparer == null))
                return ref FindKey<DefaultComparerKeySearcher>(key, null);
            else
                return ref FindKey<ComparerKeySearcher>(key, comparer);
        }

        // Performance is much worse unless this method is inlined, I'm not sure why.
        // If we disable inlining for it, our generated code size is roughly halved.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref Entry FindKey<TKeySearcher> (K key, IEqualityComparer<K>? comparer)
            where TKeySearcher : struct, IKeySearcher 
        {
            // If count is 0 our buckets/entries might be an empty array, which would make 'buckets.Length - 1' produce -1 below,
            //  so we need to bail out early for an empty collection.
            if (_Count == 0)
                return ref Unsafe.NullRef<Entry>();

            var hashCode = TKeySearcher.GetHashCode(comparer, key);

            var buckets = (Span<Bucket>)_Buckets;
            // The entries array can only ever grow, not shrink, so concurrent modification will at-worst cause us to get an
            //  entries array that is too big for the buckets array we've captured above. This is ensured by a barrier in Resize
            var entries = (Span<Entry>)_Entries;
            // We don't need to store/update the current bucket index for find operations unlike insert/remove operations,
            //  because we never need to use it for cascade counter cleanup. Removing the index makes the scan loop a bit simpler
            var initialBucketIndex = BucketIndexForHashCode(hashCode, buckets);
            // Unfortunately this eats a valuable register and as a result something gets spilled to the stack :( Still probably
            //  better than having to save/restore xmm6 every time, though.
            // I tested storing the suffix in a Vector128 to try and use a vector register, but RyuJIT assigns it to xmm6. Not sure
            //  why *that's* happening since the vector isn't being passed to anything, maybe it's something to do with the method
            //  calls for the comparer, etc, and the problem is that the vector has to live across method calls so it picks a nvreg.
            var suffix = GetHashSuffix(hashCode);
            // RyuJIT will assign this to xmm6 (a nonvolatile register) because of the potential for this method to call other
            //  methods, which requires the existing value of xmm6 to spill to the stack upon method entry. As such, it's better
            //  to construct the search vector on every call to FindSuffixInBucket instead. This trades improved performance on
            //  the common case (single-bucket search and then return) for slightly worse performance when cascaded
            // var searchVector = Vector128.Create(suffix);

            Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);

            // Null-initialize these refs and then initialize them on demand only if we have to check multiple buckets.
            // This increases code size a bit, but moves a bunch of code off of the hot path.
            ref Bucket lastBucket = ref Unsafe.NullRef<Bucket>(),
                initialBucket = ref Unsafe.NullRef<Bucket>(),
                // We can use Unsafe.Add here because we know these two indices are already within bounds;
                //  the first is calculated by BucketIndexForHashCode (either masked with & or modulus),
                //  and the second is buckets.Length - 1, so it can never be out of range.
                bucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), initialBucketIndex);
            // Intentionally bounds-check this entry index (do we need to, though?)
            ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

            do {
                int startIndex = FindSuffixInBucket(ref bucket, suffix);
                // Checking whether startIndex < 32 would theoretically make this faster, but in practice, it doesn't
                // Using a cmov to conditionally perform the count GetSlot also isn't faster
                ref var entry = ref TKeySearcher.FindKeyInBucket(ref bucket, ref firstBucketEntry, startIndex, comparer, key, out _);
                if (Unsafe.IsNullRef(ref entry)) {
                    if (bucket.GetSlot(CascadeSlot) == 0)
                        return ref Unsafe.NullRef<Entry>();
                } else
                    return ref entry;

                // We're checking multiple buckets, so initialize the loop control variables
                if (Unsafe.IsNullRef(ref initialBucket)) {
                    initialBucket = ref bucket;
                    lastBucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), buckets.Length - 1);
                }

                if (Unsafe.AreSame(ref bucket, ref lastBucket)) {
                    // If we used GetArrayDataReference here, it would allow buckets/entries to expire and shrink our stack frame by 16
                    //  bytes. But then we'd be exposed to corruption from concurrent accesses, since the underlying arrays could change.
                    // Doing that doesn't seem to actually improve the generated code at all either, despite the smaller stack frame.
                    bucket = ref MemoryMarshal.GetReference(buckets);
                    firstBucketEntry = ref MemoryMarshal.GetReference(entries);
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                }
            } while (!Unsafe.AreSame(ref bucket, ref initialBucket));

            return ref Unsafe.NullRef<Entry>();
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
        internal static bool TryInsertIntoBucket (ref Bucket bucket, ref Entry firstBucketEntry, byte suffix, K key, V value) {
            byte bucketCount = bucket.GetSlot(CountSlot);
            if (bucketCount >= BucketSizeU)
                return false;

            unchecked {
                ref var newKey = ref Unsafe.Add(ref Unsafe.As<InlineKeyArray, K>(ref bucket.Keys), bucketCount);
                ref var entry = ref Unsafe.Add(ref firstBucketEntry, bucketCount);
                bucket.SetSlot(CountSlot, (byte)(bucketCount + 1));
                bucket.SetSlot(bucketCount, suffix);
                newKey = key;
                entry = new Entry(value);
            }

            return true;
        }

        // This is a slow path that only runs when a bucket overflows, so we don't need to inline it.
        // Inlining it improves remove/insert performance on average a bit though, for remove the difference is:
        // 1.0244x (inlined) vs 1.0991x (not inlined)
        // The code size improvement from not inlining it appears to be quite small (930 bytes vs 933), so for now it's inlined.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void AdjustCascadeCounts (
            Span<Bucket> buckets, ref Bucket bucket, ref Bucket initialBucket, ref Bucket lastBucket,
            bool increase
        ) {
            ref var firstBucket = ref MemoryMarshal.GetReference(buckets);
            // We may have cascaded out of a previous bucket; if so, scan backwards and update
            //  the cascade count for every bucket we previously scanned.
            while (!Unsafe.AreSame(ref bucket, ref initialBucket)) {
                // FIXME: Track number of times we cascade out of a bucket for string rehashing anti-DoS mitigation!
                bucket = ref Unsafe.Subtract(ref bucket, 1);
                if (Unsafe.IsAddressLessThan(ref bucket, ref firstBucket))
                    bucket = ref lastBucket;

                var cascadeCount = bucket.GetSlot(CascadeSlot);
                if (increase) {
                    // Never overflow (wrap around) the counter
                    if (cascadeCount < 255)
                        bucket.SetSlot(CascadeSlot, (byte)(cascadeCount + 1));
                } else {
                    if (cascadeCount == 0)
                        Environment.FailFast("Corrupted dictionary bucket cascade slot");
                    // If the cascade counter hit 255, it's possible the actual cascade count through here is >255,
                    //  so it's no longer safe to decrement. This is a very rare scenario, but it permanently degrades the table.
                    // TODO: Track this and triggering a rehash once too many buckets are in this state + dict is mostly empty.
                    else if (cascadeCount < 255)
                        bucket.SetSlot(CascadeSlot, (byte)(cascadeCount - 1));
                }
            }
        }

        // Inlining required for acceptable codegen
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InsertResult TryInsert<TKeySearcher> (K key, V value, InsertMode mode, IEqualityComparer<K>? comparer) 
            where TKeySearcher : struct, IKeySearcher 
        {
            // This duplicates a lot of logic from FindKey, so I won't replicate its comments here.
            if (_Count >= _GrowAtCount)
                return InsertResult.NeedToGrow;

            var hashCode = TKeySearcher.GetHashCode(comparer, key);

            var buckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            var initialBucketIndex = BucketIndexForHashCode(hashCode, buckets);
            var suffix = GetHashSuffix(hashCode);

            Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);

            ref Bucket lastBucket = ref Unsafe.NullRef<Bucket>(),
                initialBucket = ref Unsafe.NullRef<Bucket>(),
                bucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), initialBucketIndex);
            ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

            do {
                // start insert logic

                if (mode != InsertMode.Rehashing) {
                    int startIndex = FindSuffixInBucket(ref bucket, suffix);
                    ref var entry = ref TKeySearcher.FindKeyInBucket(ref bucket, ref firstBucketEntry, startIndex, comparer, key, out _);

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

                if (TryInsertIntoBucket(ref bucket, ref firstBucketEntry, suffix, key, value)) {
                    // If the loop control variables are populated, that means we had to try to insert into multiple buckets.
                    // Increase the cascade counters for the buckets we checked before this one.
                    if (!Unsafe.IsNullRef(ref initialBucket))
                        AdjustCascadeCounts(buckets, ref bucket, ref initialBucket, ref lastBucket, true);

                    return InsertResult.OkAddedNew;
                }

                // end insert logic

                if (Unsafe.IsNullRef(ref initialBucket)) {
                    initialBucket = ref bucket;
                    lastBucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), buckets.Length - 1);
                }

                if (Unsafe.AreSame(ref bucket, ref lastBucket)) {
                    bucket = ref MemoryMarshal.GetReference(buckets);
                    firstBucketEntry = ref MemoryMarshal.GetReference(entries);
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                }
            } while (!Unsafe.AreSame(ref bucket, ref initialBucket));

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
        internal static void RemoveFromBucket (ref Bucket bucket, int indexInBucket, ref Entry entry, ref Entry firstBucketEntry) {
            var bucketCount = bucket.GetSlot(CountSlot);
            Debug.Assert(bucketCount > 0);
            unchecked {
                ref var newKey = ref Unsafe.Add(ref Unsafe.As<InlineKeyArray, K>(ref bucket.Keys), indexInBucket);
                int replacementIndexInBucket = bucketCount - 1;
                bucket.SetSlot(CountSlot, (byte)replacementIndexInBucket);
                if (indexInBucket != replacementIndexInBucket) {
                    bucket.SetSlot((uint)indexInBucket, bucket.GetSlot(replacementIndexInBucket));
                    bucket.SetSlot((uint)replacementIndexInBucket, 0);
                    ref var replacementKey = ref Unsafe.Add(ref Unsafe.As<InlineKeyArray, K>(ref bucket.Keys), replacementIndexInBucket);
                    ref var replacementEntry = ref Unsafe.Add(ref firstBucketEntry, replacementIndexInBucket);
                    entry = replacementEntry;
                    newKey = replacementKey;
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<K>())
                        replacementKey = default!;
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                        replacementEntry = default;
                } else {
                    bucket.SetSlot((uint)indexInBucket, 0);
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<K>())
                        newKey = default!;
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                        entry = default;
                }
            }
        }

        // Inlining required for acceptable codegen
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryRemove<TKeySearcher> (K key, IEqualityComparer<K>? comparer)
            where TKeySearcher : struct, IKeySearcher
        {
            // This duplicates a lot of logic from FindKey, so I won't replicate its comments here.
            if (_Count <= 0)
                return false;

            var hashCode = TKeySearcher.GetHashCode(comparer, key);

            var buckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            var initialBucketIndex = BucketIndexForHashCode(hashCode, buckets);
            var suffix = GetHashSuffix(hashCode);

            Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);

            ref Bucket lastBucket = ref Unsafe.NullRef<Bucket>(),
                initialBucket = ref Unsafe.NullRef<Bucket>(),
                bucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), initialBucketIndex);
            ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

            do {
                // start remove logic

                int startIndex = FindSuffixInBucket(ref bucket, suffix);
                ref var entry = ref TKeySearcher.FindKeyInBucket(ref bucket, ref firstBucketEntry, startIndex, comparer, key, out int indexInBucket);

                if (!Unsafe.IsNullRef(ref entry)) {
                    _Count--;
                    RemoveFromBucket(ref bucket, indexInBucket, ref entry, ref firstBucketEntry);
                    // If the loop control variables are populated, we had to scan multiple buckets to find the item we just
                    //  removed, so go back and decrement the cascade counters.
                    if (!Unsafe.IsNullRef(ref initialBucket))
                        AdjustCascadeCounts(buckets, ref bucket, ref initialBucket, ref lastBucket, false);
                    return true;
                }

                // Important: If the cascade counter is 0 and we didn't find the item, we don't want to check any other buckets.
                // Otherwise, we'd scan the whole table fruitlessly looking for a matching key.
                if (bucket.GetSlot(CascadeSlot) == 0)
                    return false;

                // end remove logic

                if (Unsafe.IsNullRef(ref initialBucket)) {
                    initialBucket = ref bucket;
                    lastBucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), buckets.Length - 1);
                }

                if (Unsafe.AreSame(ref bucket, ref lastBucket)) {
                    bucket = ref MemoryMarshal.GetReference(buckets);
                    firstBucketEntry = ref MemoryMarshal.GetReference(entries);
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    firstBucketEntry = ref Unsafe.Add(ref firstBucketEntry, BucketSizeI);
                }
            } while (!Unsafe.AreSame(ref bucket, ref initialBucket));

            return false;
        }

        public V this[K key] { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                if (!TryGetValue(key, out var result))
                    throw new KeyNotFoundException($"Key not found: {key}");
                else
                    return result;
            }
            set {
            retry:
                var insertResult = TryInsert(key, value, InsertMode.OverwriteValue);
                switch (insertResult) {
                    case InsertResult.OkAddedNew:
                        _Count++;
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
                    _Count++;
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

        internal struct ClearCallback : IBucketCallback {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Bucket (ref Bucket bucket, ref Entry entry) {
                var count = bucket.GetSlot(CountSlot);
                if (count == 0) {
                    // This might be locked to 255 if we previously had a ton of collisions
                    bucket.SetSlot(CascadeSlot, 0);
                    return;
                }

                bucket.Suffixes = default;
                if (RuntimeHelpers.IsReferenceOrContainsReferences<K>())
                    bucket.Keys = default;

                if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>()) {
                    ref var lastEntry = ref Unsafe.Add(ref entry, count - 1);
                    do {
                        entry = default;
                        entry = ref Unsafe.Add(ref entry, 1);
                    } while (!Unsafe.AreSame(ref entry, ref lastEntry));
                }
            }
        }

        public void Clear () {
            if (_Count == 0)
                return;

            _Count = 0;
#if SMART_CLEAR
            EnumerateBuckets(new ClearCallback());
#else
            Array.Clear(_Buckets);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<Entry>())
                Array.Clear(_Entries);
#endif
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnumerateBuckets<TCallback> (TCallback callback)
            where TCallback : struct, IBucketCallback {
            // We can't early-out if Count is 0 here since this could be invoked by Clear

            var buckets = (Span<Bucket>)_Buckets;
            var entries = (Span<Entry>)_Entries;
            // Guard against concurrent access
            if ((buckets.Length == 0) || (entries.Length == 0))
                return;

            ref Bucket bucket = ref MemoryMarshal.GetReference(buckets),
                lastBucket = ref Unsafe.Add(ref bucket, buckets.Length - 1);
            ref Entry entry = ref MemoryMarshal.GetReference(entries);

            while (true) {
                callback.Bucket(ref bucket, ref entry);

                if (!Unsafe.AreSame(ref bucket, ref lastBucket)) {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    entry = ref Unsafe.Add(ref entry, BucketSizeI);
                } else
                    break;
            }
        }

        private struct CopyToKvp : IBucketCallback {
            public readonly KeyValuePair<K, V>[] Array;
            public int Index;

            public CopyToKvp (KeyValuePair<K, V>[] array, int index) {
                Array = array;
                Index = index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Bucket (ref Bucket bucket, ref Entry firstBucketEntry) {
                var bucketCount = bucket.GetSlot(CountSlot);
                for (int j = 0; j < bucketCount; j++) {
                    ref var entry = ref Unsafe.Add(ref firstBucketEntry, j);
                    Array[Index++] = new KeyValuePair<K, V>(bucket.Keys[j], entry.Value);
                }
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
            public void Bucket (ref Bucket bucket, ref Entry firstBucketEntry) {
                var bucketCount = bucket.GetSlot(CountSlot);
                for (int j = 0; j < bucketCount; j++) {
                    ref var entry = ref Unsafe.Add(ref firstBucketEntry, j);
                    Array[Index++] = new KeyValuePair<K, V>(bucket.Keys[j], entry.Value);
                }
            }
        }

        private void CopyToArray<T> (T[] array, int index) {
            if (array == null)
                throw new ArgumentNullException(nameof(array));
            if ((uint)index > (uint)array.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < Count)
                throw new ArgumentException("Destination array too small", nameof(index));

            if (array is KeyValuePair<K, V>[] kvp)
                EnumerateBuckets(new CopyToKvp(kvp, index));
            else if (array is object[] o)
                EnumerateBuckets(new CopyToObject(o, index));
            else
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
