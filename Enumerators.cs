using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Bucket = System.Runtime.Intrinsics.Vector128<byte>;

namespace SimdDictionary {
    public partial class SimdDictionary<K, V> : IDictionary<K, V>, ICloneable {
        public struct KeyCollection : ICollection<K> {
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

            int ICollection<K>.Count => Dictionary.Count;
            bool ICollection<K>.IsReadOnly => true;

            void ICollection<K>.Add (K item) =>
                throw new NotImplementedException();

            void ICollection<K>.Clear () =>
                throw new NotImplementedException();

            bool ICollection<K>.Contains (K item) =>
                Dictionary.ContainsKey(item);

            void ICollection<K>.CopyTo (K[] array, int arrayIndex) {
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
                throw new NotImplementedException();
        }

        public struct ValueCollection : ICollection<V> {
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

            int ICollection<V>.Count => Dictionary.Count;
            bool ICollection<V>.IsReadOnly => true;

            void ICollection<V>.Add (V item) =>
                throw new NotImplementedException();

            void ICollection<V>.Clear () =>
                throw new NotImplementedException();

            // FIXME
            bool ICollection<V>.Contains (V item) =>
                throw new NotImplementedException();

            void ICollection<V>.CopyTo (V[] array, int arrayIndex) {
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
                throw new NotImplementedException();
        }

        public struct Enumerator : IEnumerator<KeyValuePair<K, V>> {
            private int _bucketIndex, _valueIndex, _valueIndexLocal;
            private Bucket _currentBucket;
            private Bucket[]? _buckets;
            private Entry[]? _entries;

            public K CurrentKey {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    if (_entries == null)
                        throw new InvalidOperationException();
                    return _entries[_valueIndex].Key;
                }
            }

            public V CurrentValue {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    if (_entries == null)
                        throw new InvalidOperationException();
                    return _entries[_valueIndex].Value;
                }
            }

            public KeyValuePair<K, V> Current {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    if (_entries == null)
                        throw new InvalidOperationException();
                    ref var entry = ref _entries[_valueIndex];
                    return new KeyValuePair<K, V>(entry.Key, entry.Value);
                }
            }
            object IEnumerator.Current => Current;

            public Enumerator (SimdDictionary<K, V> dictionary) {
                _bucketIndex = -1;
                _valueIndex = -1;
                _valueIndexLocal = BucketSizeI;
                _buckets = dictionary._Buckets;
                _entries = dictionary._Entries;
            }

            public void Dispose () {
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext () {
                if (_buckets == null)
                    return false;

                _valueIndex++;
                _valueIndexLocal++;

                while (_bucketIndex < _buckets.Length) {
                    if (_valueIndexLocal >= BucketSizeI) {
                        _valueIndexLocal = 0;
                        _bucketIndex++;
                        if (_bucketIndex >= _buckets.Length)
                            return false;
                        _currentBucket = _buckets[_bucketIndex];
                    }

                    // We iterate over the whole bucket including empty slots to keep the indices in sync
                    while (_valueIndexLocal < BucketSizeI) {
                        var suffix = _currentBucket.GetSlot(_valueIndexLocal);
                        if (suffix != 0)
                            return true;
                        _valueIndexLocal++;
                        _valueIndex++;
                    }
                }

                return false;
            }

            public void Reset () {
                _bucketIndex = -1;
                _valueIndex = -1;
                _valueIndexLocal = BucketSizeI;
            }
        }
    }
}
