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
            public int length;
            public ref Bucket firstBucket, bucket;
            // These are NullRef until the start of your second iteration.
            public ref Bucket lastBucket, initialBucket;

            // Will never fail as long as buckets isn't 0-length. You don't need to call Advance before your first loop iteration.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LoopingBucketEnumerator (Span<Bucket> buckets, int initialBucketIndex) {
                Debug.Assert(buckets.Length > 0);

                firstBucket = ref MemoryMarshal.GetReference(buckets);
                length = buckets.Length;

                // Null-initialize these refs and then initialize them on demand only if we have to check multiple buckets.
                // This increases code size a bit, but moves a bunch of code off of the hot path.
                lastBucket = ref Unsafe.NullRef<Bucket>();
                initialBucket = ref Unsafe.NullRef<Bucket>();
                // This is calculated by BucketIndexForHashCode (either masked with & or modulus), so it's never out of range
                bucket = ref Unsafe.Add(ref firstBucket, initialBucketIndex);
            }

            // Will lazily initialize initialBucket/lastBucket at the end of your first iteration.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Advance () {
                if (Unsafe.IsNullRef(ref initialBucket)) {
                    initialBucket = ref bucket;
                    lastBucket = ref Unsafe.Add(ref firstBucket, length - 1);
                }

                if (Unsafe.AreSame(ref bucket, ref lastBucket)) {
                    bucket = ref firstBucket;
                } else {
                    bucket = ref Unsafe.Add(ref bucket, 1);
                }

                return !Unsafe.AreSame(ref bucket, ref initialBucket);
            }
        }

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

        // This is a slow path that only runs when a bucket overflows, so we don't need to inline it.
        // Inlining it improves remove/insert performance on average a bit though, for remove the difference is:
        // 1.0244x (inlined) vs 1.0991x (not inlined)
        // The code size improvement from not inlining it appears to be quite small (930 bytes vs 933), so for now it's inlined.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void AdjustCascadeCounts (
            ref Bucket bucket, ref Bucket firstBucket, ref Bucket initialBucket, ref Bucket lastBucket,
            bool increase
        ) {
            // We may have cascaded out of a previous bucket; if so, scan backwards and update
            //  the cascade count for every bucket we previously scanned.
            while (!Unsafe.AreSame(ref bucket, ref initialBucket)) {
                // FIXME: Track number of times we cascade out of a bucket for string rehashing anti-DoS mitigation!
                bucket = ref Unsafe.Subtract(ref bucket, 1);
                if (Unsafe.IsAddressLessThan(ref bucket, ref firstBucket))
                    bucket = ref lastBucket;

                var cascadeCount = bucket.CascadeCount;
                if (increase) {
                    // Never overflow (wrap around) the counter
                    if (cascadeCount < 255)
                        bucket.CascadeCount = (byte)(cascadeCount + 1);
                } else {
                    if (cascadeCount == 0)
                        Environment.FailFast("Corrupted dictionary bucket cascade slot");
                    // If the cascade counter hit 255, it's possible the actual cascade count through here is >255,
                    //  so it's no longer safe to decrement. This is a very rare scenario, but it permanently degrades the table.
                    // TODO: Track this and triggering a rehash once too many buckets are in this state + dict is mostly empty.
                    else if (cascadeCount < 255)
                        bucket.CascadeCount = (byte)(cascadeCount - 1);
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
            public static ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket, int indexInBucket, int bucketCount, 
                IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            ) {
                Debug.Assert(indexInBucket >= 0);

                if (typeof(K).IsValueType) {
                    // It's impossible to properly initialize this reference until indexInBucket has been range-checked.
                    ref var pair = ref Unsafe.NullRef<Pair>();
                    for (; indexInBucket < bucketCount; indexInBucket++, pair = ref Unsafe.Add(ref pair, 1)) {
                        // It might be good to find a way to compile this down to a cmov instead of the current branch
                        if (Unsafe.IsNullRef(ref pair))
                            pair = ref Unsafe.Add(ref bucket.Pairs.Pair0, indexInBucket);
                        if (EqualityComparer<K>.Default.Equals(needle, pair.Key)) {
                            matchIndexInBucket = indexInBucket;
                            return ref pair;
                        }
                    }
                } else {
                    Environment.FailFast("FindKeyInBucketWithDefaultComparer called for non-struct key type");
                }

                matchIndexInBucket = -1;
                return ref Unsafe.NullRef<Pair>();
            }
        }

        internal struct ComparerKeySearcher : IKeySearcher {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
                return FinalizeHashCode(unchecked((uint)comparer!.GetHashCode(key!)));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket, int indexInBucket, int bucketCount, 
                IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            ) {
                Debug.Assert(indexInBucket >= 0);
                Debug.Assert(comparer != null);

                // It's impossible to properly initialize this reference until indexInBucket has been range-checked.
                ref var pair = ref Unsafe.NullRef<Pair>();
                for (; indexInBucket < bucketCount; indexInBucket++, pair = ref Unsafe.Add(ref pair, 1)) {
                    if (Unsafe.IsNullRef(ref pair))
                        pair = ref Unsafe.Add(ref bucket.Pairs.Pair0, indexInBucket);
                    if (comparer!.Equals(needle, pair.Key)) {
                        matchIndexInBucket = indexInBucket;
                        return ref pair;
                    }
                }

                matchIndexInBucket = -1;
                return ref Unsafe.NullRef<Pair>();
            }
        }
#pragma warning restore CS8619
    }
}
