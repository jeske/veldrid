using ImGuiNET;
using Veldrid.Sdl2;

namespace Veldrid
{
    /// <summary>
    /// SDL2 / Veldrid.SDL2 <see cref="InputSnapshot"/> binding for <see cref="ImGuiRenderer"/>.
    /// Reference implementation for other windowing hosts: map your own key/button types to
    /// <see cref="ImGuiKey"/> / <see cref="ImGuiMouseButton"/> and call the renderer's <c>Add*Event</c> methods.
    /// </summary>
    public static class ImGuiRendererInputSnapshotExtensions
    {
        /// <summary>
        /// Drop-in replacement for the former <c>ImGuiRenderer.Update(float, InputSnapshot)</c>:
        /// <see cref="ImGuiRenderer.BeginFrame(float)"/>, feed the snapshot, <see cref="ImGuiRenderer.EndFrame"/>.
        /// </summary>
        public static void Update(this ImGuiRenderer renderer, float deltaSeconds, InputSnapshot snapshot)
        {
            renderer.BeginFrame(deltaSeconds);
            renderer.FeedInputSnapshot(snapshot);
            renderer.EndFrame();
        }

        /// <summary>
        /// Feeds mouse position, mouse buttons (Left/Right/Middle/Button1/Button2), wheel, text input and key events
        /// from an <see cref="InputSnapshot"/>. Call between <see cref="ImGuiRenderer.BeginFrame(float)"/> and
        /// <see cref="ImGuiRenderer.EndFrame"/>.
        /// </summary>
        public static void FeedInputSnapshot(this ImGuiRenderer renderer, InputSnapshot snapshot)
        {
            renderer.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
            renderer.AddMouseButtonEvent(ImGuiMouseButton.Left, snapshot.IsMouseDown(MouseButton.Left));
            renderer.AddMouseButtonEvent(ImGuiMouseButton.Right, snapshot.IsMouseDown(MouseButton.Right));
            renderer.AddMouseButtonEvent(ImGuiMouseButton.Middle, snapshot.IsMouseDown(MouseButton.Middle));
            renderer.AddMouseButtonEvent((ImGuiMouseButton)3, snapshot.IsMouseDown(MouseButton.Button1));
            renderer.AddMouseButtonEvent((ImGuiMouseButton)4, snapshot.IsMouseDown(MouseButton.Button2));
            renderer.AddMouseWheelEvent(0f, snapshot.WheelDelta);

            for (int i = 0; i < snapshot.KeyCharPresses.Count; i++)
            {
                renderer.AddInputCharacter(snapshot.KeyCharPresses[i]);
            }

            for (int i = 0; i < snapshot.KeyEvents.Count; i++)
            {
                KeyEvent keyEvent = snapshot.KeyEvents[i];
                if (TryMapKey(keyEvent.Key, out ImGuiKey imguikey))
                {
                    renderer.AddKeyEvent(imguikey, keyEvent.Down);
                }
            }
        }

        /// <summary>
        /// Same as <see cref="FeedInputSnapshot(ImGuiRenderer, InputSnapshot)"/> plus window focus via
        /// <see cref="ImGuiRenderer.AddFocusEvent(bool)"/> from <see cref="Sdl2Window.Focused"/>.
        /// <see cref="InputSnapshot"/> carries no focus information, so this needs the window.
        /// </summary>
        public static void FeedInputSnapshot(this ImGuiRenderer renderer, InputSnapshot snapshot, Sdl2Window window)
        {
            renderer.FeedInputSnapshot(snapshot);
            renderer.AddFocusEvent(window.Focused);
        }

