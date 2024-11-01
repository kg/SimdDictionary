using System;
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
            private Entry[] _entries;
            private int _entryIndex, _entryCount;

            private ref Entry CurrentPair {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _entries[_entryIndex];
            }

            public K CurrentKey {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => CurrentPair.Key;
            }

            public V CurrentValue {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => CurrentPair.Value;
            }

            public KeyValuePair<K, V> Current {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get {
                    ref var pair = ref CurrentPair;
                    return new KeyValuePair<K, V>(pair.Key, pair.Value);
                }
            }
            object IEnumerator.Current => Current;

            DictionaryEntry IDictionaryEnumerator.Entry {
                get {
                    ref var pair = ref CurrentPair;
                    return new DictionaryEntry(pair.Key, pair.Value);
                }
            }

            object IDictionaryEnumerator.Key => CurrentKey;

            object? IDictionaryEnumerator.Value => CurrentValue;

            public Enumerator (SimdDictionary<K, V> dictionary) {
                _entries = dictionary._Entries;
                _entryIndex = -1;
                _entryCount = dictionary._Count;
            }

            public void Dispose () {
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext () {
                _entryIndex++;

                while (_entryIndex < _entryCount) {
                    ref var entry = ref _entries[_entryIndex];
                    var nextFreeSlot = entry.NextFreeSlotPlusOne;
                    if ((nextFreeSlot > 0) || (nextFreeSlot == FreeListIndexPlusOne_EndOfFreeList))
                        _entryIndex++;
                    else
                        return true;
                }

                return false;
            }

            public void Reset () {
                _entryIndex = -1;
            }
        }
    }
}
