// Force disables the vectorized suffix search implementations so you can test/benchmark the scalar one
// #define FORCE_SCALAR_IMPLEMENTATION

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;

namespace SimdDictionary {
    public partial class VectorizedDictionary<K, V> {
        // Extracting all this logic into each caller improves codegen slightly + reduces code size slightly, but the
        //  duplication reduces maintainability, so I'm pretty happy doing this instead.
        // We rely on inlining to cause this struct to completely disappear, and its fields to become registers or individual locals.
        private ref struct LoopingBucketEnumerator {
            // The size of this struct is REALLY important! Adding even a single field to this will cause stack spills in critical loops.
            // The current size is small enough for TryGetValue to have a register to spare, and for TryInsert to barely avoid touching stack.
            public ref Bucket bucket, lastBucket;
            public ref Bucket initialBucket;

            // Will never fail as long as buckets isn't 0-length. You don't need to call Advance before your first loop iteration.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LoopingBucketEnumerator (VectorizedDictionary<K, V> self, uint hashCode) {
                var buckets = new Span<Bucket>(self._Buckets);
                var initialBucketIndex = self.BucketIndexForHashCode(hashCode, buckets);
                Debug.Assert(buckets.Length > 0);

                // The start of the array. We need to stash this so we can loop later, and we're using it below too.
                ref var firstBucket = ref MemoryMarshal.GetReference(buckets);
                lastBucket = ref Unsafe.Add(ref firstBucket, buckets.Length - 1);

                // This is calculated by BucketIndexForHashCode (either masked with & or modulus), so it's never out of range
                // FIXME: For concurrent modification safety, do a Math.Min here and rely on the branch to predict 100% reliably?
                Debug.Assert(initialBucketIndex < buckets.Length);
                bucket = ref Unsafe.Add(ref firstBucket, initialBucketIndex);
                initialBucket = ref bucket;
            }

            // HACK: Outlining this method doesn't reduce code size, so just inline it.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref Bucket GetFirstBucket (VectorizedDictionary<K, V> self) {
                // In the common case (given an optimal hash) we won't actually need the address of the first bucket,
                //  so we no longer store it in the enumerator. Instead, we compute it on-demand by re-loading the field.
                // This is fairly cheap in practice since our this-reference has to stay around anyway.
                var buckets = self._Buckets;
                ref var bucket = ref MemoryMarshal.GetArrayDataReference(buckets);
                // We re-loaded Buckets, so it may be a different array than it was when we started.
                // If this is the case, it's impossible to continue since we will never reach initialBucket.
                if (!Unsafe.AreSame(ref Unsafe.Add(ref bucket, buckets.Length - 1), ref lastBucket))
                    ThrowConcurrentModification();
                return ref bucket;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Advance (VectorizedDictionary<K, V> self) {
                if (Unsafe.AreSame(ref bucket, ref lastBucket))
                    // Rare case: Wrap around from last bucket to first bucket.
                    bucket = ref GetFirstBucket(self);
                else
                    bucket = ref Unsafe.Add(ref bucket, 1);

                if (Unsafe.AreSame(ref bucket, ref initialBucket))
                    return false;
                else
                    return true;
            }

            // Will attempt to walk backwards through the buckets you previously visited.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Retreat (ref Bucket firstBucket) {
                // We can't retreat when standing on our initial bucket.
                if (Unsafe.AreSame(ref bucket, ref initialBucket))
                    return false;

                if (Unsafe.AreSame(ref bucket, ref firstBucket))
                    bucket = ref lastBucket;
                else
                    bucket = ref Unsafe.Subtract(ref bucket, 1);

