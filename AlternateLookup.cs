﻿using System;
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
                    ref var pair = ref FindKey(key);
                    if (Unsafe.IsNullRef(ref pair))
                        throw new KeyNotFoundException($"Key {key} not found");
                    return pair.Value;
                }
            }

            public bool TryGetValue (TAlternateKey key, out V value) {
                ref var pair = ref FindKey(key);
                if (Unsafe.IsNullRef(ref pair)) {
                    value = default!;
                    return false;
                } else {
                    value = pair.Value;
                    return true;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ref Pair FindKey (TAlternateKey key) {
                // This is duplicated from SimdDictionary.FindKey, look there for comments.
                var dictionary = Dictionary;
                if (dictionary._Count == 0)
                    return ref Unsafe.NullRef<Pair>();

                var comparer = Comparer;
                var hashCode = FinalizeHashCode(unchecked((uint)comparer.GetHashCode(key)));

                var buckets = (Span<Bucket>)dictionary._Buckets;
                var initialBucketIndex = dictionary.BucketIndexForHashCode(hashCode, buckets);
                var suffix = GetHashSuffix(hashCode);

                ref Bucket lastBucket = ref Unsafe.NullRef<Bucket>(),
                    initialBucket = ref Unsafe.NullRef<Bucket>(),
                    bucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), initialBucketIndex);

                do {
                    int startIndex = FindSuffixInBucket(ref bucket, suffix);
                    ref var pair = ref FindKeyInBucket(ref bucket, startIndex, comparer, key, out _);
                    if (Unsafe.IsNullRef(ref pair)) {
                        if (bucket.GetSlot(CascadeSlot) == 0)
                            return ref Unsafe.NullRef<Pair>();
                    } else
                        return ref pair;

                    if (Unsafe.IsNullRef(ref initialBucket)) {
                        initialBucket = ref bucket;
                        lastBucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(buckets), buckets.Length - 1);
                    }

                    if (Unsafe.AreSame(ref bucket, ref lastBucket)) {
                        bucket = ref MemoryMarshal.GetReference(buckets);
                    } else {
                        bucket = ref Unsafe.Add(ref bucket, 1);
                    }
                } while (!Unsafe.AreSame(ref bucket, ref initialBucket));

                return ref Unsafe.NullRef<Pair>();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket,
                int indexInBucket, IAlternateComparer<K, TAlternateKey> comparer, 
                TAlternateKey needle, out int matchIndexInBucket
            ) {
                Debug.Assert(indexInBucket >= 0);
                Debug.Assert(comparer != null);

                int count = bucket.GetSlot(CountSlot);
                // It might be faster on some targets to early-out before the address computation(s) below
                //  by doing a direct comparison between indexInBucket and count. In my local testing, it's not faster,
                //  and this implementation generates smaller code

                // It's impossible to properly initialize this reference until indexInBucket has been range-checked.
                ref var pair = ref Unsafe.NullRef<Pair>();
                for (; indexInBucket < count; indexInBucket++, pair = ref Unsafe.Add(ref pair, 1)) {
                    if (Unsafe.IsNullRef(ref pair))
                        pair = ref Unsafe.Add(ref bucket.Pairs.Pair0, indexInBucket);
                    if (comparer!.Equals(pair.Key, needle)) {
                        matchIndexInBucket = indexInBucket;
                        return ref pair;
                    }
                }

                matchIndexInBucket = -1;
                return ref Unsafe.NullRef<Pair>();
            }
        }
    }
}
