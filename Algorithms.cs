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
    public partial class SimdDictionary<K, V> {
        // Extracting all this logic into each caller improves codegen slightly + reduces code size slightly, but the
        //  duplication reduces maintainability, so I'm pretty happy doing this instead.
        // We rely on inlining to cause this struct to completely disappear, and its fields to become registers or individual locals.
        internal ref struct LoopingBucketEnumerator {
            // The size of this struct is REALLY important! Adding even a single field to this will cause stack spills in critical loops.
            // The current size is BARELY small enough for TryGetValue to run without touching stack. TryInsert for reftypes still touches stack.
            public int remainingUntilWrap;
            public ref Bucket firstBucket, bucket;
            public ref Bucket initialBucket;

            // Will never fail as long as buckets isn't 0-length. You don't need to call Advance before your first loop iteration.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LoopingBucketEnumerator (SimdDictionary<K, V> self, uint hashCode) {
                var buckets = (Span<Bucket>)self._Buckets;
                var initialBucketIndex = self.BucketIndexForHashCode(hashCode, buckets);
                Debug.Assert(buckets.Length > 0);

                // The start of the array. We need to stash this so we can loop later, and we're using it below too.
                firstBucket = ref MemoryMarshal.GetReference(buckets);
                // The number of buckets we can check before hitting the end, at which point we need to loop back to firstBucket.
                remainingUntilWrap = buckets.Length - initialBucketIndex - 1;

                // This is calculated by BucketIndexForHashCode (either masked with & or modulus), so it's never out of range
                // FIXME: For concurrent modification safety, do a Math.Min here and rely on the branch to predict 100% reliably?
                Debug.Assert(initialBucketIndex < buckets.Length);
                bucket = ref Unsafe.Add(ref firstBucket, initialBucketIndex);
                initialBucket = ref bucket;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Advance () {
                // For a single-bucket array, we will always start with bucket == initialBucket == firstBucket, and remainingUntilWrap == 0.
                // This method will then set bucket = firstBucket and remainingUntilWrap = int.MaxValue.
                // bucket will then == initialBucket, so we return false.
                // For larger arrays, we will loop around when we hit the end, and continue until we reach the bucket we started at.

                // Technically it should never be possible for this to become negative, but if we were initialized with a bad
                //  bucket index, it could be. So we might as well do <= even if that might generate an extra instruction.
                if (remainingUntilWrap <= 0) {
                    bucket = ref firstBucket;
                    // Now that we've wrapped around, we will encounter initialBucket before we ever overrun the end of the 
                    //  array, so it's safe for remainingUntilWrap to be int.MaxValue.
                    remainingUntilWrap = int.MaxValue;
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                    --remainingUntilWrap;
                }

                return !Unsafe.AreSame(ref bucket, ref initialBucket);
            }

            // Will attempt to walk backwards through the buckets you previously visited.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Retreat (SimdDictionary<K, V> self) {
                // We can't retreat when standing on our initial bucket.
                if (Unsafe.AreSame(ref bucket, ref initialBucket))
                    return false;

                if (Unsafe.AreSame(ref bucket, ref firstBucket)) {
                    // Wrapping around backwards during a retreat is uncommon, so the field load is okay.
                    var buckets = self._Buckets;
                    ref var newFirstBucket = ref MemoryMarshal.GetArrayDataReference(buckets);
                    // We had to re-load our array from a field, so the array could have been replaced since we first
                    //  loaded it and calculated firstBucket. Make sure it hasn't changed.
                    if (!Unsafe.AreSame(ref firstBucket, ref newFirstBucket))
                        Environment.FailFast("SimdDictionary was resized during cascade count update");
                    bucket = ref Unsafe.Add(ref newFirstBucket, buckets.Length - 1);
                } else
                    bucket = ref Unsafe.Subtract(ref bucket, 1);

                // It's okay if we're standing on our initial bucket right now.
                return true;
            }
        }

        // Callback is passed by-ref so it can be used to store results from the enumeration operation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnumerateBuckets<TCallback> (ref TCallback callback)
            where TCallback : struct, IBucketCallback {
            // We can't early-out if Count is 0 here since this could be invoked by Clear

            // FIXME: Using a foreach on this span produces an imul-per-iteration for some reason.
            var buckets = (Span<Bucket>)_Buckets;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe int FindSuffixInBucket (ref Bucket bucket, byte suffix, int bucketCount) {
#if !FORCE_SCALAR_IMPLEMENTATION
            if (Sse2.IsSupported) {
                // FIXME: It would be nice to precompute the search vector outside of the loop, to hide the latency of vpbroadcastb.
                // Right now if we do that, ryujit places the search vector in xmm6 which forces stack spills, and that's worse.
                // So we have to compute it on-demand here. On modern x86-64 chips the latency of vpbroadcastb is 1, at least.
                // FIXME: For some key types RyuJIT hoists the Vector128.Create out of the loop for us and causes a stack spill anyway :(
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
                for (int i = 0; i < bucketCount; i++) {
                    if (haystack[i] == suffix)
                        return i;
                }
                return 32;
            }
        }

        // In the common case this method never runs, but inlining allows some smart stuff to happen in terms of stack size and
        //  register usage.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AdjustCascadeCounts (
            LoopingBucketEnumerator enumerator, bool increase
        ) {
            // We may have cascaded out of a previous bucket; if so, scan backwards and update
            //  the cascade count for every bucket we previously scanned.
            while (enumerator.Retreat(this)) {
                // FIXME: Track number of times we cascade out of a bucket for string rehashing anti-DoS mitigation!
                var cascadeCount = enumerator.bucket.CascadeCount;
                if (increase) {
                    // Never overflow (wrap around) the counter
                    if (cascadeCount < 255)
                        enumerator.bucket.CascadeCount = (byte)(cascadeCount + 1);
                } else {
                    if (cascadeCount == 0)
                        Environment.FailFast("Corrupted dictionary bucket cascade slot");
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
        internal struct DefaultComparerKeySearcher : IKeySearcher {

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
                Debug.Assert(comparer != null);

                int count = bucketCount - indexInBucket;
                if (count <= 0)
                    return ref Unsafe.NullRef<Pair>();

                ref Pair pair = ref Unsafe.Add(ref bucket.Pairs.Pair0, indexInBucket);
                while (true) {
                    if (EqualityComparer<K>.Default.Equals(needle, pair.Key)) {
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

        internal struct ComparerKeySearcher : IKeySearcher {
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
