using System;

namespace Veldrid
{
    /// <summary>
    ///     Why the GPU device became unusable. Mirrors the DXGI device-removed reasons; other backends map their
    ///     equivalents (VK_ERROR_DEVICE_LOST ...) onto <see cref="Unknown"/> until they get a finer classification.
    /// </summary>
    public enum GpuDeviceLostReason
    {
        /// <summary>DXGI_ERROR_DEVICE_REMOVED (0x887A0005) — driver crash, driver upgrade/reinstall, adapter disabled.</summary>
        DeviceRemoved = 0,
        /// <summary>DXGI_ERROR_DEVICE_RESET (0x887A0007) — TDR: the driver reset the GPU after a timeout.</summary>
        DeviceReset = 1,
        /// <summary>DXGI_ERROR_DEVICE_HUNG (0x887A0006) — the GPU stopped responding to this application's commands.</summary>
        DeviceHung = 2,
        /// <summary>DXGI_ERROR_DRIVER_INTERNAL_ERROR (0x887A0020).</summary>
        DriverInternalError = 3,
        /// <summary>Lost for a reason the backend could not classify (or a backend without typed reasons).</summary>
        Unknown = 4,
        /// <summary><see cref="GraphicsDevice.SimulateDeviceLost"/> — a debug-only synthetic loss, no native failure occurred.</summary>
        Simulated = 5,
    }

    /// <summary>
    ///     Monotonic counter identifying the lifetime of the NATIVE device behind a <see cref="GraphicsDevice"/>.
    ///     Bumped by <see cref="GraphicsDevice.RecoverFromDeviceLost"/>. Consumers that cache device-derived handles
    ///     snapshot the generation at creation and compare at use time: a mismatch means the handle is dead.
    /// </summary>
    public readonly record struct GpuDeviceGeneration(int Value)
    {
        /// <summary>Generation of the device created by the constructor (before any recovery).</summary>
        public static readonly GpuDeviceGeneration Initial = new(1);

        /// <summary>The next generation after this one.</summary>
        public GpuDeviceGeneration Next() => new(Value + 1);

        public override string ToString() => $"gen{Value}";
    }

    /// <summary>
    ///     The GPU device behind this <see cref="GraphicsDevice"/> has been removed or reset by the OS/driver (TDR,
    ///     driver crash, driver reinstall). Every child resource is invalid. The owner must stop rendering and call
    ///     <see cref="GraphicsDevice.RecoverFromDeviceLost"/>, then rebuild all resources.
    ///     <para>
    ///     Thrown from the device-level choke points (<c>SwapBuffers</c>, <c>SubmitCommands</c>, <c>Map</c>,
    ///     <c>UpdateBuffer</c>/<c>UpdateTexture</c>, <c>CommandList.End</c>, <c>Swapchain.Resize</c>) INSTEAD of the
    ///     raw native exception, so callers can catch one typed exception rather than pattern-matching HRESULTs.
    ///     </para>
    /// </summary>
    public class VeldridDeviceLostException : VeldridException
    {
        /// <summary>Classified reason for the loss.</summary>
        public GpuDeviceLostReason LostReason { get; }

        /// <summary>Raw native reason code (DXGI HRESULT, VkResult ...). 0 when not applicable (e.g. simulated).</summary>
        public int NativeReasonCode { get; }

        public VeldridDeviceLostException(GpuDeviceLostReason lostReason, int nativeReasonCode, string message, Exception innerException)
            : base(message, innerException)
        {
            LostReason = lostReason;
            NativeReasonCode = nativeReasonCode;
        }

        public VeldridDeviceLostException(GpuDeviceLostReason lostReason, int nativeReasonCode, string message)
            : base(message)
        {
            LostReason = lostReason;
            NativeReasonCode = nativeReasonCode;
        }
    }
}