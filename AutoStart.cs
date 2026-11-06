using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanIpWatcher
{
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "LanIpWatcher";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(AppName);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) throw new InvalidOperationException("无法打开开机启动注册表项。");
                if (enabled)
                {
                    string exe = Application.ExecutablePath;
                    if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
                        exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    key.SetValue(AppName, "\"" + exe + "\" --autostart");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }
    }
}
