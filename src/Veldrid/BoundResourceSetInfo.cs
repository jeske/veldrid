using System;

namespace Veldrid
{
    internal struct BoundResourceSetInfo : IEquatable<BoundResourceSetInfo>
    {
        public ResourceSet Set;
        public SmallFixedOrDynamicArray Offsets;

        public BoundResourceSetInfo(ResourceSet set, ReadOnlySpan<uint> offsets)
        {
            Set = set;
            Offsets = new SmallFixedOrDynamicArray(offsets);
        }

        public bool Equals(ResourceSet set, ReadOnlySpan<uint> offsets)
        {
            if (set != Set || offsets.Length != Offsets.Count) return false;

            for (int i = 0; i < offsets.Length; i++)
            {
                if (offsets[i] != Offsets.Get((uint)i))
                    return false;
            }

            return true;
        }

        public bool Equals(BoundResourceSetInfo other)
        {
            if (Set != other.Set || Offsets.Count != other.Offsets.Count) return false;

            for (uint i = 0; i < Offsets.Count; i++)
            {
                if (Offsets.Get(i) != other.Offsets.Get(i)) return false;
            }

            return true;
        }
    }
}