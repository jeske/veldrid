using System;
using System.Buffers;

namespace Veldrid
{
    /// <summary>Owned copy of a bound set's dynamic offsets: inline for the common small case, pooled above that. Filled from a
    /// length-carrying span so the copy is bounds-checked by construction.</summary>
    internal unsafe struct SmallFixedOrDynamicArray : IDisposable
    {
        private const int max_fixed_values = 5;

        public readonly uint Count;
        private fixed uint fixedData[max_fixed_values];
        public readonly uint[] Data;

        public uint Get(uint i)
        {
            return Count > max_fixed_values ? Data[i] : fixedData[i];
        }

        public SmallFixedOrDynamicArray(ReadOnlySpan<uint> data)
        {
            if (data.Length > max_fixed_values)
            {
                Data = ArrayPool<uint>.Shared.Rent(data.Length);
                data.CopyTo(Data);
            }
            else
            {
                for (int i = 0; i < data.Length; i++) fixedData[i] = data[i];

                Data = null;
            }

            Count = (uint)data.Length;
        }

        public void Dispose()
        {
            if (Data != null) ArrayPool<uint>.Shared.Return(Data);
        }
    }
}