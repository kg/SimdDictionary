using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
                // if (dictionary._Count == 0)
                //     return ref Unsafe.NullRef<Pair>();

                var comparer = Comparer;
                var hashCode = FinalizeHashCode(unchecked((uint)comparer.GetHashCode(key)));

                var buckets = (Span<Bucket>)dictionary._Buckets;
                var initialBucketIndex = dictionary.BucketIndexForHashCode(hashCode, buckets);
                var suffix = GetHashSuffix(hashCode);

                var enumerator = new LoopingBucketEnumerator(buckets, initialBucketIndex);
                do {
                    int bucketCount = enumerator.bucket.Count,
                        startIndex = FindSuffixInBucket(ref enumerator.bucket, suffix, bucketCount);
                    ref var pair = ref FindKeyInBucket(ref enumerator.bucket, startIndex, bucketCount, comparer, key, out _);
                    if (Unsafe.IsNullRef(ref pair)) {
                        if (enumerator.bucket.CascadeCount == 0)
                            return ref Unsafe.NullRef<Pair>();
                    } else
                        return ref pair;
                } while (enumerator.Advance());

                return ref Unsafe.NullRef<Pair>();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket,
                int indexInBucket, int bucketCount, IAlternateComparer<K, TAlternateKey> comparer, 
                TAlternateKey needle, out int matchIndexInBucket
            ) {
                Debug.Assert(indexInBucket >= 0);
                Debug.Assert(comparer != null);

                // It might be faster on some targets to early-out before the address computation(s) below
                //  by doing a direct comparison between indexInBucket and count. In my local testing, it's not faster,
                //  and this implementation generates smaller code

                // It's impossible to properly initialize this reference until indexInBucket has been range-checked.
                ref var pair = ref Unsafe.NullRef<Pair>();
                for (; indexInBucket < bucketCount; indexInBucket++, pair = ref Unsafe.Add(ref pair, 1)) {
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
