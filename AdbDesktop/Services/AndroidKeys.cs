using System.Collections.Generic;
using System.Windows.Input;

namespace AdbDesktop
{
    /// <summary>
    /// Maps WPF input to Android keycodes and meta-state.
    ///
    /// Upstream scrcpy does this in keyboard_sdk.c against SDL keysyms. There is no SDL
    /// window here, so the same job is done against WPF's Key enum instead.
    ///
    /// Only non-text keys need mapping: printable characters go through
    /// scv_inject_text() from WPF's TextInput event, which handles layouts, dead keys
    /// and IMEs correctly without this table having to know about any of them.
    /// </summary>
    internal static class AndroidKeys
    {
        // AMETA_* bits
        public const uint MetaNone = 0;
        public const uint MetaShiftOn = 0x00000001;
        public const uint MetaAltOn = 0x00000002;
        public const uint MetaCtrlOn = 0x00001000;

        // AKEYCODE_*
        public const int Back = 4;
        public const int Home = 3;
        public const int AppSwitch = 187;
        public const int Enter = 66;
        public const int Del = 67;          // backspace
        public const int ForwardDel = 112;
        public const int Escape = 111;
        public const int Tab = 61;
        public const int DpadUp = 19;
        public const int DpadDown = 20;
        public const int DpadLeft = 21;
        public const int DpadRight = 22;
        public const int MoveHome = 122;
        public const int MoveEnd = 123;
        public const int PageUp = 92;
        public const int PageDown = 93;
        public const int VolumeUp = 24;
        public const int VolumeDown = 25;

        private static readonly Dictionary<Key, int> Map = new()
        {
            [Key.Enter] = Enter,
            [Key.Return] = Enter,
            [Key.Back] = Del,
            [Key.Delete] = ForwardDel,
            [Key.Escape] = Escape,
            [Key.Tab] = Tab,
            [Key.Up] = DpadUp,
            [Key.Down] = DpadDown,
            [Key.Left] = DpadLeft,
            [Key.Right] = DpadRight,
            [Key.Home] = MoveHome,
            [Key.End] = MoveEnd,
            [Key.PageUp] = PageUp,
            [Key.PageDown] = PageDown,
            [Key.VolumeUp] = VolumeUp,
            [Key.VolumeDown] = VolumeDown,
        };

        /// <summary>
        /// Returns the Android keycode for a non-text key, or null when the key should
        /// be left to TextInput.
        /// </summary>
        public static int? Translate(Key key) =>
            Map.TryGetValue(key, out var code) ? code : null;

        public static uint MetaState(ModifierKeys modifiers)
        {
            uint meta = MetaNone;

            if ((modifiers & ModifierKeys.Shift) != 0) meta |= MetaShiftOn;
            if ((modifiers & ModifierKeys.Control) != 0) meta |= MetaCtrlOn;
            if ((modifiers & ModifierKeys.Alt) != 0) meta |= MetaAltOn;

            return meta;
        }
    }
}
