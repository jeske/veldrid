using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;
using Vortice.DXGI;
using Vortice.DXGI.Debug;
using Vortice.Mathematics;
using Feature = Vortice.Direct3D11.Feature;
using MapFlags = Vortice.Direct3D11.MapFlags;
using VorticeDXGI = Vortice.DXGI.DXGI;
using VorticeD3D11 = Vortice.Direct3D11.D3D11;

namespace Veldrid.D3D11
{
    internal class D3D11GraphicsDevice : GraphicsDevice
    {
        public override string DeviceName => deviceName;

        public override string VendorName => vendorName;

        public override GraphicsApiVersion ApiVersion { get; }

        public override GraphicsBackend BackendType => GraphicsBackend.Direct3D11;

        public override bool IsUvOriginTopLeft => true;

        public override bool IsDepthRangeZeroToOne => true;

        public override bool IsClipSpaceYInverted => false;

        public override ResourceFactory ResourceFactory => d3d11ResourceFactory;

        public ID3D11Device Device => device;

        public IDXGIAdapter Adapter => dxgiAdapter;

        public bool IsDebugEnabled { get; }

        public bool SupportsConcurrentResources => supportsConcurrentResources;

        public bool SupportsCommandLists => supportsCommandLists;

        public int DeviceId => deviceId;

        public override Swapchain MainSwapchain => mainSwapchain;

        public override GraphicsDeviceFeatures Features { get; }

        public override bool AllowTearing
        {
            get => mainSwapchain.AllowTearing;
            set => mainSwapchain.AllowTearing = value;
        }

        // NOT readonly: swapped in place by RecoverFromDeviceLost (spec 1000 D2 — same GraphicsDevice identity,
        // fresh native device). Every access to these happens under immediateContextLock or on the owner's
        // single render thread; the recovery contract requires all rendering threads to be stopped.
        private IDXGIAdapter dxgiAdapter;
        private ID3D11Device device;
        private ID3D11DeviceContext immediateContext;
        private D3D11ResourceFactory d3d11ResourceFactory;
        private string deviceName;
        private string vendorName;
        private int deviceId;
        private readonly DeviceCreationFlags nativeDeviceCreationFlags;

        /// <summary>Set by SimulateDeviceLost; cleared by RecoverFromDeviceLost. Makes the next Submit/Present throw.</summary>
        private volatile bool simulatedDeviceLost;
        /// <summary>Sticky once any choke point classified a native failure as device-lost; cleared by recovery.</summary>
        private volatile bool deviceLostObserved;

        private readonly D3D11Swapchain mainSwapchain;
        private readonly bool supportsConcurrentResources;
        private readonly bool supportsCommandLists;
        private readonly object immediateContextLock = new object();
        private readonly BackendInfoD3D11 d3d11Info;

        private readonly object mappedResourceLock = new object();

        private readonly Dictionary<MappedResourceCacheKey, MappedResourceInfo> mappedResources
            = new Dictionary<MappedResourceCacheKey, MappedResourceInfo>();

        private readonly object stagingResourcesLock = new object();
        private readonly List<D3D11Buffer> availableStagingBuffers = new List<D3D11Buffer>();

        private readonly object resetEventsLock = new object();
        private readonly List<ManualResetEvent[]> resetEvents = new List<ManualResetEvent[]>();

        public D3D11GraphicsDevice(GraphicsDeviceOptions options, D3D11DeviceOptions d3D11DeviceOptions, SwapchainDescription? swapchainDesc)
            : this(mergeOptions(d3D11DeviceOptions, options), swapchainDesc)
        {
        }

        public D3D11GraphicsDevice(D3D11DeviceOptions options, SwapchainDescription? swapchainDesc)
        {
            var flags = (DeviceCreationFlags)options.DeviceCreationFlags;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif
            // If debug flag set but SDK layers aren't available we can't enable debug.
            if (0 != (flags & DeviceCreationFlags.Debug) && !VorticeD3D11.SdkLayersAvailable()) flags &= ~DeviceCreationFlags.Debug;
            nativeDeviceCreationFlags = flags;

            createNativeDeviceAndAdapter(options.AdapterPtr);

            switch (device.FeatureLevel)
            {
                case FeatureLevel.Level_10_0:
                    ApiVersion = new GraphicsApiVersion(10, 0, 0, 0);
                    break;

                case FeatureLevel.Level_10_1:
                    ApiVersion = new GraphicsApiVersion(10, 1, 0, 0);
                    break;

                case FeatureLevel.Level_11_0:
                    ApiVersion = new GraphicsApiVersion(11, 0, 0, 0);
                    break;

                case FeatureLevel.Level_11_1:
                    ApiVersion = new GraphicsApiVersion(11, 1, 0, 0);
                    break;

                case FeatureLevel.Level_12_0:
                    ApiVersion = new GraphicsApiVersion(12, 0, 0, 0);
                    break;

                case FeatureLevel.Level_12_1:
                    ApiVersion = new GraphicsApiVersion(12, 1, 0, 0);
                    break;

                case FeatureLevel.Level_12_2:
                    ApiVersion = new GraphicsApiVersion(12, 2, 0, 0);
                    break;
            }

            if (swapchainDesc != null)
            {
                var desc = swapchainDesc.Value;
                mainSwapchain = new D3D11Swapchain(this, ref desc);
            }

            immediateContext = device.ImmediateContext;
            device.CheckThreadingSupport(out supportsConcurrentResources, out supportsCommandLists);

            IsDebugEnabled = (flags & DeviceCreationFlags.Debug) != 0;

            Features = new GraphicsDeviceFeatures(
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                device.FeatureLevel >= FeatureLevel.Level_11_0,
                true,
                device.FeatureLevel >= FeatureLevel.Level_11_1,
                device.FeatureLevel >= FeatureLevel.Level_11_1,
                device.CheckFeatureSupport<FeatureDataDoubles>(Feature.Doubles).DoublePrecisionFloatShaderOps);

            d3d11ResourceFactory = new D3D11ResourceFactory(this);
            d3d11Info = new BackendInfoD3D11(this);

            PostDeviceCreated();
            RegisterDeviceLostNotification();
        }

