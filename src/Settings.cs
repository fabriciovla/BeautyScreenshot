using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Win32;

namespace BeautyScreenshot
{
    /// <summary>User preferences, stored under HKCU\Software\BeautyScreenshot. Read on demand: they change rarely.</summary>
    static class Settings
    {
        const string Key = @"Software\BeautyScreenshot";
        const string RunKey = InstallInfo.RunKey;
        const string RunName = InstallInfo.RunName;
        public static string DefaultPattern => L.T("Captura {fecha} {hora}", "Screenshot {date} {time}");

        static string ExePath => Assembly.GetEntryAssembly().Location;

        static object Get(string name)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return k?.GetValue(name);
        }

        static void Set(string name, object value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
                k.SetValue(name, value);
        }

        static int GetInt(string name, int fallback) => Get(name) is int v ? v : fallback;
        static bool GetBool(string name, bool fallback) => GetInt(name, fallback ? 1 : 0) != 0;
        static void SetBool(string name, bool value) => Set(name, value ? 1 : 0);

        // ---------------------------------------------------------------- capture

        public static Hotkey Hotkey
        {
            get => Hotkey.Parse(Get("Hotkey") as string);
            set => Set("Hotkey", value.Serialize());
        }

        /// <summary>What Enter and double-click do.</summary>
        public static CaptureAction DefaultAction
        {
            get => GetInt("DefaultAction", 0) == 1 ? CaptureAction.Save : CaptureAction.Copy;
            set => Set("DefaultAction", (int)value);
        }

        // ---------------------------------------------------------------- saving

        public static string SaveDir
        {
            get => Get("SaveDir") as string ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            set => Set("SaveDir", value);
        }

        public static bool QuickSave { get => GetBool("QuickSave", false); set => SetBool("QuickSave", value); }
        public static bool CopyOnSave { get => GetBool("CopyOnSave", false); set => SetBool("CopyOnSave", value); }

        public static string FileNamePattern
        {
            get => Get("FileName") as string ?? DefaultPattern;
            set => Set("FileName", value);
        }

        static int Counter { get => GetInt("Counter", 0); set => Set("Counter", value); }

        /// <summary>
        /// Expands {fecha} / {date}, {hora} / {time} and {n}, and removes characters Windows does not allow in file
        /// names. Both spellings work in both languages, so a pattern keeps working if Windows changes language.
        /// </summary>
        public static string FileName(string pattern, DateTime when, int n)
        {
            string s = (pattern ?? "")
                .Replace("{fecha}", when.ToString("yyyy-MM-dd")).Replace("{date}", when.ToString("yyyy-MM-dd"))
                .Replace("{hora}", when.ToString("HH.mm.ss")).Replace("{time}", when.ToString("HH.mm.ss"))
                .Replace("{n}", n.ToString("000"));
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            return s.Length == 0 ? L.T("Captura", "Screenshot") : s;
        }

        public static string SuggestedFileName() => FileName(FileNamePattern, DateTime.Now, Counter + 1);

        /// <summary>Call after saving through the dialog, so the next {n} moves on.</summary>
        public static void AdvanceCounter()
        {
            if (FileNamePattern.Contains("{n}")) Counter = Counter + 1;
        }

