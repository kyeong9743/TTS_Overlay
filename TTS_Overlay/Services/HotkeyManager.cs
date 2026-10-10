using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TtsOverlay.Services
{
    public class HotkeyManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int HOTKEY_ID = 9000;        // 오버레이 열기/닫기
        public const int STOP_HOTKEY_ID = 9002;   // 재생 중단
        public const uint MOD_NONE = 0x0000;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        // 키를 누르고 있어도 WM_HOTKEY가 반복 발생하지 않게 함(창이 연속으로 토글되는 것 방지)
        private const uint MOD_NOREPEAT = 0x4000;
        // 조합이 비어 있는지 시험 등록할 때만 잠깐 쓰는 ID
        private const int PROBE_HOTKEY_ID = 9001;

        // 기본값: Ctrl + T
        public const uint DEFAULT_MODIFIERS = MOD_CONTROL;
        public const uint DEFAULT_KEY = 0x54; // 'T'

        private readonly int _id;
        private IntPtr _hWnd;
        private HwndSource _source;
        public event EventHandler HotkeyPressed;

        /// <param name="id">이 인스턴스가 관리할 단축키 ID. 한 창에 단축키를 여러 개 걸려면 ID가 서로 달라야 함.</param>
        public HotkeyManager(int id = HOTKEY_ID)
        {
            _id = id;
        }

        /// <summary>사람이 읽을 수 있는 조합 문자열. 예: "Ctrl + Shift + T"</summary>
        public static string Format(uint modifiers, uint key)
        {
            var parts = new List<string>();
            if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & MOD_WIN) != 0) parts.Add("Win");

            var wpfKey = KeyInterop.KeyFromVirtualKey((int)key);
            parts.Add(wpfKey == Key.None ? $"0x{key:X}" : wpfKey.ToString());
            return string.Join(" + ", parts);
        }

        /// <summary>수정자 키인지 여부. 수정자만으로는 조합의 주 키가 될 수 없음.</summary>
        public static bool IsModifierKey(Key key) => key is
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin or
            Key.System;

        /// <summary>현재 Ctrl/Alt/Shift/Win 상태를 RegisterHotKey 수정자 플래그로 변환.</summary>
        public static uint CurrentModifiers()
        {
            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
            // WPF의 Keyboard.Modifiers는 Win 키를 보고하지 않으므로 키 상태를 직접 확인
            if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
                mods |= MOD_WIN;
            return mods;
        }

        public void Setup(IntPtr windowHandle)
        {
            _hWnd = windowHandle;
            _source = HwndSource.FromHwnd(_hWnd);
            _source.AddHook(HwndHook);
        }

        public bool Register(uint modifiers, uint key)
        {
            if (_hWnd == IntPtr.Zero) return false;
            return RegisterHotKey(_hWnd, _id, modifiers | MOD_NOREPEAT, key);
        }

        /// <summary>
        /// 조합을 지금 전역 단축키로 등록할 수 있는지 시험. 잠깐 등록해 보고 바로 해제함.
        /// Windows나 다른 프로그램이 이미 쓰고 있으면 false.
        /// </summary>
        public static bool IsAvailable(IntPtr windowHandle, uint modifiers, uint key)
        {
            if (windowHandle == IntPtr.Zero) return false;
            if (!RegisterHotKey(windowHandle, PROBE_HOTKEY_ID, modifiers, key)) return false;
            UnregisterHotKey(windowHandle, PROBE_HOTKEY_ID);
            return true;
        }

        public void Unregister()
        {
            if (_hWnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hWnd, _id);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            _source?.RemoveHook(HwndHook);
        }
    }
}
