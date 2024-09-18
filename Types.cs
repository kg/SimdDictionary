using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SimdDictionary {
    public partial class SimdDictionary<K, V>
        where K : notnull
    {
        public const int InitialCapacity = 0,
            // User-specified capacity values will be increased to this percentage in order
            //  to maintain an ideal load factor. FIXME: 120 isn't right
            OversizePercentage = 120,
            // TODO: A BucketSize of 8 would waste 4 slots in every bucket, but would turn some imuls into shifts
            BucketSizeI = 14,
            CountSlot = 14,
            CascadeSlot = 15;

        public const uint BucketSizeU = 14;
        
        // This size must match BucketSizeI/U
        // A size of 4 or 8 would turn some imuls into shifts, but the impact of that seems small compared
        //  to the wasted memory
        [InlineArray(14)]
        internal struct InlineKeyArray {
            public K Key0;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        internal struct Bucket {
            public Vector128<byte> Suffixes;
            public InlineKeyArray Keys;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly byte GetSlot (int index) {
                Debug.Assert(index < Vector128<byte>.Count);
                // the extract-lane opcode this generates is slower than doing a byte load from memory,
                //  even if we already have the bucket in a register. Not sure why, but my guess based on agner's
                //  instruction tables is that it's because lane extract generates more uops than a byte move.
                // the two operations have the same latency on icelake, and the byte move's latency is lower on zen4
                // return self[index];
                // index &= 15;
                return Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref Unsafe.AsRef(in Suffixes)), index);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetSlot (nuint index, byte value) {
                Debug.Assert(index < (nuint)Vector128<byte>.Count);
                // index &= 15;
                Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref Suffixes), index) = value;
            }
        }

        internal record struct Entry (V Value);

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
            // The dictionary is clearly corrupted
            CorruptedInternalState,
        }

        // We have separate implementations of FindKeyInBucket that get used depending on whether we have a null
        //  comparer for a valuetype, where we can rely on ryujit to inline EqualityComparer<K>.Default
        internal interface IKeySearcher {
            static abstract ref Entry FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization of the key reference below.
                [UnscopedRef] ref Bucket bucket, [UnscopedRef] ref Entry firstEntryInBucket,
                int indexInBucket, IEqualityComparer<K>? comparer, K needle, out int matchIndexInBucket
            );

            static abstract uint GetHashCode (IEqualityComparer<K>? comparer, K key);
        }

        // Used to encapsulate operations that enumerate all the buckets synchronously (i.e. CopyTo)
        internal interface IBucketCallback {
            // Return false to stop iteration
            abstract bool Bucket (ref Bucket bucket, ref Entry firstBucketEntry);
        }
    }
}