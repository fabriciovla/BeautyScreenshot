using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace BeautyScreenshot
{
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Dark/light title bar painted in the window's own background color (Windows 11; ignored elsewhere).</summary>
        public static void StyleTitleBar(IntPtr hwnd, bool dark, System.Windows.Media.Color caption, System.Windows.Media.Color text)
        {
            try
            {
                int useDark = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref useDark, 4);
                int captionColor = caption.R | caption.G << 8 | caption.B << 16;
                DwmSetWindowAttribute(hwnd, 35, ref captionColor, 4);
                int textColor = text.R | text.G << 8 | text.B << 16;
                DwmSetWindowAttribute(hwnd, 36, ref textColor, 4);
            }
            catch (DllNotFoundException) { }
        }

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        /// <summary>
        /// Keeps a window visible on the monitor but out of every screenshot and screen recording
        /// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004 and later). Returns false where it is not supported.
        /// </summary>
        public static bool ExcludeFromCapture(IntPtr hwnd)
        {
            try { return SetWindowDisplayAffinity(hwnd, 0x11); }
            catch (EntryPointNotFoundException) { return false; }
        }

        public static IntPtr Foreground() => GetForegroundWindow();

        /// <summary>
        /// Shares input state with the foreground app for a moment, so a window we open now (the save dialog) may
        /// become active even though our own windows never take the focus. Dispose to end it; disposing twice is fine.
        /// </summary>
        public static IDisposable BorrowForeground()
        {
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            return new Loan(() => { if (attached) { attached = false; AttachThreadInput(me, other, false); } });
        }

        sealed class Loan : IDisposable
        {
            readonly Action end;
            public Loan(Action end) { this.end = end; }
            public void Dispose() => end();
        }

        /// <summary>Gives the focus back to a window we took it from (allowed: we are the foreground process).</summary>
        public static void GiveFocusBack(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd)) SetForegroundWindow(hwnd);
        }

        public static void AddExStyle(IntPtr hwnd, int style)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | style);
        }

        /// <summary>Places a window in physical pixels, topmost, without activating it.</summary>
        public static void Place(IntPtr hwnd, int x, int y, int w, int h)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        /// <summary>A background process cannot normally steal focus; borrowing the input queue of the current foreground thread lets it.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        public static double ScaleOf(Forms.Screen screen)
        {
            var b = screen.Bounds;
            var monitor = MonitorFromPoint(new POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 }, 2);
            try
            {
                if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 1.0;
        }

        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        /// <summary>Waits until the desktop compositor has presented a new frame (~16 ms), e.g. after hiding a window.</summary>
        public static void WaitForCompositor()
        {
            try { DwmFlush(); } catch (DllNotFoundException) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        /// <summary>
        /// A background process with no visible windows may be put in efficiency mode (slower cores, lower clocks),
        /// which would delay the capture after the shortcut. Opt out of that, and stay slightly ahead of busy apps.
        /// </summary>
        public static void StayResponsive()
        {
            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 };   // EXECUTION_SPEED: off
                SetProcessInformation(Process.GetCurrentProcess().Handle, 4, ref state, Marshal.SizeOf(state));      // ProcessPowerThrottling
            }
            catch (EntryPointNotFoundException) { }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        }

        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
    }

    /// <summary>
    /// Low-level keyboard hook that watches for the capture shortcut and swallows it, so neither
    /// Windows nor other apps (the Snipping Tool on PrtSc, for instance) react to it.
    /// It can also record the next combination pressed, for the settings window.
    /// </summary>
    sealed class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const int VK_SNAPSHOT = 0x2C, VK_ESCAPE = 0x1B, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const byte VK_MASK = 0xE8;   // unassigned key, used to keep Alt/Win from acting alone after a swallowed key

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extraInfo; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        readonly HookProc proc;   // kept in a field so the GC never collects the delegate
        readonly System.Windows.Threading.Dispatcher dispatcher;
        readonly Action pressed;
        Action<Hotkey?> recorder;
        IntPtr hook;
        readonly System.Collections.Generic.HashSet<int> swallowed = new System.Collections.Generic.HashSet<int>();   // keys whose release must be swallowed too

        /// <summary>The shortcut being watched.</summary>
        public Hotkey Hotkey;

        /// <summary>
        /// While a capture is on screen, every key (but modifiers) is offered here first, with the modifiers held:
        /// the capture overlay never takes the focus, so this is how it gets the keyboard. Returning true swallows
        /// the key, so it does not reach the app behind. Runs on the UI thread, inside the hook: keep it quick.
        /// </summary>
        public Func<int, int, bool> Intercept;

        public KeyboardHook(Hotkey hotkey, Action pressed)
        {
            Hotkey = hotkey;
            this.pressed = pressed;
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            proc = Callback;
            using (var module = Process.GetCurrentProcess().MainModule)
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
        }

        public bool Installed => hook != IntPtr.Zero;

        /// <summary>
        /// Captures the next key pressed together with its modifiers instead of acting on it.
        /// The callback runs on the UI thread with null when the user pressed Esc alone.
        /// </summary>
        public void Record(Action<Hotkey?> done) => recorder = done;

        public void StopRecording() => recorder = null;

        static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        static bool IsModifier(int vk) =>
            vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN || (vk >= 0xA0 && vk <= 0xA5);

        static int CurrentMods()
        {
            int mods = 0;
            if (Held(VK_MENU)) mods |= Hotkey.Alt;
            if (Held(VK_CONTROL)) mods |= Hotkey.Ctrl;
            if (Held(VK_SHIFT)) mods |= Hotkey.Shift;
            if (Held(VK_LWIN) || Held(VK_RWIN)) mods |= Hotkey.Win;
            return mods;
        }

        /// <summary>Alt or Win released right after a swallowed key would open the menu bar or Start; a dummy key prevents it.</summary>
        void MaskModifiers(int mods)
        {
            if ((mods & (Hotkey.Alt | Hotkey.Win)) == 0) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                keybd_event(VK_MASK, 0, 0, IntPtr.Zero);
                keybd_event(VK_MASK, 0, 2, IntPtr.Zero);
            }));
        }

        IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)k.vkCode;
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                if (down && !IsModifier(vk))
                {
                    int mods = CurrentMods();
                    if (recorder != null)
                    {
                        var done = recorder;
                        recorder = null;
                        swallowed.Add(vk);
                        Hotkey? result = vk == VK_ESCAPE && mods == 0 ? (Hotkey?)null : new Hotkey { Mods = mods, Vk = vk };
                        MaskModifiers(mods);
                        dispatcher.BeginInvoke(new Action(() => done(result)));
                        return new IntPtr(1);
                    }
                    if (vk == Hotkey.Vk && mods == Hotkey.Mods)
                    {
                        if (swallowed.Add(vk))   // not on auto-repeat
                        {
                            MaskModifiers(mods);
                            dispatcher.BeginInvoke(pressed);
                        }
                        return new IntPtr(1);
                    }
                    if (Intercept != null && Intercept(vk, mods))
                    {
                        swallowed.Add(vk);
                        MaskModifiers(mods);
                        return new IntPtr(1);
                    }
                    if (swallowed.Contains(vk)) return new IntPtr(1);   // auto-repeat of a key swallowed earlier
                }
                else if (!down)
                {
                    if (swallowed.Remove(vk)) return new IntPtr(1);
                    // Some keyboards only report the release of PrtSc.
                    if (vk == VK_SNAPSHOT && Hotkey.Vk == VK_SNAPSHOT && recorder == null && CurrentMods() == Hotkey.Mods)
                    {
                        dispatcher.BeginInvoke(pressed);
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }
}
