using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BeautyScreenshot
{
    /// <summary>
    /// Where the installer puts things. Shared by the setup (which installs) and the app (which uninstalls itself
    /// from Settings → Apps), so both agree on every path and key.
    /// </summary>
    static class InstallInfo
    {
        public const string InstanceMutex = @"Local\BeautyScreenshot.Instance";
        public const string ExitEvent = @"Local\BeautyScreenshot.Exit";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BeautyScreenshot";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunName = "BeautyScreenshot";
        public const string DisplayName = "Beauty Screenshot";

        /// <summary>Per-user, so no administrator rights are needed: %LOCALAPPDATA%\Programs\BeautyScreenshot.</summary>
        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BeautyScreenshot");
        public static string Exe => Path.Combine(Dir, "BeautyScreenshot.exe");
        public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");

        /// <summary>
        /// Asks a running copy to quit and waits until it has. A copy from before the exit signal existed (1.3 and
        /// earlier) cannot be asked, so it is ended instead; it lives in the tray, there is no unsaved work to lose.
        /// </summary>
        public static void CloseRunning(int timeoutMs)
        {
            bool asked = false;
            if (EventWaitHandle.TryOpenExisting(ExitEvent, out var exit))
                using (exit) { exit.Set(); asked = true; }

            var clock = Stopwatch.StartNew();
            while (asked && clock.ElapsedMilliseconds < timeoutMs)
            {
                if (!Running) return;
                Thread.Sleep(100);
            }
            if (!Running) return;

            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("BeautyScreenshot"))
                using (p)
                {
                    if (p.Id == self) continue;
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
        }

        static bool Running
        {
            get
            {
                if (!Mutex.TryOpenExisting(InstanceMutex, out var m)) return false;
                m.Dispose();
                return true;
            }
        }
    }
}