        /// <summary>
        /// Maps a Veldrid <see cref="Key"/> to an <see cref="ImGuiKey"/>. Public so other bindings can reuse the table.
        /// Modifier keys map to <see cref="ImGuiKey.ModShift"/> / <see cref="ImGuiKey.ModCtrl"/> / <see cref="ImGuiKey.ModAlt"/> / <see cref="ImGuiKey.ModSuper"/>.
        /// </summary>
        public static bool TryMapKey(Key key, out ImGuiKey result)
        {
            ImGuiKey keyToImGuiKeyShortcut(Key keyToConvert, Key startKey1, ImGuiKey startKey2)
            {
                int changeFromStart1 = (int)keyToConvert - (int)startKey1;
                return startKey2 + changeFromStart1;
            }

            if (key >= Key.F1 && key <= Key.F12)
            {
                result = keyToImGuiKeyShortcut(key, Key.F1, ImGuiKey.F1);
                return true;
            }
            else if (key >= Key.Keypad0 && key <= Key.Keypad9)
            {
                result = keyToImGuiKeyShortcut(key, Key.Keypad0, ImGuiKey.Keypad0);
                return true;
            }
            else if (key >= Key.A && key <= Key.Z)
            {
                result = keyToImGuiKeyShortcut(key, Key.A, ImGuiKey.A);
                return true;
            }
            else if (key >= Key.Number0 && key <= Key.Number9)
            {
                result = keyToImGuiKeyShortcut(key, Key.Number0, ImGuiKey._0);
                return true;
            }

            switch (key)
            {
                case Key.ShiftLeft:
                case Key.ShiftRight:
                    result = ImGuiKey.ModShift;
                    return true;
                case Key.ControlLeft:
                case Key.ControlRight:
                    result = ImGuiKey.ModCtrl;
                    return true;
                case Key.AltLeft:
                case Key.AltRight:
                    result = ImGuiKey.ModAlt;
                    return true;
                case Key.WinLeft:
                case Key.WinRight:
                    result = ImGuiKey.ModSuper;
                    return true;
                case Key.Menu:
                    result = ImGuiKey.Menu;
                    return true;
                case Key.Up:
                    result = ImGuiKey.UpArrow;
                    return true;
                case Key.Down:
                    result = ImGuiKey.DownArrow;
                    return true;
                case Key.Left:
                    result = ImGuiKey.LeftArrow;
                    return true;
                case Key.Right:
                    result = ImGuiKey.RightArrow;
                    return true;
                case Key.Enter:
                    result = ImGuiKey.Enter;
                    return true;
                case Key.Escape:
                    result = ImGuiKey.Escape;
                    return true;
                case Key.Space:
                    result = ImGuiKey.Space;
                    return true;
                case Key.Tab:
                    result = ImGuiKey.Tab;
                    return true;
                case Key.BackSpace:
                    result = ImGuiKey.Backspace;
                    return true;
                case Key.Insert:
                    result = ImGuiKey.Insert;
                    return true;
                case Key.Delete:
                    result = ImGuiKey.Delete;
                    return true;
                case Key.PageUp:
                    result = ImGuiKey.PageUp;
                    return true;
                case Key.PageDown:
                    result = ImGuiKey.PageDown;
                    return true;
                case Key.Home:
                    result = ImGuiKey.Home;
                    return true;
                case Key.End:
                    result = ImGuiKey.End;
                    return true;
                case Key.CapsLock:
                    result = ImGuiKey.CapsLock;
                    return true;
                case Key.ScrollLock:
                    result = ImGuiKey.ScrollLock;
                    return true;
                case Key.PrintScreen:
                    result = ImGuiKey.PrintScreen;
                    return true;
                case Key.Pause:
                    result = ImGuiKey.Pause;
                    return true;
                case Key.NumLock:
                    result = ImGuiKey.NumLock;
                    return true;
                case Key.KeypadDivide:
                    result = ImGuiKey.KeypadDivide;
                    return true;
                case Key.KeypadMultiply:
                    result = ImGuiKey.KeypadMultiply;
                    return true;
                case Key.KeypadSubtract:
                    result = ImGuiKey.KeypadSubtract;
                    return true;
                case Key.KeypadAdd:
                    result = ImGuiKey.KeypadAdd;
                    return true;
                case Key.KeypadDecimal:
                    result = ImGuiKey.KeypadDecimal;
                    return true;
                case Key.KeypadEnter:
                    result = ImGuiKey.KeypadEnter;
                    return true;
                case Key.Tilde:
                    result = ImGuiKey.GraveAccent;
                    return true;
                case Key.Minus:
                    result = ImGuiKey.Minus;
                    return true;
                case Key.Plus:
                    result = ImGuiKey.Equal;
                    return true;
                case Key.BracketLeft:
                    result = ImGuiKey.LeftBracket;
                    return true;
                case Key.BracketRight:
                    result = ImGuiKey.RightBracket;
                    return true;
                case Key.Semicolon:
                    result = ImGuiKey.Semicolon;
                    return true;
                case Key.Quote:
                    result = ImGuiKey.Apostrophe;
                    return true;
                case Key.Comma:
                    result = ImGuiKey.Comma;
                    return true;
                case Key.Period:
                    result = ImGuiKey.Period;
                    return true;
                case Key.Slash:
                    result = ImGuiKey.Slash;
                    return true;
                case Key.BackSlash:
                case Key.NonUSBackSlash:
                    result = ImGuiKey.Backslash;
                    return true;
                default:
                    result = ImGuiKey.GamepadBack;
                    return false;
            }
        }
    }
}