        private D3D11DeviceLostRegistration deviceLostRegistration;

        public override bool SupportsDeviceLostNotifications => deviceLostRegistration != null;

        private void RegisterDeviceLostNotification()
        {
            // Native registration is per generation. ID3D11Device4 is runtime-dependent,
            // not tied to the adapter's feature level. Existing HRESULT detection remains available.
            var notificationDevice = device.QueryInterfaceOrNull<ID3D11Device4>();
            if (notificationDevice == null) return;
            GpuDeviceGeneration generation = DeviceGeneration;
            try
            {
                deviceLostRegistration = new D3D11DeviceLostRegistration(notificationDevice,
                    () => NotifyDeviceLost(generation));
            }
            catch (Exception error)
            {
                // The registration constructor releases its owned interface on failure.
                Trace.TraceWarning($"D3D11 asynchronous device-loss registration unavailable: {error}");
            }
        }

        public override void ThrowIfDeviceLost()
        {
            if (simulatedDeviceLost) throwIfDeviceKnownLost();
            if (device != null)
            {
                Result reason = device.DeviceRemovedReason;
                if (reason.Failure)
                    throw tryClassifyDeviceLost(reason.Code, null);
            }
            throwIfDeviceKnownLost();
        }

        /// <summary>
        ///     Creates the ID3D11Device + DXGI adapter and fills the identity fields. Shared by the constructor and
        ///     <see cref="RecoverFromDeviceLost"/> so both take the same feature-level list, debug-flag logic and
        ///     WARP/empty-feature-level fallback. Throws (SharpGenException) when no device can be created — during a
        ///     driver reinstall window this is the normal "not yet" signal.
        /// </summary>
        private void createNativeDeviceAndAdapter(IntPtr preferredAdapterPtr)
        {
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_0
            };

            try
            {
                VorticeD3D11.D3D11CreateDevice(preferredAdapterPtr,
                    DriverType.Hardware,
                    nativeDeviceCreationFlags,
                    featureLevels,
                    out device).CheckError();
            }
            catch
            {
                VorticeD3D11.D3D11CreateDevice(IntPtr.Zero,
                    DriverType.Hardware,
                    nativeDeviceCreationFlags,
                    Array.Empty<FeatureLevel>(),
                    out device).CheckError();
            }

            using (var dxgiDevice = device!.QueryInterface<IDXGIDevice>())
            {
                // Store a pointer to the DXGI adapter.
                // This is for the case of no preferred DXGI adapter, or fallback to WARP.
                dxgiDevice.GetAdapter(out dxgiAdapter).CheckError();

                var desc = dxgiAdapter.Description;
                deviceName = desc.Description;
                vendorName = "id:" + ((uint)desc.VendorId).ToString("x8");
                deviceId = desc.DeviceId;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
        // DEVICE LOST: detection
        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

        private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
        private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
        private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
        private const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);

        public override bool IsDeviceLost
        {
            get
            {
                if (simulatedDeviceLost || deviceLostObserved) return true;
                ID3D11Device currentDevice = device;
                if (currentDevice == null) return true; // torn down by a failed recovery attempt
                try
                {
                    return currentDevice.DeviceRemovedReason.Failure;
                }
                catch
                {
                    return true;
                }
            }
        }

        private static GpuDeviceLostReason? classifyDxgiCode(int hresult)
        {
            switch (hresult)
            {
                case DxgiErrorDeviceRemoved: return GpuDeviceLostReason.DeviceRemoved;
                case DxgiErrorDeviceReset: return GpuDeviceLostReason.DeviceReset;
                case DxgiErrorDeviceHung: return GpuDeviceLostReason.DeviceHung;
                case DxgiErrorDriverInternalError: return GpuDeviceLostReason.DriverInternalError;
                default: return null;
            }
        }