                // It's okay if we're standing on our initial bucket right now.
                return true;
            }
        }

        // Callback is passed by-ref so it can be used to store results from the enumeration operation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnumerateBuckets<TCallback> (Span<Bucket> buckets, ref TCallback callback)
            where TCallback : struct, IBucketCallback {
            // FIXME: Using a foreach on this span produces an imul-per-iteration for some reason.
            ref Bucket bucket = ref MemoryMarshal.GetReference(buckets),
                lastBucket = ref Unsafe.Add(ref bucket, buckets.Length - 1);

            while (true) {
                var ok = callback.Bucket(ref bucket);
                if (ok && !Unsafe.AreSame(ref bucket, ref lastBucket))
                    bucket = ref Unsafe.Add(ref bucket, 1);
                else
                    break;
            }
        }

        // Callback is passed by-ref so it can be used to store results from the enumeration operation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnumeratePairs<TCallback> (Span<Bucket> buckets, ref TCallback callback)
            where TCallback : struct, IPairCallback {
            // FIXME: Using a foreach on this span produces an imul-per-iteration for some reason.
            ref Bucket bucket = ref MemoryMarshal.GetReference(buckets),
                lastBucket = ref Unsafe.Add(ref bucket, buckets.Length - 1);

            while (true) {
                ref var pair = ref bucket.Pairs.Pair0;
                for (int i = 0, c = bucket.Count; i < c; i++) {
                    if (!callback.Pair(ref pair))
                        return;
                    pair = ref Unsafe.Add(ref pair, 1);
                }

                if (!Unsafe.AreSame(ref bucket, ref lastBucket))
                    bucket = ref Unsafe.Add(ref bucket, 1);
                else
                    return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int FindSuffixInBucket (ref Bucket bucket, Vector128<byte> searchVector, int bucketCount) {
#if !FORCE_SCALAR_IMPLEMENTATION
            if (Sse2.IsSupported) {
                return BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.CompareEqual(searchVector, bucket.Suffixes)));
            } else if (AdvSimd.Arm64.IsSupported) {
                // Completely untested
                var laneBits = AdvSimd.And(
                    AdvSimd.CompareEqual(searchVector, bucket.Suffixes), 
                    Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128)
                );
                var moveMask = AdvSimd.Arm64.AddAcross(laneBits.GetLower()).ToScalar() |
                    (AdvSimd.Arm64.AddAcross(laneBits.GetUpper()).ToScalar() << 8);
                return BitOperations.TrailingZeroCount(moveMask);
            } else if (PackedSimd.IsSupported) {
                // Completely untested
                return BitOperations.TrailingZeroCount(PackedSimd.Bitmask(PackedSimd.CompareEqual(searchVector, bucket.Suffixes)));
            } else {
#else
            {
#endif
                if (false) {
                    // Hand-unrolled scan of multiple bytes at a time. If a bucket contains 9 or more items, we will erroneously
                    //  check lanes 15 and 16 (which contain the count and cascade count), but finding a false match there is harmless
                    // We could do this 4 bytes at a time instead, but that isn't actually faster
                    // This produces larger code than a chain of ifs.
                    var wideHaystack = (UInt64*)Unsafe.AsPointer(ref bucket);
                    for (int i = 0; i < bucketCount; i += 8, wideHaystack += 1) {
                        // Doing a xor this way basically performs a vectorized compare of all the lanes, and we can test the result with
                        //  a == 0 check on the low 8 bits, which is a single 'test rNNb' instruction on x86/x64
                        var matchMask = *wideHaystack ^ searchVector.AsUInt64()[0];
                        if (Step(ref matchMask))
                            return i;
                        if (Step(ref matchMask))
                            return i + 1;
                        if (Step(ref matchMask))
                            return i + 2;
                        if (Step(ref matchMask))
                            return i + 3;
                        if (Step(ref matchMask))
                            return i + 4;
                        if (Step(ref matchMask))
                            return i + 5;
                        if (Step(ref matchMask))
                            return i + 6;
                        if (Step(ref matchMask))
                            return i + 7;
                    }
                } else if (true) {
                    // Hand-unrolling the search into four comparisons per loop iteration is a significant performance improvement
                    //  for a moderate code size penalty (733b -> 826b; 399usec -> 321usec, vs BCL's 421b and 270usec)
                    // If a bucket contains 13 or more items we will erroneously check lanes 15/16 but this is harmless.
                    var haystack = (byte*)Unsafe.AsPointer(ref bucket);
                    for (int i = 0; i < bucketCount; i += 4, haystack += 4) {
                        if (haystack[0] == searchVector[0])
                            return i;
                        if (haystack[1] == searchVector[0])
                            return i + 1;
                        if (haystack[2] == searchVector[0])
                            return i + 2;
                        if (haystack[3] == searchVector[0])
                            return i + 3;
                    }
                } else {
                    var haystack = (byte*)Unsafe.AsPointer(ref bucket);
                    for (int i = 0; i < bucketCount; i++, haystack++)
                        if (*haystack == searchVector[0])
                            return i;
                }
                
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static bool Step (ref UInt64 matchMask) {
                    if ((matchMask & 0xFF) == 0)
                        return true;
                    matchMask >>= 8;
                    return false;
                }

                return 32;
            }
        }

        // In the common case this method never runs, but inlining allows some smart stuff to happen in terms of stack size and
        //  register usage.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AdjustCascadeCounts (
            LoopingBucketEnumerator enumerator, bool increase
        ) {
            // Early-out before doing setup work since in the common case we won't have cascaded out of a bucket at all
            if (Unsafe.AreSame(ref enumerator.bucket, ref enumerator.initialBucket))
                return;

            // We need the first bucket since it's the wrap point for a retreat, instead of lastBucket (which we have already)
            ref var firstBucket = ref enumerator.GetFirstBucket(this);

            // We may have cascaded out of a previous bucket; if so, scan backwards and update
            //  the cascade count for every bucket we previously scanned.
            while (enumerator.Retreat(ref firstBucket)) {
                // FIXME: Track number of times we cascade out of a bucket for string rehashing anti-DoS mitigation!
                var cascadeCount = enumerator.bucket.CascadeCount;
                if (increase) {
                    // Never overflow (wrap around) the counter
                    if (cascadeCount < 255)
                        enumerator.bucket.CascadeCount = (byte)(cascadeCount + 1);
                } else {
                    if (cascadeCount == 0)
                        ThrowCorrupted();
                    // If the cascade counter hit 255, it's possible the actual cascade count through here is >255,
                    //  so it's no longer safe to decrement. This is a very rare scenario, but it permanently degrades the table.
                    // TODO: Track this and triggering a rehash once too many buckets are in this state + dict is mostly empty.
                    else if (cascadeCount < 255)
                        enumerator.bucket.CascadeCount = (byte)(cascadeCount - 1);
                }
            }
        }

