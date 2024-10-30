﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimdDictionary {
    public partial class SimdDictionary<K, V> {
        public struct KeyCollection : ICollection<K>, ICollection {
            public readonly SimdDictionary<K, V> Dictionary;

            public struct Enumerator : IEnumerator<K> {
                internal SimdDictionary<K, V>.Enumerator Inner;

                public K Current => Inner.CurrentKey;
                object? IEnumerator.Current => Inner.CurrentKey;

                internal Enumerator (SimdDictionary<K, V> dictionary) {
                    Inner = dictionary.GetEnumerator();
                }

                public void Dispose () =>
                    Inner.Dispose();

                public bool MoveNext () =>
                    Inner.MoveNext();

                public void Reset () =>
                    Inner.Reset();
            }

            internal KeyCollection (SimdDictionary<K, V> dictionary) {
                Dictionary = dictionary;
            }

            public int Count => Dictionary.Count;
            bool ICollection<K>.IsReadOnly => true;

            void ICollection<K>.Add (K item) =>
                throw new InvalidOperationException();

            void ICollection<K>.Clear () =>
                Dictionary.Clear();

            bool ICollection<K>.Contains (K item) =>
                Dictionary.ContainsKey(item);

            void ICollection<K>.CopyTo (K[] array, int arrayIndex) {
                // FIXME: Use EnumerateBuckets
                using (var e = GetEnumerator())
                    while (e.MoveNext())
                        array[arrayIndex++] = e.Current;
            }

            public Enumerator GetEnumerator () =>
                new Enumerator(Dictionary);

            IEnumerator<K> IEnumerable<K>.GetEnumerator () =>
                GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator () =>
                GetEnumerator();

            bool ICollection<K>.Remove (K item) =>
                Dictionary.Remove(item);

            bool ICollection.IsSynchronized => false;
            object ICollection.SyncRoot => Dictionary;
            void ICollection.CopyTo(System.Array array, int index) {
                throw new NotImplementedException();
            }
        }

        public struct ValueCollection : ICollection<V>, ICollection {
            public readonly SimdDictionary<K, V> Dictionary;

            public struct Enumerator : IEnumerator<V> {
                internal SimdDictionary<K, V>.Enumerator Inner;

                public V Current => Inner.CurrentValue;
                object? IEnumerator.Current => Inner.CurrentValue;

                internal Enumerator (SimdDictionary<K, V> dictionary) {
                    Inner = dictionary.GetEnumerator();
                }

                public void Dispose () =>
                    Inner.Dispose();

                public bool MoveNext () =>
                    Inner.MoveNext();

                public void Reset () =>
                    Inner.Reset();
            }

            internal ValueCollection (SimdDictionary<K, V> dictionary) {
                Dictionary = dictionary;
            }

            public int Count => Dictionary.Count;
            bool ICollection<V>.IsReadOnly => true;

            void ICollection<V>.Add (V item) =>
                throw new InvalidOperationException();

            void ICollection<V>.Clear () =>
                Dictionary.Clear();

            // FIXME
            bool ICollection<V>.Contains (V item) =>
                throw new InvalidOperationException();

            void ICollection<V>.CopyTo (V[] array, int arrayIndex) {
                // FIXME: Use EnumerateBuckets
                using (var e = GetEnumerator())
                    while (e.MoveNext())
                        array[arrayIndex++] = e.Current;
            }

            public Enumerator GetEnumerator () =>
                new Enumerator(Dictionary);

            IEnumerator<V> IEnumerable<V>.GetEnumerator () =>
                GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator () =>
                GetEnumerator();

            bool ICollection<V>.Remove (V item) =>
                throw new InvalidOperationException();

            bool ICollection.IsSynchronized => false;
            object ICollection.SyncRoot => Dictionary;
            void ICollection.CopyTo(System.Array array, int index) {
                throw new NotImplementedException();
            }
        }

        public struct Enumerator : IEnumerator<KeyValuePair<K, V>>, IDictionaryEnumerator {
            private int _bucketIndex, _valueIndexLocal;
            private Bucket _currentBucket;
            private Bucket[] _buckets;

            public K CurrentKey {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    return _currentBucket.Pairs[_valueIndexLocal].Key;
                }
            }

            public V CurrentValue {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    return _currentBucket.Pairs[_valueIndexLocal].Value;
                }
            }

            public KeyValuePair<K, V> Current {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    ref var pair = ref _currentBucket.Pairs[_valueIndexLocal];
                    return new KeyValuePair<K, V>(pair.Key, pair.Value);
                }
            }
            object IEnumerator.Current => Current;

            DictionaryEntry IDictionaryEnumerator.Entry {
                get {
                    ref var pair = ref _currentBucket.Pairs[_valueIndexLocal];
                    return new DictionaryEntry(pair.Key, pair.Value);
                }
            }

            object IDictionaryEnumerator.Key => CurrentKey;

            object? IDictionaryEnumerator.Value => CurrentValue;

            public Enumerator (SimdDictionary<K, V> dictionary) {
                _bucketIndex = -1;
                _valueIndexLocal = BucketSizeI;
                _buckets = dictionary._Buckets;
            }

            public void Dispose () {
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext () {
                _valueIndexLocal++;

                while (_bucketIndex < _buckets.Length) {
                    var count = _currentBucket.Count;
                    if (_valueIndexLocal >= count) {
                        _valueIndexLocal = 0;
                        _bucketIndex++;
                        if (_bucketIndex >= _buckets.Length)
                            return false;
                        _currentBucket = _buckets[_bucketIndex];
                    }

                    while (_valueIndexLocal < count) {
                        var suffix = _currentBucket.GetSlot(_valueIndexLocal);
                        if (suffix != 0)
                            return true;
                        _valueIndexLocal++;
                    }
                }

                return false;
            }

            public void Reset () {
                _bucketIndex = -1;
                _valueIndexLocal = BucketSizeI;
            }
        }
    }
}
