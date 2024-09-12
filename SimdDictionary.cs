using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Bucket = System.Runtime.Intrinsics.Vector128<byte>;

namespace SimdDictionary {

    internal unsafe static class BucketExtensions {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetSlot (this ref readonly Bucket self, int index) =>
            self[index];

        // Use when modifying one or two slots, to avoid a whole-vector-load-then-store
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetSlot (this ref Bucket self, nuint index, byte value) =>
            Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref self), index) = value;
    }

    public partial class SimdDictionary<K, V> : IDictionary<K, V>, ICloneable {
        internal enum InsertMode {
            // Fail the insertion if a matching key is found
            EnsureUnique,
            // Overwrite the value if a matching key is found
            OverwriteValue,
            // Overwrite both the key and value if a matching key is found
            OverwriteKeyAndValue,
            // Don't scan for existing matches before inserting into the bucket
            Rehashing
        }

        internal enum InsertResult {
            OkAddedNew,
            OkOverwroteExisting,
            NeedToGrow,
            KeyAlreadyPresent,
        }

        // Special result indexes when the key is not found in the bucket
        internal enum ScanBucketResult : int {
            // One or more items cascaded out of the bucket so we need to keep scanning
            Overflowed = 33,
            // Nothing cascaded out of the bucket so we can stop scanning
            NoOverflow = 34,
        }

        public const int InitialCapacity = 0,
            // User-specified capacity values will be increased to this percentage in order
            //  to maintain an ideal load factor. FIXME: 120 isn't right
            OversizePercentage = 120,
            BucketSizeI = 14;

        public const uint
            // We need to make sure suffixes are never zero, and it's
            //  more likely that a bad hash will collide at the top bit
            //  than at the bottom (i.e. using an int/ptr as its own hash)
            SuffixSalt = 0b10000000,
            BucketSizeU = 14;

        internal ref struct InternalEnumerator {
            public const int CountSlot = 14,
                CascadeSlot = 15;

            public readonly Span<Bucket> Buckets;
            public readonly Span<K> Keys;

            public readonly int InitialBucketIndex;
            public int BucketIndex, ElementIndex;
            // FIXME: We could potentially optimize MoveNext by getting rid of this
            public ref Bucket Bucket;

            /// <summary>
            /// Creates a fully-initialized InternalEnumerator pointed at the bucket you specify.
            /// You don't need to call MoveNext before using it!
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public InternalEnumerator (SimdDictionary<K, V> self, int bucketIndex = 0)
                : this (self._Buckets, self._Keys, bucketIndex) {
                if (self._Buckets == null)
                    throw new NullReferenceException();
            }

            private InternalEnumerator (Span<Bucket> buckets, Span<K> keys, int bucketIndex) {
                Buckets = buckets;
                Keys = keys;
                InitialBucketIndex = BucketIndex = bucketIndex;
                ElementIndex = bucketIndex * BucketSizeI;
                Bucket = ref Buckets[bucketIndex];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext () {
                if (++BucketIndex >= Buckets.Length) {
                    BucketIndex = ElementIndex = 0;
                    Bucket = ref Buckets[0];
                } else {
                    // FIXME: Unsafe.Add doesn't work here
                    Bucket = ref Buckets[BucketIndex];
                    ElementIndex += BucketSizeI;
                }

                if (BucketIndex == InitialBucketIndex)
                    return false;
                else
                    return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal readonly int ScanBucket (IEqualityComparer<K>? comparer, K needle, byte suffix) {
                // Eagerly load the bucket into a local, otherwise each reference to 'Bucket' will do an indirect load
                var bucket = Bucket;

                int index;
                // We create the search vector on the fly with Vector128.Create(suffix) because passing it as a param from outside
                //  seems to cause RyuJIT to generate inferior code instead of flowing it through a register even when inlined
                if (Sse2.IsSupported) {
                    index = BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.CompareEqual(Vector128.Create(suffix), bucket)));
                } else if (AdvSimd.Arm64.IsSupported) {
                    // Completely untested
                    var matchVector = AdvSimd.CompareEqual(Vector128.Create(suffix), bucket);
                    var masked = AdvSimd.And(matchVector, Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128));
                    var bits = AdvSimd.Arm64.AddAcross(masked.GetLower()).ToScalar() |
                        (AdvSimd.Arm64.AddAcross(masked.GetUpper()).ToScalar() << 8);
                    index = BitOperations.TrailingZeroCount(bits);
                } else
                    // FIXME: Scalar implementation like dn_simdhash's
                    throw new NotImplementedException();

                // We need to duplicate the loop header logic and move it inside the if, otherwise
                //  count gets spilled to the stack.
                if (typeof(K).IsValueType && (comparer == null)) {
                    // We do this instead of a for-loop so we can skip ReadKey when there's no match,
                    //  which improves performance for missing items and/or hash collisions
                    int count = bucket[CountSlot];
                    if (index >= count)
                        goto no_match;

                    ref K key = ref ReadKey(index);
                    do {
                        if (EqualityComparer<K>.Default.Equals(needle, key))
                            return index;
                        index++;
                        key = ref Unsafe.Add(ref key, 1);
                    } while (index < count);
                } else {
                    int count = bucket[CountSlot];
                    if (index >= count)
                        goto no_match;

                    ref K key = ref ReadKey(index);
                    do {
                        if (comparer!.Equals(needle, key))
                            return index;
                        index++;
                        key = ref Unsafe.Add(ref key, 1);
                    } while (index < count);
                }

                no_match:
                    return bucket[CascadeSlot] > 0
                        ? (int)ScanBucketResult.Overflowed
                        : (int)ScanBucketResult.NoOverflow;
            }

            public byte BucketCount {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                readonly get => Bucket[CountSlot];
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => Bucket.SetSlot(CountSlot, value);
            }

            public byte CascadeCount {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                readonly get => Bucket[CascadeSlot];
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => Bucket.SetSlot(CascadeSlot, value);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly ref K ReadKey (int index) =>
                ref Keys[ElementIndex + index];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref K Key (int index) =>
                ref Keys[ElementIndex + index];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref V Value (SimdDictionary<K, V> self, int index) =>
                ref self._Values[ElementIndex + index];

            /// <summary>
            /// Creates a fully-initialized InternalEnumerator pointed at the correct bucket for the specified hashcode.
            /// You don't need to call MoveNext before using it!
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static InternalEnumerator ForHashCode (SimdDictionary<K, V> self, uint hashCode) {
                var buckets = (Span<Bucket>)self._Buckets;
                var bucketIndex = unchecked((int)(hashCode & (uint)(buckets.Length - 1)));
                return new InternalEnumerator(buckets, self._Keys, bucketIndex);
            }
        }

        public readonly KeyCollection Keys;
        public readonly ValueCollection Values;
        public readonly IEqualityComparer<K>? Comparer;
        private int _Count;
        private Bucket[] _Buckets;
        private K[] _Keys;
        private V[] _Values;

        public SimdDictionary () 
            : this (InitialCapacity, null) {
        }

        public SimdDictionary (int capacity)
            : this (capacity, null) {
        }

        public SimdDictionary (IEqualityComparer<K>? comparer)
            : this (InitialCapacity, comparer) {
        }

        public SimdDictionary (int capacity, IEqualityComparer<K>? comparer) {
            Unsafe.SkipInit(out _Buckets);
            Unsafe.SkipInit(out _Keys);
            Unsafe.SkipInit(out _Values);
            if (typeof(K).IsValueType)
                Comparer = comparer;
            else
                Comparer = comparer ?? EqualityComparer<K>.Default;            
            EnsureCapacity(capacity);
            Keys = new KeyCollection(this);
            Values = new ValueCollection(this);
        }

        public SimdDictionary (SimdDictionary<K, V> source) 
            : this (source.Count, source.Comparer) {
            // FIXME: Optimize this
            foreach (var kvp in source)
                Add(kvp.Key, kvp.Value);
        }

        static int AdjustCapacity (int capacity) {
            if (capacity < 1)
                capacity = 1;
            var bucketCount = ((capacity + BucketSizeI - 1) / BucketSizeI);
            // Power-of-two bucket counts enable using & (count - 1) instead of mod count
            var npot = BitOperations.RoundUpToPowerOf2((uint)bucketCount);
            return (int)(npot * BucketSizeI);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity (int capacity) {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            capacity = AdjustCapacity(capacity * OversizePercentage / 100);

            if ((_Buckets != null) && (Capacity >= capacity))
                return;

            int nextIncrement = (_Buckets == null)
                ? capacity
                : Capacity * 2;

            if (!TryResize(Math.Max(capacity, nextIncrement)))
                throw new Exception("Internal error: Failed to resize");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureSpaceForNewItem () {
            // FIXME: Maintain good load factor
            EnsureCapacity(_Count + 1);
        }

        internal bool TryResize (int capacity) {
            var oldCount = _Count;
            var bucketCount = capacity / BucketSizeI;
            var oldBuckets = _Buckets;
            var oldKeys = _Keys;
            var oldValues = _Values;
            _Buckets = new Bucket[bucketCount];
            _Keys = new K[capacity];
            _Values = new V[capacity];
            if (oldBuckets != null)
                if (!TryRehash(oldBuckets, oldKeys, oldValues))
                    return false;
            return true;
        }

        internal bool TryRehash (Bucket[] oldBuckets, K[] oldKeys, V[] oldValues) {
            for (uint i = 0; i < oldBuckets.Length; i++) {
                ref var bucket = ref oldBuckets[i];
                var baseIndex = i * BucketSizeU;

                for (int j = 0, c = bucket.GetSlot((int)InternalEnumerator.CountSlot); j < c; j++) {
                    if (bucket.GetSlot(j) == 0)
                        continue;

                    if (TryInsert(oldKeys[baseIndex + j], oldValues[baseIndex + j], InsertMode.Rehashing) != InsertResult.OkAddedNew)
                        return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint GetHashCode (IEqualityComparer<K>? comparer, K key) {
            if (comparer == null)
                return unchecked((uint)key!.GetHashCode());
            else
                return unchecked((uint)comparer.GetHashCode(key!));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetHashSuffix (uint hashCode) =>
            // The bottom bits of the hash form the bucket index, so we
            //  use the top bits of the hash as a suffix
            unchecked((byte)((hashCode >> 24) | SuffixSalt));

        // FIXME: Optimize out the extra enumerator by walking backwards or something
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void AdjustCascadeCounts (int firstBucketIndex, int lastBucketIndex, bool increase) {
            var enumerator = new InternalEnumerator(this, firstBucketIndex);
            do {
                if (enumerator.BucketIndex == lastBucketIndex)
                    return;

                byte cascadeCount = enumerator.CascadeCount;
                if (cascadeCount < 255) {
                    if (increase) {
                        if (enumerator.BucketCount < BucketSizeI)
                            throw new Exception();

                        enumerator.CascadeCount = (byte)(cascadeCount + 1);
                    }
                    else if (cascadeCount == 0)
                        throw new InvalidOperationException();
                    else
                        enumerator.CascadeCount = (byte)(cascadeCount - 1);
                }
            } while (enumerator.MoveNext());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref V FindValue (K needle) {
            if (_Count == 0)
                return ref Unsafe.NullRef<V>();

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, needle);
            var suffix = GetHashSuffix(hashCode);
            var enumerator = InternalEnumerator.ForHashCode(this, hashCode);

            do {
                var indexInBucket = enumerator.ScanBucket(comparer, needle, suffix);
                // FIXME: Find a way to do a single comparison here instead of 2? Not sure if there's a good option.
                if (indexInBucket < BucketSizeI)
                    return ref enumerator.Value(this, indexInBucket);
                else if (indexInBucket == (int)ScanBucketResult.NoOverflow)
                    return ref Unsafe.NullRef<V>();
            } while (enumerator.MoveNext());

            return ref Unsafe.NullRef<V>();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal InsertResult TryInsert (K key, V value, InsertMode mode) {
            // FIXME: Load factor
            if ((_Keys == null) || (_Count >= _Keys.Length))
                return InsertResult.NeedToGrow;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var enumerator = InternalEnumerator.ForHashCode(this, hashCode);
            var firstBucketIndex = enumerator.BucketIndex;

            do {
                if (mode != InsertMode.Rehashing) {
                    var indexInBucket = enumerator.ScanBucket(comparer, key, suffix);
                    if (indexInBucket < BucketSizeI) {
                        if (mode == InsertMode.EnsureUnique)
                            return InsertResult.KeyAlreadyPresent;
                        else {
                            if (mode == InsertMode.OverwriteKeyAndValue)
                                enumerator.Key(indexInBucket) = key;
                            enumerator.Value(this, indexInBucket) = value;
                            return InsertResult.OkOverwroteExisting;
                        }
                    }
                }

                var newIndexInBucket = enumerator.BucketCount;
                if (newIndexInBucket < BucketSizeU) {
                    enumerator.BucketCount = (byte)(newIndexInBucket + 1);
                    enumerator.Bucket.SetSlot(newIndexInBucket, suffix);
                    enumerator.Key(newIndexInBucket) = key;
                    enumerator.Value(this, newIndexInBucket) = value;
                    if (firstBucketIndex != enumerator.BucketIndex)
                        AdjustCascadeCounts(firstBucketIndex, enumerator.BucketIndex, true);
                    return InsertResult.OkAddedNew;
                } else
                    ;
            } while (enumerator.MoveNext());

            // FIXME: This shouldn't be possible
            return InsertResult.NeedToGrow;
        }

        public V this[K key] { 
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                if (!TryGetValue(key, out var result))
                    throw new KeyNotFoundException();
                else
                    return result;
            }
            // FIXME: In-place modification
            set => Add(key, value);
        }

        ICollection<K> IDictionary<K, V>.Keys => Keys;
        ICollection<V> IDictionary<K, V>.Values => Values;

        public int Count => _Count;
        public int Capacity => _Buckets.Length * BucketSizeI;

        bool ICollection<KeyValuePair<K, V>>.IsReadOnly => false;

        public void Add (K key, V value) {
            var ok = TryAdd(key, value);
            if (!ok)
                throw new ArgumentException($"Key already exists: {key}");
        }

        public bool TryAdd (K key, V value) {
            EnsureSpaceForNewItem();

        retry:
            var insertResult = TryInsert(key, value, InsertMode.EnsureUnique);
            switch (insertResult) {
                case InsertResult.OkAddedNew:
                    _Count++;
                    return true;
                case InsertResult.NeedToGrow:
                    TryResize(Capacity + 1);
                    goto retry;
                default:
                    return false;
            }
        }

        void ICollection<KeyValuePair<K, V>>.Add (KeyValuePair<K, V> item) =>
            Add(item.Key, item.Value);

        public void Clear () {
            _Count = 0;
            Array.Clear(_Buckets);
            Array.Clear(_Values);
        }

        bool ICollection<KeyValuePair<K, V>>.Contains (KeyValuePair<K, V> item) =>
            TryGetValue(item.Key, out var value) &&
            (value?.Equals(item.Value) == true);

        public bool ContainsKey (K key) {
            return !Unsafe.IsNullRef(ref FindValue(key));
        }

        void ICollection<KeyValuePair<K, V>>.CopyTo (KeyValuePair<K, V>[] array, int arrayIndex) {
            using (var e = GetEnumerator())
                while (e.MoveNext())
                    array[arrayIndex++] = e.Current;
        }

        public Enumerator GetEnumerator () =>
            new Enumerator(this);

        IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator () =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator () =>
            GetEnumerator();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Remove (K key) {
            if (_Count == 0)
                return false;

            var comparer = Comparer;
            var hashCode = GetHashCode(comparer, key);
            var suffix = GetHashSuffix(hashCode);
            var enumerator = InternalEnumerator.ForHashCode(this, hashCode);
            
            do {
                var indexInBucket = enumerator.ScanBucket(comparer, key, suffix);
                if (indexInBucket < BucketSizeI) {
                    ref var bucket = ref enumerator.Bucket;
                    int bucketCount = enumerator.BucketCount,
                        replacementIndexInBucket = bucketCount - 1;

                    unchecked {
                        _Count--;
                        enumerator.BucketCount--;
                        bucket.SetSlot((uint)indexInBucket, bucket.GetSlot(replacementIndexInBucket));
                        bucket.SetSlot((uint)replacementIndexInBucket, 0);
                        enumerator.Key(indexInBucket) = enumerator.ReadKey((int)replacementIndexInBucket);
                        enumerator.Value(this, indexInBucket) = enumerator.Value(this, (int)replacementIndexInBucket);
                        enumerator.Key((int)replacementIndexInBucket) = default!;
                        enumerator.Value(this, (int)replacementIndexInBucket) = default!;
                    }

                    if (enumerator.BucketIndex != enumerator.InitialBucketIndex)
                        AdjustCascadeCounts(enumerator.InitialBucketIndex, enumerator.BucketIndex, false);

                    return true;
                } else if (indexInBucket == (int)ScanBucketResult.NoOverflow)
                    return false;
                else if (indexInBucket == (int)ScanBucketResult.Overflowed)
                    continue;
            } while (enumerator.MoveNext());

            return false;
        }

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue (K key, out V value) {
            ref var result = ref FindValue(key);
            if (Unsafe.IsNullRef(ref result)) {
                value = default!;
                return false;
            } else {
                value = result;
                return true;
            }
        }

        public object Clone () =>
            new SimdDictionary<K, V>(this);
    }
}