        /// <summary>Next free file name in the save folder, for saving without the dialog.</summary>
        public static string NextSavePath()
        {
            string dir = SaveDir;
            Directory.CreateDirectory(dir);
            string pattern = FileNamePattern;
            var now = DateTime.Now;
            int n = Counter + 1;
            string path;
            if (pattern.Contains("{n}"))
            {
                while (File.Exists(path = Path.Combine(dir, FileName(pattern, now, n) + ".png"))) n++;
                Counter = n;
            }
            else
            {
                string name = FileName(pattern, now, n);
                path = Path.Combine(dir, name + ".png");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " (" + i + ").png");
            }
            return path;
        }

        // ---------------------------------------------------------------- appearance

        /// <summary>How dark the screen gets while selecting, in percent.</summary>
        public static int DimPercent
        {
            get => Math.Max(0, Math.Min(85, GetInt("Dim", 50)));
            set => Set("Dim", value);
        }

        /// <summary>Corner radius of the selection and of the resulting image, in DIPs; 0 means square corners.</summary>
        public static int CornerRadius
        {
            get => Math.Max(0, Math.Min(24, GetInt("Radius", 10)));
            set => Set("Radius", value);
        }

        public static bool ShowHint { get => GetBool("ShowHint", true); set => SetBool("ShowHint", value); }
        public static bool ShowToasts { get => GetBool("ShowToasts", true); set => SetBool("ShowToasts", value); }
        public static bool PlaySound { get => GetBool("PlaySound", true); set => SetBool("PlaySound", value); }

        // ---------------------------------------------------------------- general

        public static bool StartWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(RunName) is string;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, "\"" + ExePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
        }

        /// <summary>If the exe was moved, keep the autostart entry pointing at the current location.</summary>
        public static void RefreshAutoStartPath()
        {
            if (StartWithWindows) StartWithWindows = true;
        }
    }

    /// <summary>A key plus modifiers, as watched by the keyboard hook.</summary>
    struct Hotkey
    {
        public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
        const int VK_SNAPSHOT = 0x2C, VK_PAUSE = 0x13, VK_SCROLL = 0x91, VK_F1 = 0x70, VK_F24 = 0x87;

        public int Mods;
        public int Vk;

        public static readonly Hotkey Default = new Hotkey { Vk = VK_SNAPSHOT };

        public bool Equals(Hotkey other) => Mods == other.Mods && Vk == other.Vk;

        /// <summary>Keys that are safe to use without modifiers, because nobody types with them.</summary>
        public bool UsableAlone => Vk == VK_SNAPSHOT || Vk == VK_PAUSE || Vk == VK_SCROLL || (Vk >= VK_F1 && Vk <= VK_F24);

        public bool IsValid => Vk != 0 && (Mods != 0 || UsableAlone);

        public string Serialize() => Mods + ":" + Vk;

        public static Hotkey Parse(string s)
        {
            var parts = (s ?? "").Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int mods) && int.TryParse(parts[1], out int vk))
            {
                var h = new Hotkey { Mods = mods, Vk = vk };
                if (h.IsValid) return h;
            }
            return Default;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if ((Mods & Win) != 0) parts.Add("Win");
            if ((Mods & Ctrl) != 0) parts.Add("Ctrl");
            if ((Mods & Alt) != 0) parts.Add("Alt");
            if ((Mods & Shift) != 0) parts.Add(L.T("Mayús", "Shift"));
            parts.Add(KeyName(Vk));
            return string.Join(" + ", parts);
        }

        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

        static string KeyName(int vk)
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            switch (key)
            {
                case Key.Snapshot: return L.T("Impr Pant", "Print Screen");
                case Key.Pause: return L.T("Pausa", "Pause");
                case Key.Scroll: return L.T("Bloq Despl", "Scroll Lock");
                case Key.Space: return L.T("Espacio", "Space");
                case Key.Return: return "Enter";
                case Key.Back: return L.T("Retroceso", "Backspace");
                case Key.Tab: return "Tab";
                case Key.Insert: return "Insert";
                case Key.Delete: return L.T("Supr", "Delete");
                case Key.Home: return L.T("Inicio", "Home");
                case Key.End: return L.T("Fin", "End");
                case Key.PageUp: return L.T("Re Pág", "Page Up");
                case Key.PageDown: return L.T("Av Pág", "Page Down");
                case Key.Left: return "←";
                case Key.Right: return "→";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
            }
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num " + (key - Key.NumPad0);
            if (key >= Key.F1 && key <= Key.F24) return key.ToString();
            if (key >= Key.A && key <= Key.Z) return key.ToString();
            uint ch = MapVirtualKey((uint)vk, 2) & 0x7FFFFFFF;   // MAPVK_VK_TO_CHAR; the high bit marks dead keys
            if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
            return key.ToString();
        }
    }

    static class Theme
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        static bool Read(string name, int fallback)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return (k?.GetValue(name) is int v ? v : fallback) != 0;
        }

        /// <summary>Theme of apps (used by the toast and the settings window).</summary>
        public static bool AppsLight => Read("AppsUseLightTheme", 1);

        /// <summary>Theme of the taskbar and tray (used by the tray icon and menu).</summary>
        public static bool SystemLight => Read("SystemUsesLightTheme", 0);
    }
}