#pragma warning disable CS8619
        // These have to be structs so that the JIT will specialize callers instead of Canonizing them
        private struct DefaultComparerKeySearcher : IKeySearcher {

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
                return FinalizeHashCode(unchecked((uint)EqualityComparer<K>.Default.GetHashCode(key!)));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static unsafe ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket, int indexInBucket, int bucketCount, 
                IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            ) {
                Unsafe.SkipInit(out matchIndexInBucket);
                Debug.Assert(indexInBucket >= 0);

                int count = bucketCount - indexInBucket;
                if (count <= 0)
                    return ref Unsafe.NullRef<Pair>();

                ref Pair pair = ref Unsafe.Add(ref bucket.Pairs.Pair0, indexInBucket);
                while (true) {
                    if (EqualityComparer<K>.Default.Equals(needle, pair.Key)) {
                        // We could optimize out the bucketCount local to prevent a stack spill in some cases by doing
                        //  Unsafe.ByteOffset(...) / sizeof(Pair), but the potential idiv is extremely painful
                        matchIndexInBucket = bucketCount - count;
                        return ref pair;
                    }

                    // NOTE: --count <= 0 produces an extra 'test' opcode
                    if (--count == 0)
                        return ref Unsafe.NullRef<Pair>();
                    else
                        pair = ref Unsafe.Add(ref pair, 1);
                }
            }
        }

        private struct ComparerKeySearcher : IKeySearcher {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
                return FinalizeHashCode(unchecked((uint)comparer!.GetHashCode(key!)));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static unsafe ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket, int indexInBucket, int bucketCount, 
                IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            ) {
                Unsafe.SkipInit(out matchIndexInBucket);
                Debug.Assert(indexInBucket >= 0);
                Debug.Assert(comparer != null);

                int count = bucketCount - indexInBucket;
                if (count <= 0)
                    return ref Unsafe.NullRef<Pair>();

                ref Pair pair = ref Unsafe.Add(ref bucket.Pairs.Pair0, indexInBucket);
                // FIXME: This loop spills two values to/from the stack every iteration, and it's not clear which.
                // The ValueType-with-default-comparer one doesn't.
                while (true) {
                    if (comparer.Equals(needle, pair.Key)) {
                        // We could optimize out the bucketCount local to prevent a stack spill in some cases by doing
                        //  Unsafe.ByteOffset(...) / sizeof(Pair), but the potential idiv is extremely painful
                        matchIndexInBucket = bucketCount - count;
                        return ref pair;
                    }

                    // NOTE: --count <= 0 produces an extra 'test' opcode
                    if (--count == 0)
                        return ref Unsafe.NullRef<Pair>();
                    else
                        pair = ref Unsafe.Add(ref pair, 1);
                }
            }
        }
#pragma warning restore CS8619
    }
}