        /// <summary>
        ///     Decide whether a native failure means "the device is gone". First by the failing call's own HRESULT,
        ///     then — because many calls fail with a generic E_FAIL/E_INVALIDARG once the device is removed — by asking
        ///     the device itself (<c>GetDeviceRemovedReason</c>). Returns null when the failure is NOT a device loss.
        ///     Marks <see cref="deviceLostObserved"/> so later calls short-circuit via <see cref="throwIfDeviceKnownLost"/>.
        /// </summary>
        private VeldridDeviceLostException tryClassifyDeviceLost(int failingCallHResult, Exception innerException)
        {
            GpuDeviceLostReason? reason = classifyDxgiCode(failingCallHResult);
            int reasonCode = failingCallHResult;

            if (reason == null)
            {
                ID3D11Device currentDevice = device;
                if (currentDevice == null) return null;
                Result removedReason;
                try
                {
                    removedReason = currentDevice.DeviceRemovedReason;
                }
                catch
                {
                    return null;
                }
                if (removedReason.Success) return null;
                reasonCode = removedReason.Code;
                reason = classifyDxgiCode(removedReason.Code) ?? GpuDeviceLostReason.Unknown;
            }

            deviceLostObserved = true;
            string message = $"The Direct3D 11 device was lost ({reason}, DXGI reason 0x{(uint)reasonCode:X8}). " +
                             "All GPU resources are invalid; call GraphicsDevice.RecoverFromDeviceLost() and rebuild them.";
            return innerException != null
                ? new VeldridDeviceLostException(reason.Value, reasonCode, message, innerException)
                : new VeldridDeviceLostException(reason.Value, reasonCode, message);
        }

        /// <summary>
        ///     For catch blocks around native calls: if <paramref name="nativeFailure"/> is (or reveals) a device loss,
        ///     throw the typed <see cref="VeldridDeviceLostException"/> instead; otherwise return so the caller rethrows
        ///     the original. Internal so <see cref="D3D11CommandList"/> and <see cref="D3D11Swapchain"/> share it.
        /// </summary>
        internal void rethrowAsDeviceLostIfApplicable(SharpGenException nativeFailure)
        {
            VeldridDeviceLostException deviceLost = tryClassifyDeviceLost(nativeFailure.ResultCode.Code, nativeFailure);
            if (deviceLost != null) throw deviceLost;
        }

