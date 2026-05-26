using System;
using static Veldrid.MetalBindings.ObjectiveCRuntime;

namespace Veldrid.MetalBindings
{
    public struct NSWindow
    {
        public readonly IntPtr NativePtr;
        public NSWindow(IntPtr ptr)
        {
            NativePtr = ptr;
        }

        public NSView contentView => objc_msgSend<NSView>(NativePtr, sel_contentView);

        public double backingScaleFactor => CGFloat_objc_msgSend(NativePtr, sel_backingScaleFactor);

        private static readonly Selector sel_contentView = "contentView";
        private static readonly Selector sel_backingScaleFactor = "backingScaleFactor";
    }
}
