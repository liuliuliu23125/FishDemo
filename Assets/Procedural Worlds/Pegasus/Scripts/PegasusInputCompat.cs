using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace Pegasus
{
    /// <summary>
    /// Small compatibility layer that supports both legacy Input Manager and the new Input System.
    /// This relies on Unity's scripting defines:
    /// - ENABLE_INPUT_SYSTEM
    /// - ENABLE_LEGACY_INPUT_MANAGER
    /// </summary>
    public static class PegasusInputCompat
    {
        public static bool GetKey(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetKeyControl(key, out var keyControl) && keyControl != null)
            {
                return keyControl.isPressed;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(key);
#else
            return false;
#endif
        }

        public static bool GetKeyDown(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetKeyControl(key, out var keyControl) && keyControl != null)
            {
                return keyControl.wasPressedThisFrame;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#else
            return false;
#endif
        }

        public static bool GetMouseButton(int button)
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                return button switch
                {
                    0 => mouse.leftButton.isPressed,
                    1 => mouse.rightButton.isPressed,
                    2 => mouse.middleButton.isPressed,
                    _ => false
                };
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(button);
#else
            return false;
#endif
        }

        public static bool GetMouseButtonDown(int button)
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                return button switch
                {
                    0 => mouse.leftButton.wasPressedThisFrame,
                    1 => mouse.rightButton.wasPressedThisFrame,
                    2 => mouse.middleButton.wasPressedThisFrame,
                    _ => false
                };
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(button);
#else
            return false;
#endif
        }

        /// <summary>Mouse delta in pixels this frame.</summary>
        public static Vector2 GetMouseDelta()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                return mouse.delta.ReadValue();
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#else
            return Vector2.zero;
#endif
        }

        /// <summary>Mouse scroll (typically +/- 0.1 per wheel notch on legacy; varies on new).</summary>
        public static float GetMouseScrollY()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                // New Input System reports scroll in "lines"/pixels depending on platform; normalize a bit.
                return mouse.scroll.ReadValue().y;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetAxisRaw("Mouse ScrollWheel");
#else
            return 0f;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static bool TryGetKeyControl(KeyCode keyCode, out KeyControl control)
        {
            control = null;
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return false;
            }

            if (!TryMapKeyCode(keyCode, out var key))
            {
                return false;
            }

            control = keyboard[key];
            return control != null;
        }

        private static bool TryMapKeyCode(KeyCode keyCode, out Key key)
        {
            // Map the most common keys needed by tools/scripts.
            // If a key is not mapped and legacy input is disabled, the key will simply not be detected.
            key = default;

            // Letters
            if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
            {
                key = (Key)((int)Key.A + (keyCode - KeyCode.A));
                return true;
            }

            // Digits (top row)
            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
            {
                key = (Key)((int)Key.Digit0 + (keyCode - KeyCode.Alpha0));
                return true;
            }

            // Function keys
            if (keyCode >= KeyCode.F1 && keyCode <= KeyCode.F12)
            {
                key = (Key)((int)Key.F1 + (keyCode - KeyCode.F1));
                return true;
            }

            switch (keyCode)
            {
                case KeyCode.Space: key = Key.Space; return true;
                case KeyCode.Return: key = Key.Enter; return true;
                case KeyCode.KeypadEnter: key = Key.NumpadEnter; return true;
                case KeyCode.Escape: key = Key.Escape; return true;
                case KeyCode.Tab: key = Key.Tab; return true;
                case KeyCode.BackQuote: key = Key.Backquote; return true;
                case KeyCode.Backspace: key = Key.Backspace; return true;
                case KeyCode.Delete: key = Key.Delete; return true;

                case KeyCode.UpArrow: key = Key.UpArrow; return true;
                case KeyCode.DownArrow: key = Key.DownArrow; return true;
                case KeyCode.LeftArrow: key = Key.LeftArrow; return true;
                case KeyCode.RightArrow: key = Key.RightArrow; return true;

                case KeyCode.LeftShift: key = Key.LeftShift; return true;
                case KeyCode.RightShift: key = Key.RightShift; return true;
                case KeyCode.LeftControl: key = Key.LeftCtrl; return true;
                case KeyCode.RightControl: key = Key.RightCtrl; return true;
                case KeyCode.LeftAlt: key = Key.LeftAlt; return true;
                case KeyCode.RightAlt: key = Key.RightAlt; return true;

                case KeyCode.Minus: key = Key.Minus; return true;
                case KeyCode.Equals: key = Key.Equals; return true;
                case KeyCode.LeftBracket: key = Key.LeftBracket; return true;
                case KeyCode.RightBracket: key = Key.RightBracket; return true;
                case KeyCode.Backslash: key = Key.Backslash; return true;
                case KeyCode.Semicolon: key = Key.Semicolon; return true;
                case KeyCode.Quote: key = Key.Quote; return true;
                case KeyCode.Comma: key = Key.Comma; return true;
                case KeyCode.Period: key = Key.Period; return true;
                case KeyCode.Slash: key = Key.Slash; return true;

                case KeyCode.PageUp: key = Key.PageUp; return true;
                case KeyCode.PageDown: key = Key.PageDown; return true;
                case KeyCode.Home: key = Key.Home; return true;
                case KeyCode.End: key = Key.End; return true;
                case KeyCode.Insert: key = Key.Insert; return true;

                default:
                    return false;
            }
        }
#endif
    }
}

