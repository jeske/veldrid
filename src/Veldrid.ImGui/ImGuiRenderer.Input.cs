using ImGuiNET;
using System.Numerics;

namespace Veldrid
{
    /// <summary>
    /// Windowing-agnostic input feed for <see cref="ImGuiRenderer"/>. Each method forwards 1:1 to the
    /// corresponding <c>ImGuiIO.Add*Event</c> (ImGui >= 1.87 event-queue input model). The host owns the
    /// mapping from its own key/button types to <see cref="ImGuiKey"/> / <see cref="ImGuiMouseButton"/>.
    /// Call these between <see cref="BeginFrame(float)"/> and <see cref="EndFrame"/>.
    /// </summary>
    public partial class ImGuiRenderer
    {
        /// <summary>Mouse position in ImGui display coordinates (framebuffer pixels / DisplayFramebufferScale).</summary>
        public void AddMousePosEvent(float x, float y)
            => ImGui.GetIO().AddMousePosEvent(x, y);

        /// <summary>Mouse button state. Buttons beyond Middle are <c>(ImGuiMouseButton)3</c> / <c>(ImGuiMouseButton)4</c>.</summary>
        public void AddMouseButtonEvent(ImGuiMouseButton button, bool down)
            => ImGui.GetIO().AddMouseButtonEvent((int)button, down);

        /// <summary>Mouse wheel delta in ImGui wheel units (1.0 = one notch).</summary>
        public void AddMouseWheelEvent(float wheelX, float wheelY)
            => ImGui.GetIO().AddMouseWheelEvent(wheelX, wheelY);

        /// <summary>Text input: one Unicode codepoint.</summary>
        public void AddInputCharacter(uint unicodeCodepoint)
            => ImGui.GetIO().AddInputCharacter(unicodeCodepoint);

        /// <summary>Key state. Modifiers are <see cref="ImGuiKey.ModCtrl"/> / <see cref="ImGuiKey.ModShift"/> / <see cref="ImGuiKey.ModAlt"/> / <see cref="ImGuiKey.ModSuper"/>.</summary>
        public void AddKeyEvent(ImGuiKey key, bool down)
            => ImGui.GetIO().AddKeyEvent(key, down);

        /// <summary>Window focus gained/lost. On loss ImGui releases held keys and buttons.</summary>
        public void AddFocusEvent(bool focused)
            => ImGui.GetIO().AddFocusEvent(focused);

        /// <summary>
        /// Sets the framebuffer scale independently of <see cref="WindowResized(int, int)"/>
        /// (e.g. the host renders ImGui into a down-scaled game texture). Takes effect at the next
        /// <see cref="BeginFrame(float)"/>. <see cref="WindowResized(int, int)"/> is in framebuffer pixels;
        /// <c>DisplaySize = size / scaleFactor</c>.
        /// </summary>
        public void SetDisplayFramebufferScale(Vector2 scaleFactor)
        {
            _scaleFactor = scaleFactor;
        }
    }
}