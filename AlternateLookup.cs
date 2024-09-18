using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace SimdDictionary
{
    public interface IAlternateComparer<K, TAlternateKey> {
        int GetHashCode (TAlternateKey other);
        bool Equals (K key, TAlternateKey other);
    }

    public partial class SimdDictionary<K, V> {
        public readonly struct AlternateLookup<TAlternateKey>
            where TAlternateKey : notnull { // fixme: allows ref struct

            public readonly SimdDictionary<K, V> Dictionary;
            public readonly IAlternateComparer<K, TAlternateKey> Comparer;

            public AlternateLookup (SimdDictionary<K, V> dictionary, IAlternateComparer<K, TAlternateKey> comparer) {
                if (dictionary == null)
                    throw new ArgumentNullException(nameof(dictionary));
                if (comparer == null)
                    throw new ArgumentNullException(nameof(comparer));
                Dictionary = dictionary;
                Comparer = comparer;
            }

            public V this [TAlternateKey key] {
                get {
                    ref V value = ref FindKey(key);
                    if (Unsafe.IsNullRef(ref value))
                        throw new KeyNotFoundException($"Key {key} not found");
                    return value;
                }
            }

            public bool TryGetValue (TAlternateKey key, out V value) {
                ref var entry = ref FindKey(key);
                if (Unsafe.IsNullRef(ref entry)) {
                    value = default!;
                    return false;
                } else {
                    value = entry;
                    return true;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ref V FindKey (TAlternateKey key) {
                // This is duplicated from SimdDictionary.FindKey, look there for comments.
                var dictionary = Dictionary;
                if (dictionary._Count == 0)
                    return ref Unsafe.NullRef<V>();

                var comparer = Comparer;
                var hashCode = FinalizeHashCode(unchecked((uint)comparer.GetHashCode(key)));

                var buckets = (Span<Bucket>)dictionary._Buckets;
                var entries = (Span<V>)dictionary._Entries;
                var initialBucketIndex = dictionary.BucketIndexForHashCode(hashCode, buckets);
                var suffix = GetHashSuffix(hashCode);

                Debug.Assert(entries.Length >= buckets.Length * BucketSizeI);

                ref Bucket lastBucket = ref Unsafe.NullRef<Bucket>(),
                    initialBucket = ref Unsafe.NullRef<Bucket>(),
                    bucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), initialBucketIndex);
                ref var firstBucketEntry = ref entries[initialBucketIndex * BucketSizeI];

                do {
                    int startIndex = FindSuffixInBucket(ref bucket, suffix);
                    ref var entry = ref FindKeyInBucket(ref bucket, ref firstBucketEntry, startIndex, comparer, key, out _);
                    if (Unsafe.IsNullRef(ref entry)) {
                        if (bucket.GetSlot(CascadeSlot) == 0)
                            return ref Unsafe.NullRef<V>();
                    } else
                        return ref entry;

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

                return ref Unsafe.NullRef<V>();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static ref V FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization of the key reference below.
                [UnscopedRef] ref Bucket bucket, [UnscopedRef] ref V firstEntryInBucket, 
                int indexInBucket, IAlternateComparer<K, TAlternateKey> comparer, TAlternateKey needle, out int matchIndexInBucket
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
                    if (comparer!.Equals(key, needle)) {
                        matchIndexInBucket = indexInBucket;
                        return ref Unsafe.Add(ref firstEntryInBucket, indexInBucket);
                    }
                }

                matchIndexInBucket = -1;
                return ref Unsafe.NullRef<V>();
            }
        }
    }
}