        /// <summary>Fast path once a loss has been observed (or simulated): fail typed without touching the dead device.</summary>
        private void throwIfDeviceKnownLost()
        {
            if (simulatedDeviceLost)
            {
                throw new VeldridDeviceLostException(GpuDeviceLostReason.Simulated, 0,
                    "Simulated device loss (GraphicsDevice.SimulateDeviceLost). Call RecoverFromDeviceLost() to clear.");
            }
            if (deviceLostObserved || device == null)
            {
                throw new VeldridDeviceLostException(GpuDeviceLostReason.Unknown, 0,
                    "The Direct3D 11 device is lost and has not been recovered yet (RecoverFromDeviceLost pending).");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
        // DEVICE LOST: recovery
        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

        public override void SimulateDeviceLost()
        {
            simulatedDeviceLost = true;
            if (deviceLostRegistration != null)
                deviceLostRegistration.SignalForSimulation();
            else
                NotifyDeviceLost(DeviceGeneration);
        }

        public override void RecoverFromDeviceLost()
        {
            lock (immediateContextLock)
            {
                // 1. Tear down the dead native device. Everything here is best-effort: the objects belong to a device
                deviceLostRegistration?.Dispose();
                deviceLostRegistration = null;
                //    the driver already destroyed; Release() on them is legal but any other call may fail.
                if (device != null)
                {
                    try { retiredImmediateContext?.Dispose(); } catch { /* dead device */ }
                    retiredImmediateContext = null;
                    // FIRST: unbind everything from the old immediate context. It still holds references to the
                    // last frame's render targets — i.e. every swapchain's back buffer. Other children (textures,
                    // buffers owned by consumers) keep the old device alive past our Dispose below, so without
                    // ClearState those back-buffer references survive and DXGI refuses to create a new swapchain
                    // on the same HWND (E_ACCESSDENIED from CreateSwapChainForHwnd — seen with SimulateDeviceLost,
                    // 2026-10-29; DirectXTK DeviceResources::HandleDeviceLost does exactly this).
                    try { immediateContext?.ClearState(); } catch { /* dead device */ }
                    try { immediateContext?.Flush(); } catch { /* dead device */ }

                    // The main swapchain (FluidUI: the 1x1 bootstrap window) is OURS, not a consumer's: release its
                    // natives here so it cannot pin the old device (its framebuffer kept the old device at refcount 33
                    // in the 2026-10-29 live-object report). The consumer rebuilds it via RecreateOnRestoredDevice
                    // like any other swapchain.
                    mainSwapchain?.ReleaseNativeObjectsForDeviceLoss();

                    try { d3d11ResourceFactory?.Dispose(); } catch { /* dead device */ }
                    lock (stagingResourcesLock)
                    {
                        foreach (D3D11Buffer buffer in availableStagingBuffers)
                        {
                            StagingBufferPoolTelemetry.NoteEvictedFromPool(buffer.SizeInBytes);
                            try { buffer.Dispose(); } catch { /* dead device */ }
                        }
                        availableStagingBuffers.Clear();
                    }
                    lock (mappedResourceLock) mappedResources.Clear();
                    try { videoMemoryQueryAdapter?.Dispose(); } catch { /* dead device */ }
                    videoMemoryQueryAdapter = null;
                    videoMemoryQueryAdapterProbed = false;
                    // Owners have released ALL children before entering recovery. Finalize deferred
                    // destruction on the OLD context, then release it before creating the replacement.
                    PointSampler?.Dispose();
                    LinearSampler?.Dispose();
                    if (Features.SamplerAnisotropy) Aniso4XSampler?.Dispose();
                    try { immediateContext?.ClearState(); immediateContext?.Flush(); } catch { /* removed device */ }
                    immediateContext?.Dispose();
                    try { device.Dispose(); } catch { /* dead device */ }
                    try { dxgiAdapter?.Dispose(); } catch { /* dead device */ }
                    d3d11ResourceFactory = null;
                    immediateContext = null;
                    device = null;
                    dxgiAdapter = null;
                }

                // 2. Fresh device on the DEFAULT adapter (spec 1000 D5). Throws while the driver is still down —
                //    we stay torn down (device == null → IsDeviceLost) and the caller retries later.
                createNativeDeviceAndAdapter(IntPtr.Zero);
                immediateContext = device.ImmediateContext;
                d3d11ResourceFactory = new D3D11ResourceFactory(this);

                simulatedDeviceLost = false;
                deviceLostObserved = false;
            }

            // 3. Device-level common resources (samplers) + generation bump + DeviceRestored event. Outside the
            //    lock: subscribers may legitimately call back into the device.
            RecreateCommonResourcesAfterRecovery();
            OnDeviceRecovered();
            RegisterDeviceLostNotification();
        }

        /// <summary>
        /// The immediate context of the device retired by the last <see cref="RecoverFromDeviceLost"/> (null if none,
        /// or if that device is truly gone). Kept so retired children released AFTER recovery can be flushed out.
        /// </summary>
        private ID3D11DeviceContext retiredImmediateContext;

        /// <summary>
        /// Force D3D11's DEFERRED object destruction to complete, on the current immediate context AND on the context
        /// retired by the last <see cref="RecoverFromDeviceLost"/>. Call after releasing objects whose destruction must
        /// be COMPLETE before continuing — the flip-model swapchain rule: an HWND may own only one, and the old one
        /// counts as alive until its owning context is flushed (CreateSwapChainForHwnd → E_ACCESSDENIED otherwise;
        /// observed with SimulateDeviceLost 2026-10-29; ID3D11DeviceContext::Flush docs, "Deferred Destruction Issues
        /// with Flip Presentation Swap Chains"). Retired context: ClearState + Flush (nothing live on it). Current
        /// context: Flush only — ClearState would wipe a live frame's pipeline state.
        /// </summary>
        internal void FlushContextsForDeferredDestruction()
        {
            ID3D11DeviceContext retired = retiredImmediateContext;
            if (retired != null)
            {
                try { retired.ClearState(); retired.Flush(); } catch { /* dead device */ }
                // Do NOT dispose it here: recovery releases old-device objects swapchain by swapchain (main swapchain,
                // then every window surface), and each release needs a later flush on THIS context to finalize. It
                // is disposed by the next RecoverFromDeviceLost or PlatformDispose. (Disposing it on the first flush
                // left later-released RTV/DSV/back buffers and a deferred context live on the old device —
                // DXGI live-object report, 2026-10-29.)
            }
            lock (immediateContextLock)
            {
                ID3D11DeviceContext current = immediateContext;
                if (current == null) return;
                try
                {
                    current.Flush();
                }
                catch (SharpGenException nativeFailure)
                {
                    rethrowAsDeviceLostIfApplicable(nativeFailure);
                    throw;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            var dxgiFormat = D3D11Formats.ToDxgiFormat(format, depthFormat);
            if (checkFormatMultisample(dxgiFormat, 32))
                return TextureSampleCount.Count32;
            if (checkFormatMultisample(dxgiFormat, 16))
                return TextureSampleCount.Count16;
            if (checkFormatMultisample(dxgiFormat, 8))
                return TextureSampleCount.Count8;
            if (checkFormatMultisample(dxgiFormat, 4))
                return TextureSampleCount.Count4;
            if (checkFormatMultisample(dxgiFormat, 2)) return TextureSampleCount.Count2;

            return TextureSampleCount.Count1;
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            return Util.AssertSubtype<Fence, D3D11Fence>(fence).Wait(nanosecondTimeout);
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            int msTimeout;
            if (nanosecondTimeout == ulong.MaxValue)
                msTimeout = -1;
            else
                msTimeout = (int)Math.Min(nanosecondTimeout / 1_000_000, int.MaxValue);

            var events = getResetEventArray(fences.Length);
            for (int i = 0; i < fences.Length; i++)
                events[i] = Util.AssertSubtype<Fence, D3D11Fence>(fences[i]).ResetEvent;

            bool result;

            if (waitAll)
                result = WaitHandle.WaitAll(events.Cast<WaitHandle>().ToArray(), msTimeout);
            else
            {
                int index = WaitHandle.WaitAny(events.Cast<WaitHandle>().ToArray(), msTimeout);
                result = index != WaitHandle.WaitTimeout;
            }

            returnResetEventArray(events);

            return result;
        }

        public override void ResetFence(Fence fence)
        {
            Util.AssertSubtype<Fence, D3D11Fence>(fence).Reset();
        }

        public override bool GetD3D11Info(out BackendInfoD3D11 info)
        {
            info = d3d11Info;
            return true;
        }

        internal override uint GetUniformBufferMinOffsetAlignmentCore()
        {
            return 256u;
        }

        internal override uint GetStructuredBufferMinOffsetAlignmentCore()
        {
            return 16;
        }

        protected override MappedResource MapCore(IMappableResource resource, MapMode mode, uint subresource)
        {
            var key = new MappedResourceCacheKey(resource, subresource);

            lock (mappedResourceLock)
            {
                if (mappedResources.TryGetValue(key, out var info))
                {
                    if (info.Mode != mode) throw new VeldridException("The given resource was already mapped with a different MapMode.");

                    info.RefCount += 1;
                    mappedResources[key] = info;
                }
                else
                {
                    // No current mapping exists -- create one.

                    if (resource is D3D11Buffer buffer)
                    {
                        lock (immediateContextLock)
                        {
                            var msr = immediateContext.Map(
                                buffer.Buffer,
                                0,
                                D3D11Formats.VdToD3D11MapMode((buffer.Usage & BufferUsage.Dynamic) == BufferUsage.Dynamic, mode));

                            info.MappedResource = new MappedResource(resource, mode, msr.DataPointer, buffer.SizeInBytes);
                            info.RefCount = 1;
                            info.Mode = mode;
                            mappedResources.Add(key, info);
                        }
                    }
                    else
                    {
                        var texture = Util.AssertSubtype<IMappableResource, D3D11Texture>(resource);

                        lock (immediateContextLock)
                        {
                            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                            immediateContext.Map(
                                texture.DeviceTexture,
                                (int)mipLevel,
                                (int)arrayLayer,
                                D3D11Formats.VdToD3D11MapMode(false, mode),
                                MapFlags.None,
                                out int _,
                                out MappedSubresource msr);

                            info.MappedResource = new MappedResource(
                                resource,
                                mode,
                                msr.DataPointer,
                                texture.Height * (uint)msr.RowPitch,
                                subresource,
                                (uint)msr.RowPitch,
                                (uint)msr.DepthPitch);
                            info.RefCount = 1;
                            info.Mode = mode;
                            mappedResources.Add(key, info);
                        }
                    }
                }

                return info.MappedResource;
            }
        }

        protected override void UnmapCore(IMappableResource resource, uint subresource)
        {
            var key = new MappedResourceCacheKey(resource, subresource);

            lock (mappedResourceLock)
            {
                if (!mappedResources.TryGetValue(key, out var info)) throw new VeldridException($"The given resource ({resource}) is not mapped.");

                info.RefCount -= 1;
                bool commitUnmap = info.RefCount == 0;

                if (commitUnmap)
                {
                    lock (immediateContextLock)
                    {
                        if (resource is D3D11Buffer buffer)
                            immediateContext.Unmap(buffer.Buffer, 0);
                        else
                        {
                            var texture = Util.AssertSubtype<IMappableResource, D3D11Texture>(resource);
                            immediateContext.Unmap(texture.DeviceTexture, (int)subresource);
                        }

                        bool result = mappedResources.Remove(key);
                        Debug.Assert(result);
                    }
                }
                else
                    mappedResources[key] = info;
            }
        }

        protected override void PlatformDispose()
        {
            deviceLostRegistration?.Dispose();
            deviceLostRegistration = null;
            // Dispose staging buffers
            foreach (D3D11Buffer buffer in availableStagingBuffers)
            { StagingBufferPoolTelemetry.NoteEvictedFromPool(buffer.SizeInBytes); buffer.Dispose(); }

            availableStagingBuffers.Clear();

            if (device == null)
            {
                // Lost and never recovered: the native side was already released in RecoverFromDeviceLost's teardown.
                mainSwapchain?.Dispose();
                return;
            }

            d3d11ResourceFactory.Dispose();
            mainSwapchain?.Dispose();
            immediateContext.Dispose();

            if (IsDebugEnabled)
            {
                uint refCount = device.Release();

                if (refCount > 0)
                {
                    var deviceDebug = device.QueryInterfaceOrNull<ID3D11Debug>();

                    if (deviceDebug != null)
                    {
                        deviceDebug.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Summary | ReportLiveDeviceObjectFlags.Detail | ReportLiveDeviceObjectFlags.IgnoreInternal);
                        deviceDebug.Dispose();
                    }
                }

                dxgiAdapter.Dispose();

                // Report live objects using DXGI if available (DXGIGetDebugInterface1 will fail on pre Windows 8 OS).
                if (VorticeDXGI.DXGIGetDebugInterface1(out IDXGIDebug1 dxgiDebug).Success)
                {
                    dxgiDebug.ReportLiveObjects(VorticeDXGI.DebugAll, ReportLiveObjectFlags.Summary | ReportLiveObjectFlags.IgnoreInternal);
                    dxgiDebug.Dispose();
                }
            }
            else
            {
                device.Dispose();
                dxgiAdapter.Dispose();
            }
        }

        private static D3D11DeviceOptions mergeOptions(D3D11DeviceOptions d3D11DeviceOptions, GraphicsDeviceOptions options)
        {
            if (options.Debug) d3D11DeviceOptions.DeviceCreationFlags |= (uint)DeviceCreationFlags.Debug;

            return d3D11DeviceOptions;
        }

        private bool checkFormatMultisample(Format format, int sampleCount)
        {
            return device.CheckMultisampleQualityLevels(format, sampleCount) != 0;
        }

        private D3D11Buffer getFreeStagingBuffer(uint sizeInBytes)
        {
            lock (stagingResourcesLock)
            {
                // Best fit: the SMALLEST pooled buffer that is large enough.
                D3D11Buffer bestFit = null;
                foreach (var buffer in availableStagingBuffers)
                {
                    if (buffer.SizeInBytes >= sizeInBytes && (bestFit == null || buffer.SizeInBytes < bestFit.SizeInBytes))
                        bestFit = buffer;
                }
                if (bestFit != null)
                {
                    availableStagingBuffers.Remove(bestFit);
                    StagingBufferPoolTelemetry.NoteTakenFromPool(bestFit.SizeInBytes);
                    return bestFit;
                }

                // Miss: every pooled buffer is smaller than this request; the one created below supersedes them all.
                // Retire them or the pool grows by one exact-size CPU buffer per new high-water mark, forever
                // (see D3D11CommandList.getFreeStagingBuffer for the AN_Monitor 2 GB case, 2026-09-24).
                foreach (var buffer in availableStagingBuffers) { StagingBufferPoolTelemetry.NoteEvictedFromPool(buffer.SizeInBytes); buffer.Dispose(); }
                availableStagingBuffers.Clear();
            }

            var staging = ResourceFactory.CreateBuffer(
                new BufferDescription(sizeInBytes, BufferUsage.Staging));
            StagingBufferPoolTelemetry.NoteCreated(sizeInBytes);

            return Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(staging);
        }

        private ManualResetEvent[] getResetEventArray(int length)
        {
            lock (resetEventsLock)
            {
                for (int i = resetEvents.Count - 1; i > 0; i--)
                {
                    var array = resetEvents[i];

                    if (array.Length == length)
                    {
                        resetEvents.RemoveAt(i);
                        return array;
                    }
                }
            }

            var newArray = new ManualResetEvent[length];
            return newArray;
        }

        private void returnResetEventArray(ManualResetEvent[] array)
        {
            lock (resetEventsLock) resetEvents.Add(array);
        }

        private protected override void SubmitCommandsCore(CommandList cl, Fence fence)
        {
            var d3d11Cl = Util.AssertSubtype<CommandList, D3D11CommandList>(cl);

            lock (immediateContextLock)
            {
                throwIfDeviceKnownLost();
                try
                {
                    if (d3d11Cl.DeviceCommandList != null) // CommandList may have been reset in the meantime (resized swapchain).
                    {
                        immediateContext.ExecuteCommandList(d3d11Cl.DeviceCommandList, false);
                        d3d11Cl.OnCompleted();
                    }
                }
                catch (SharpGenException nativeFailure)
                {
                    rethrowAsDeviceLostIfApplicable(nativeFailure);
                    throw;
                }
            }

            if (fence is D3D11Fence d3d11Fence) d3d11Fence.Set();
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            lock (immediateContextLock)
            {
                throwIfDeviceKnownLost();
                var d3d11Sc = Util.AssertSubtype<Swapchain, D3D11Swapchain>(swapchain);
                // Present is where a TDR / driver restart is FIRST observable (DXGI_ERROR_DEVICE_REMOVED/RESET). The
                // result used to be discarded, so the loss only surfaced as an unrelated CheckError later
                // (AN_FluidUI/_SPECS/1000, crash logs 2026-10-28/29: D3D11CommandList.End, D3D11Swapchain.Resize).
                Result presentResult = d3d11Sc.DxgiSwapChain.Present(d3d11Sc.SyncInterval, d3d11Sc.PresentFlags);
                if (presentResult.Failure)
                {
                    VeldridDeviceLostException deviceLost = tryClassifyDeviceLost(presentResult.Code, innerException: null);
                    if (deviceLost != null) throw deviceLost;
                    // DXGI_STATUS_OCCLUDED and friends are SUCCESS codes and never reach here; a genuine non-device-lost
                    // failure surfaces as the raw SharpGen exception exactly as other Vortice calls would.
                    try
                    {
                        presentResult.CheckError();
                    }
                    catch (SharpGenException nativeFailure)
                    {
                        rethrowAsDeviceLostIfApplicable(nativeFailure);
                        throw;
                    }
                }
            }
        }

        private protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            if (D3D11Formats.IsUnsupportedFormat(format))
            {
                properties = default;
                return false;
            }

            bool isDepthStencilUsage = (usage & TextureUsage.DepthStencil) != 0;
            // ToDxgiFormat(depthFormat: true) yields the TYPELESS resource format (R24G8_Typeless, ...), which is
            // what depth textures are CREATED with so they can be viewed as depth OR shader resource. But
            // CheckFormatSupport on a typeless format never reports DepthStencil/ShaderSample - the capability
            // bits live on the typed formats. Query the typed depth format instead (2026-09-28: every depth
            // format reported "unsupported", which broke callers choosing D24S8 vs D32S8 at runtime).
            var dxgiFormat = isDepthStencilUsage
                ? D3D11Formats.GetDepthFormat(format)
                : D3D11Formats.ToDxgiFormat(format, false);
            var fs = device.CheckFormatSupport(dxgiFormat);

            if (((usage & TextureUsage.RenderTarget) != 0 && (fs & FormatSupport.RenderTarget) == 0)
                || ((usage & TextureUsage.DepthStencil) != 0 && (fs & FormatSupport.DepthStencil) == 0)
                || ((usage & TextureUsage.Sampled) != 0 && (fs & FormatSupport.ShaderSample) == 0)
                || ((usage & TextureUsage.Cubemap) != 0 && (fs & FormatSupport.TextureCube) == 0)
                || ((usage & TextureUsage.Storage) != 0 && (fs & FormatSupport.TypedUnorderedAccessView) == 0))
            {
                properties = default;
                return false;
            }

            const uint max_texture_dimension = 16384;
            const uint max_volume_extent = 2048;

            uint sampleCounts = 0;
            if (checkFormatMultisample(dxgiFormat, 1)) sampleCounts |= 1 << 0;

            if (checkFormatMultisample(dxgiFormat, 2)) sampleCounts |= 1 << 1;

            if (checkFormatMultisample(dxgiFormat, 4)) sampleCounts |= 1 << 2;

            if (checkFormatMultisample(dxgiFormat, 8)) sampleCounts |= 1 << 3;

            if (checkFormatMultisample(dxgiFormat, 16)) sampleCounts |= 1 << 4;

            if (checkFormatMultisample(dxgiFormat, 32)) sampleCounts |= 1 << 5;

            properties = new PixelFormatProperties(
                max_texture_dimension,
                type == TextureType.Texture1D ? 1 : max_texture_dimension,
                type != TextureType.Texture3D ? 1 : max_volume_extent,
                uint.MaxValue,
                type == TextureType.Texture3D ? 1 : max_volume_extent,
                sampleCounts);
            return true;
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            try
            {
                updateBufferUnchecked(buffer, bufferOffsetInBytes, source, sizeInBytes);
            }
            catch (SharpGenException nativeFailure)
            {
                rethrowAsDeviceLostIfApplicable(nativeFailure);
                throw;
            }
        }

        private unsafe void updateBufferUnchecked(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            var d3dBuffer = Util.AssertSubtype<DeviceBuffer, D3D11Buffer>(buffer);
            if (sizeInBytes == 0) return;

            bool isDynamic = (buffer.Usage & BufferUsage.Dynamic) == BufferUsage.Dynamic;
            bool isStaging = (buffer.Usage & BufferUsage.Staging) == BufferUsage.Staging;
            bool isUniformBuffer = (buffer.Usage & BufferUsage.UniformBuffer) == BufferUsage.UniformBuffer;
            bool updateFullBuffer = bufferOffsetInBytes == 0 && sizeInBytes == buffer.SizeInBytes;
            bool useUpdateSubresource = !isDynamic && !isStaging && (!isUniformBuffer || updateFullBuffer);
            bool useMap = (isDynamic && updateFullBuffer) || isStaging;

            if (useUpdateSubresource)
            {
                Box? subregion = new Box((int)bufferOffsetInBytes, 0, 0, (int)(sizeInBytes + bufferOffsetInBytes), 1, 1);

                if (isUniformBuffer) subregion = null;

                lock (immediateContextLock) immediateContext.UpdateSubresource(d3dBuffer.Buffer, 0, subregion, source, 0, 0);
            }
            else if (useMap)
            {
                var mr = MapCore(buffer, MapMode.Write, 0);

                if (sizeInBytes < 1024)
                    Unsafe.CopyBlock((byte*)mr.Data + bufferOffsetInBytes, source.ToPointer(), sizeInBytes);
                else
                {
                    Buffer.MemoryCopy(
                        source.ToPointer(),
                        (byte*)mr.Data + bufferOffsetInBytes,
                        buffer.SizeInBytes,
                        sizeInBytes);
                }

                UnmapCore(buffer, 0);
            }
            else
            {
                var staging = getFreeStagingBuffer(sizeInBytes);
                UpdateBuffer(staging, 0, source, sizeInBytes);
                var sourceRegion = new Box(0, 0, 0, (int)sizeInBytes, 1, 1);

                lock (immediateContextLock)
                {
                    immediateContext.CopySubresourceRegion(
                        d3dBuffer.Buffer, 0, (int)bufferOffsetInBytes, 0, 0,
                        staging.Buffer, 0,
                        sourceRegion);
                }

                lock (stagingResourcesLock) { availableStagingBuffers.Add(staging); StagingBufferPoolTelemetry.NoteReturnedToPool(staging.SizeInBytes); }
            }
        }

        private protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint z,
            uint width,
            uint height,
            uint depth,
            uint mipLevel,
            uint arrayLayer)
        {
            try
            {
                updateTextureUnchecked(texture, source, sizeInBytes, x, y, z, width, height, depth, mipLevel, arrayLayer);
            }
            catch (SharpGenException nativeFailure)
            {
                rethrowAsDeviceLostIfApplicable(nativeFailure);
                throw;
            }
        }

        private unsafe void updateTextureUnchecked(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint z,
            uint width,
            uint height,
            uint depth,
            uint mipLevel,
            uint arrayLayer)
        {
            var d3dTex = Util.AssertSubtype<Texture, D3D11Texture>(texture);
            bool useMap = (texture.Usage & TextureUsage.Staging) == TextureUsage.Staging;

            if (useMap)
            {
                uint subresource = texture.CalculateSubresource(mipLevel, arrayLayer);
                var map = MapCore(texture, MapMode.Write, subresource);

                uint denseRowSize = FormatHelpers.GetRowPitch(width, texture.Format);
                uint denseSliceSize = FormatHelpers.GetDepthPitch(denseRowSize, height, texture.Format);

                Util.CopyTextureRegion(
                    source.ToPointer(),
                    0, 0, 0,
                    denseRowSize, denseSliceSize,
                    map.Data.ToPointer(),
                    x, y, z,
                    map.RowPitch, map.DepthPitch,
                    width, height, depth,
                    texture.Format);

                UnmapCore(texture, subresource);
            }
            else
            {
                int subresource = D3D11Util.ComputeSubresource(mipLevel, texture.MipLevels, arrayLayer);
                var resourceRegion = new Box(
                    (int)x,
                    right: (int)(x + width),
                    top: (int)y,
                    front: (int)z,
                    bottom: (int)(y + height),
                    back: (int)(z + depth));

                uint srcRowPitch = FormatHelpers.GetRowPitch(width, texture.Format);
                uint srcDepthPitch = FormatHelpers.GetDepthPitch(srcRowPitch, height, texture.Format);

                lock (immediateContextLock)
                {
                    immediateContext.UpdateSubresource(
                        d3dTex.DeviceTexture,
                        subresource,
                        resourceRegion,
                        source,
                        (int)srcRowPitch,
                        (int)srcDepthPitch);
                }
            }
        }

        private protected override void WaitForIdleCore()
        {
        }

        private protected override void TrimDriverMemoryForIdleCore()
        {
            // IDXGIDevice3::Trim — Windows 8.1+ runtime. Releases driver/runtime internal scratch allocations
            // (measured ~100 MiB retained after a resize storm; see AN_Mirica/_BUGFIX/150). ClearState is NOT
            // required for the memory effect (5.3b), so pipeline state tracked by D3D11CommandList is left alone.
            // Serialized against the immediate context like every other device-level call here.
            lock (immediateContextLock)
            {
                if (device == null) return; // lost and not yet recovered — nothing to trim
                try
                {
                    using var dxgiDevice3 = device.QueryInterfaceOrNull<IDXGIDevice3>();
                    dxgiDevice3?.Trim();
                }
                catch (SharpGenException nativeFailure)
                {
                    rethrowAsDeviceLostIfApplicable(nativeFailure);
                    throw;
                }
            }
        }

        private protected override void WaitForNextFrameReadyCore()
        {
            mainSwapchain.WaitForNextFrameReady();
        }

        // IDXGIAdapter3 is Windows 10+; QueryInterface may fail on older DXGI. Resolved once, kept for the device's life
        // (it is a reference on the SAME adapter we already own; disposed with it).
        private IDXGIAdapter3 videoMemoryQueryAdapter;
        private bool videoMemoryQueryAdapterProbed;

        private protected override bool TryQueryVideoMemoryUsageCore(out GraphicsDevice_VideoMemoryUsage usage)
        {
            usage = default;
            if (!videoMemoryQueryAdapterProbed)
            {
                videoMemoryQueryAdapterProbed = true;
                try { videoMemoryQueryAdapter = dxgiAdapter.QueryInterfaceOrNull<IDXGIAdapter3>(); }
                catch { videoMemoryQueryAdapter = null; }
            }
            if (videoMemoryQueryAdapter == null) return false;
            try
            {
                // Node 0: the only node on a non-linked adapter; SLI/CrossFire nodes are not distinguished here.
                QueryVideoMemoryInfo local = videoMemoryQueryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);       // Vortice: returns the struct, throws on HRESULT failure
                QueryVideoMemoryInfo nonLocal = videoMemoryQueryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal);
                usage = new GraphicsDevice_VideoMemoryUsage(local.CurrentUsage, local.Budget, nonLocal.CurrentUsage, nonLocal.Budget);
                return true;
            }
            catch { return false; }
        }
    }
}
