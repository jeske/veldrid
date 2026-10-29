using System;
using System.Threading;
using Vortice.Direct3D11;

namespace Veldrid.D3D11
{
    /// <summary>One native generation's one-shot device-removal event and managed wait.</summary>
    internal sealed class D3D11DeviceLostRegistration : IDisposable
    {
        private readonly ID3D11Device4 notificationDevice;
        private readonly EventWaitHandle removedEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        private readonly int registrationCookie;
        private readonly RegisteredWaitHandle registeredWait;
        private int disposed;

        internal D3D11DeviceLostRegistration(ID3D11Device4 notificationDevice, Action signalOwner)
        {
            this.notificationDevice = notificationDevice;
            try
            {
                registrationCookie = notificationDevice.RegisterDeviceRemovedEvent(removedEvent.SafeWaitHandle.DangerousGetHandle());
                try
                {
                    registeredWait = ThreadPool.RegisterWaitForSingleObject(removedEvent, (_, timedOut) =>
                    {
                        // No native calls, locks on the device, or recovery here. The generation is
                        // captured by the caller, so a late queued message cannot target a new device.
                        if (!timedOut && Volatile.Read(ref disposed) == 0) signalOwner();
                    }, null, Timeout.Infinite, executeOnlyOnce: true);
                }
                catch
                {
                    notificationDevice.UnregisterDeviceRemoved(registrationCookie);
                    throw;
                }
            }
            catch
            {
                removedEvent.Dispose();
                notificationDevice.Dispose();
                throw;
            }
        }

        // App-local test: exercise the same wait/callback as the native signal, without TDR.
        internal void SignalForSimulation() => removedEvent.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                notificationDevice.UnregisterDeviceRemoved(registrationCookie);
            }
            finally
            {
                // Callback contract is enqueue-only: it never waits for this owner thread.
                using (var callbacksFinished = new ManualResetEvent(false))
                {
                    if (registeredWait.Unregister(callbacksFinished)) callbacksFinished.WaitOne();
                }
                removedEvent.Dispose();
                notificationDevice.Dispose();
            }
        }
    }
}