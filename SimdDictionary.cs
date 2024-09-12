using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SimdDictionary {

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct Bucket {
        public const uint CountSlot = 14,
            CascadeSlot = 15;

        [FieldOffset(0)]
        public Vector128<byte> Vector;
        [FieldOffset(0)]
        public fixed byte Slots[16];

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal readonly byte GetSlot (nuint index) =>
            Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref Unsafe.AsRef(in Vector)), index);

        // Use when modifying one or two slots, to avoid a whole-vector-load-then-store
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal void SetSlot (nuint index, byte value) =>
            Unsafe.AddByteOffset(ref Unsafe.As<Vector128<byte>, byte>(ref Vector), index) = value;

        public byte Count {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get => GetSlot(CountSlot);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetSlot(CountSlot, value);
        }

        public byte CascadeCount {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get => GetSlot(CascadeSlot);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => SetSlot(CascadeSlot, value);
        }
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
            Overflowed = -2,
            // Nothing cascaded out of the bucket so we can stop scanning
            NoOverflow = -1,
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

        internal readonly struct KeyComparer {
            public readonly IEqualityComparer<K>? Comparer;

            public KeyComparer (IEqualityComparer<K>? comparer) {
                if (typeof(K).IsValueType)
                    Comparer = comparer;
                else
                    Comparer = comparer ?? EqualityComparer<K>.Default;
            }

            public bool Equals (K lhs, K rhs) {
                if (typeof(K).IsValueType && (Comparer == null))
                    return EqualityComparer<K>.Default.Equals(lhs, rhs);
                else
                    return Comparer!.Equals(lhs, rhs);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            internal uint GetHashCode (K key) {
                if (Comparer == null)
                    return unchecked((uint)key!.GetHashCode());
                else
                    return unchecked((uint)Comparer.GetHashCode(key!));
            }
        }

        internal ref struct InternalEnumerator {
            public readonly Span<Bucket> Buckets;

            public readonly int InitialBucketIndex;
            public int BucketIndex, ElementIndex;
            public ref Bucket Bucket;

            /// <summary>
            /// Creates a fully-initialized InternalEnumerator pointed at the bucket you specify.
            /// You don't need to call MoveNext before using it!
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public InternalEnumerator (SimdDictionary<K, V> self, int bucketIndex = 0) {
                Buckets = self._Buckets;
                if (Buckets == null)
                    throw new NullReferenceException();
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
                    Bucket = ref Buckets[BucketIndex];
                    ElementIndex += BucketSizeI;
                }

                if (BucketIndex == InitialBucketIndex)
                    return false;
                else
                    return true;
            }

            public ref K Key (SimdDictionary<K, V> self, int index) =>
                ref self._Keys.AsSpan()[ElementIndex + index];

            public ref V Value (SimdDictionary<K, V> self, int index) =>
                ref self._Values.AsSpan()[ElementIndex + index];
        }

        public readonly KeyCollection Keys;
        public readonly ValueCollection Values;
        private readonly KeyComparer Comparer;
        private int _Count;
        private Bucket[] _Buckets;
        private K[] _Keys;
        private V[] _Values;

        public SimdDictionary () 
            : this (InitialCapacity, typeof(K).IsValueType ? null : EqualityComparer<K>.Default) {
        }

        public SimdDictionary (int capacity)
            : this (capacity, typeof(K).IsValueType ? null : EqualityComparer<K>.Default) {
        }

        public SimdDictionary (IEqualityComparer<K>? comparer)
            : this (InitialCapacity, comparer) {
        }

        public SimdDictionary (int capacity, IEqualityComparer<K>? comparer) {
            Unsafe.SkipInit(out _Buckets);
            Unsafe.SkipInit(out _Keys);
            Unsafe.SkipInit(out _Values);
            Comparer = new KeyComparer(comparer);
            EnsureCapacity(capacity);
            Keys = new KeyCollection(this);
            Values = new ValueCollection(this);
        }

        public SimdDictionary (SimdDictionary<K, V> source) 
            : this (source.Count, source.Comparer.Comparer) {
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

                for (uint j = 0, c = bucket.Count; j < c; j++) {
                    if (bucket.GetSlot(j) == 0)
                        continue;

                    if (TryInsert(oldKeys[baseIndex + j], oldValues[baseIndex + j], InsertMode.EnsureUnique) != InsertResult.OkAddedNew)
                        return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        internal static byte GetHashSuffix (uint hashCode) =>
            // The bottom bits of the hash form the bucket index, so we
            //  use the top bits of the hash as a suffix
            unchecked((byte)((hashCode >> 24) | SuffixSalt));

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        internal int GetBucketIndex (uint hashCode) =>
            unchecked((int)(hashCode & (uint)(_Buckets.Length - 1)));

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        internal int FindFirstMatchingSuffix (Vector128<byte> haystack, Vector128<byte> needle) {
            int result = 32;

            if (Sse2.IsSupported) {
                result = BitOperations.TrailingZeroCount(Sse2.MoveMask(Sse2.CompareEqual(needle, haystack)));
            } else if (AdvSimd.Arm64.IsSupported) {
                // Completely untested
                var matchVector = AdvSimd.CompareEqual(needle, haystack);
                var masked = AdvSimd.And(matchVector, Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128));
                var bits = AdvSimd.Arm64.AddAcross(masked.GetLower()).ToScalar() |
                    (AdvSimd.Arm64.AddAcross(masked.GetUpper()).ToScalar() << 8);
                result = BitOperations.TrailingZeroCount(bits);
            } else
                throw new NotImplementedException();

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int ScanBucket (InternalEnumerator enumerator, K needle, Vector128<byte> searchVector) {
            int count = enumerator.Bucket.Count,
                index = FindFirstMatchingSuffix(enumerator.Bucket.Vector, searchVector);
            for (; index < count; index++) {
                if (Comparer.Equals(enumerator.Key(this, index), needle))
                    return index;
            }

            return enumerator.Bucket.CascadeCount > 0
                ? (int)ScanBucketResult.Overflowed
                : (int)ScanBucketResult.NoOverflow;
        }

        internal void AdjustCascadeCounts (int firstBucketIndex, int lastBucketIndex, bool increase) {
            var enumerator = new InternalEnumerator(this, firstBucketIndex);
            do {
                if (enumerator.BucketIndex == lastBucketIndex)
                    return;

                byte cascadeCount = enumerator.Bucket.CascadeCount;
                if (cascadeCount < 255) {
                    if (increase) {
                        if (enumerator.Bucket.Count < BucketSizeI)
                            throw new Exception();

                        enumerator.Bucket.CascadeCount = (byte)(cascadeCount + 1);
                    }
                    else if (cascadeCount == 0)
                        throw new InvalidOperationException();
                    else
                        enumerator.Bucket.CascadeCount = (byte)(cascadeCount - 1);
                }
            } while (enumerator.MoveNext());
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
        internal ref V FindValue (K needle) {
            if (_Count == 0)
                return ref Unsafe.NullRef<V>();

            var hashCode = Comparer.GetHashCode(needle);
            var suffix = GetHashSuffix(hashCode);
            var searchVector = Vector128.Create(suffix);
            var firstBucketIndex = GetBucketIndex(hashCode);
            var enumerator = new InternalEnumerator(this, firstBucketIndex);

            do {
                var indexInBucket = ScanBucket(enumerator, needle, searchVector);
                if (indexInBucket >= 0)
                    return ref enumerator.Value(this, indexInBucket);
                else if (indexInBucket == (int)ScanBucketResult.NoOverflow)
                    return ref Unsafe.NullRef<V>();
            } while (enumerator.MoveNext());

            return ref Unsafe.NullRef<V>();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
        internal InsertResult TryInsert (K key, V value, InsertMode mode) {
            // FIXME: Load factor
            if ((_Keys == null) || (_Count >= _Keys.Length))
                return InsertResult.NeedToGrow;

            var hashCode = Comparer.GetHashCode(key);
            var suffix = GetHashSuffix(hashCode);
            var searchVector = Vector128.Create(suffix);
            var firstBucketIndex = GetBucketIndex(hashCode);
            var enumerator = new InternalEnumerator(this, firstBucketIndex);

            do {
                if (mode != InsertMode.Rehashing) {
                    var indexInBucket = ScanBucket(enumerator, key, searchVector);
                    if (indexInBucket >= 0) {
                        if (mode == InsertMode.EnsureUnique)
                            return InsertResult.KeyAlreadyPresent;
                        else {
                            if (mode == InsertMode.OverwriteKeyAndValue)
                                enumerator.Key(this, indexInBucket) = key;
                            enumerator.Value(this, indexInBucket) = value;
                            return InsertResult.OkOverwroteExisting;
                        }
                    }
                }

                var newIndexInBucket = enumerator.Bucket.Count;
                if (newIndexInBucket < BucketSizeU) {
                    enumerator.Bucket.Count = (byte)(newIndexInBucket + 1);
                    enumerator.Bucket.SetSlot(newIndexInBucket, suffix);
                    enumerator.Key(this, newIndexInBucket) = key;
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public bool Remove (K key) {
            throw new NotImplementedException();
        }

        bool ICollection<KeyValuePair<K, V>>.Remove (KeyValuePair<K, V> item) =>
            // FIXME: Check value
            Remove(item.Key);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
