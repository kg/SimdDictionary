using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace SimdDictionary
{
    public partial class UnorderedDictionary<K, V> {
        public readonly struct AlternateLookup<TAlternateKey>
            where TAlternateKey : notnull, allows ref struct {

            public readonly UnorderedDictionary<K, V> Dictionary;
            public readonly IAlternateEqualityComparer<TAlternateKey, K> Comparer;

            public AlternateLookup (UnorderedDictionary<K, V> dictionary, IAlternateEqualityComparer<TAlternateKey, K> comparer) {
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
                        throw new KeyNotFoundException();
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

                var comparer = Comparer;
                var hashCode = FinalizeHashCode(unchecked((uint)comparer.GetHashCode(key)));
                var suffix = GetHashSuffix(hashCode);
                var searchVector = Vector128.Create(suffix);
                var enumerator = new LoopingBucketEnumerator(dictionary, hashCode);
                do {
                    int bucketCount = enumerator.bucket.Count,
                        startIndex = FindSuffixInBucket(ref enumerator.bucket, searchVector, bucketCount);
                    ref var pair = ref FindKeyInBucket(ref enumerator.bucket, startIndex, bucketCount, comparer, key, out _);
                    if (Unsafe.IsNullRef(ref pair)) {
                        if (enumerator.bucket.CascadeCount == 0)
                            return ref Unsafe.NullRef<Pair>();
                    } else
                        return ref pair;
                } while (enumerator.Advance(dictionary));

                return ref Unsafe.NullRef<Pair>();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static ref Pair FindKeyInBucket (
                // We have to use UnscopedRef to allow lazy initialization
                [UnscopedRef] ref Bucket bucket,
                int indexInBucket, int bucketCount, IAlternateEqualityComparer<TAlternateKey, K> comparer, 
                TAlternateKey needle, out int matchIndexInBucket
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
    }
}
