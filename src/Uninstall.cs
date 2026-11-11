using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace BeautyScreenshot
{
    /// <summary>
    /// "BeautyScreenshot.exe --desinstalar", what Settings → Apps runs: closes the running copy and removes
    /// everything the setup created, plus the preferences and the log. Saved captures are the user's: never touched.
    /// </summary>
    static class Uninstall
    {
        public static void Run()
        {
            var answer = MessageBox.Show(
                L.T("¿Querés desinstalar Beauty Screenshot?\n\nLas capturas que guardaste no se borran.",
                    "Uninstall Beauty Screenshot?\n\nThe screenshots you saved are not deleted."),
                InstallInfo.DisplayName, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;

            InstallInfo.CloseRunning(4000);

            using (var run = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey, true))
                run?.DeleteValue(InstallInfo.RunName, false);
            Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\BeautyScreenshot", false);
            TryDelete(() => File.Delete(InstallInfo.StartMenuLink));
            TryDelete(() => Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyScreenshot"), true));

            MessageBox.Show(L.T("Beauty Screenshot se desinstaló.", "Beauty Screenshot was uninstalled."), InstallInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);

            // The exe cannot delete itself while running: a hidden cmd removes the folder a moment after we exit.
            // Only the installed folder, never wherever a portable copy happens to live (Downloads, the desktop…).
            string dir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            if (string.Equals(dir.TrimEnd('\\'), InstallInfo.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                });
        }

        static void TryDelete(Action delete)
        {
            try { delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
