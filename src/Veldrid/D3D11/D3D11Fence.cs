using System;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D11;

namespace Veldrid.D3D11
{
    /// <summary>
    ///     Direct3D11 <see cref="Fence"/> backed by a <c>D3D11_QUERY_EVENT</c>, so <see cref="Signaled"/> / <c>WaitForFence</c> reflect GPU
    ///     COMPLETION of the submission the fence was attached to (<c>_TASKS/GpuOrderingBarrier_vs_WaitForGpuIdle.md</c> D5). Before Nov 10 2026
    ///     this was a <c>ManualResetEvent</c> set right after <c>ExecuteCommandList</c> - i.e. it signalled when the CPU had handed the list to the
    ///     runtime, which made <c>WaitForFence</c> on D3D11 return immediately regardless of the GPU.
    ///     <para>
    ///         Lifecycle: created signalled or not (<see cref="initiallySignaled"/>); <see cref="Issue"/> is called by the device's submit path
    ///         (under the immediate-context lock) and records <c>End(query)</c> after the <c>ExecuteCommandList</c>; from then on the state is the
    ///         query's. <see cref="Reset"/> returns to the unsignalled, un-issued state. An EVENT query may be <c>End</c>ed again after it was
    ///         read - the canonical cycle.
    ///     </para>
    /// </summary>
    internal sealed class D3D11Fence : Fence
    {
        public override bool Signaled => issued ? gd.PollEventQuery(query) : initiallySignaled;
        public override bool IsDisposed => disposed;
        public override string Name
        {
            get => name;
            set { name = value; query.DebugName = value; }
        }

        private readonly D3D11GraphicsDevice gd;
        private readonly ID3D11Query query;
        private volatile bool issued;
        private volatile bool initiallySignaled;
        private string name;
        private bool disposed;

        public D3D11Fence(D3D11GraphicsDevice gd, bool signaled)
        {
            this.gd = gd;
            initiallySignaled = signaled;
            query = gd.Device.CreateQuery(new QueryDescription(QueryType.Event));
        }

        /// <summary>Called by <c>D3D11GraphicsDevice.SubmitCommandsCore</c> while holding the immediate-context lock, right after <c>ExecuteCommandList</c>.</summary>
        internal void Issue(ID3D11DeviceContext immediateContext)
        {
            immediateContext.End(query);
            issued = true;
        }

        public override void Reset()
        {
            issued = false;
            initiallySignaled = false;
        }

        /// <summary>Polls until signalled or the timeout elapses. Spins briefly, then yields; <c>ulong.MaxValue</c> = wait forever.</summary>
        internal bool Wait(ulong nanosecondTimeout)
        {
            if (Signaled) return true;
            long deadlineTicks = nanosecondTimeout == ulong.MaxValue ? long.MaxValue : Stopwatch.GetTimestamp() + (long)(nanosecondTimeout / 1_000_000_000.0 * Stopwatch.Frequency);
            var spin = new SpinWait();
            while (Stopwatch.GetTimestamp() < deadlineTicks)
            {
                if (spin.Count < 32) spin.SpinOnce(); else Thread.Sleep(0);
                if (Signaled) return true;
            }
            return Signaled;
        }

        #region Disposal

        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;
            query.Dispose();
        }

        #endregion
    }
}