using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace BeautyScreenshot.Setup
{
    /// <summary>
    /// What the setup actually does, step by step: close a running copy, copy the exe (carried inside the setup as
    /// a resource), add the Start menu entry and register the uninstaller in Settings → Apps. All per user.
    /// </summary>
    static class Installer
    {
        /// <summary>Already installed by a setup: this run is an update, and the welcome is skipped.</summary>
        public static bool IsUpdate
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(InstallInfo.UninstallKey))
                    return k != null && File.Exists(InstallInfo.Exe);
            }
        }

        /// <summary>Whether it starts with Windows today, wherever the exe was (a portable copy counts too).</summary>
        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey))
                    return k?.GetValue(InstallInfo.RunName) is string;
            }
        }

        public static void CloseRunning() => InstallInfo.CloseRunning(4000);

        public static void CopyApp()
        {
            Directory.CreateDirectory(InstallInfo.Dir);
            // The exe of a copy that was just ended may stay locked for a moment.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("BeautyScreenshot.exe"))
                    using (var file = File.Create(InstallInfo.Exe))
                        payload.CopyTo(file);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(300);
                }
            }
        }

        public static void CreateShortcut()
        {
            // WScript.Shell is the simplest way to write a .lnk without our own COM declarations.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { InstallInfo.StartMenuLink });
                var linkType = link.GetType();
                void Set(string name, object value) => linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, new[] { value });
                Set("TargetPath", InstallInfo.Exe);
                Set("WorkingDirectory", InstallInfo.Dir);
                Set("Description", L.T("Capturas de pantalla simples y lindas", "Simple, beautiful screenshots"));
                Set("IconLocation", InstallInfo.Exe + ",0");
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        public static void Register()
        {
            var info = FileVersionInfo.GetVersionInfo(InstallInfo.Exe);
            string version = info.FileMajorPart + "." + info.FileMinorPart + "." + info.FileBuildPart;
            using (var k = Registry.CurrentUser.CreateSubKey(InstallInfo.UninstallKey))
            {
                k.SetValue("DisplayName", InstallInfo.DisplayName);
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "Beauty Apps");
                k.SetValue("DisplayIcon", InstallInfo.Exe + ",0");
                k.SetValue("InstallLocation", InstallInfo.Dir);
                k.SetValue("UninstallString", "\"" + InstallInfo.Exe + "\" --desinstalar");
                k.SetValue("URLInfoAbout", "https://beauty-shell.vercel.app/screenshot/");
                k.SetValue("EstimatedSize", (int)Math.Ceiling(new FileInfo(InstallInfo.Exe).Length / 1024.0), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void SetStartWithWindows(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(InstallInfo.RunKey))
            {
                if (on) k.SetValue(InstallInfo.RunName, "\"" + InstallInfo.Exe + "\"");
                else k.DeleteValue(InstallInfo.RunName, false);
            }
        }

        public static void Launch() => Process.Start(new ProcessStartInfo(InstallInfo.Exe) { WorkingDirectory = InstallInfo.Dir, UseShellExecute = true });
    }
}
