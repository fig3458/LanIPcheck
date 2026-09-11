using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanIpWatcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Win7 = 6.1；更低给出提示（仍尝试运行）
            var os = Environment.OSVersion;
            if (os.Version.Major < 6 || (os.Version.Major == 6 && os.Version.Minor < 1))
            {
                MessageBox.Show(
                    "当前系统过旧。本程序建议在 Windows 7 / 8 / 10 / 11 上运行。",
                    "LanIpWatcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            bool auto = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase))
                    {
                        auto = true;
                        break;
                    }
                }
            }

            try
            {
                Application.Run(new MainForm(auto));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "程序启动失败：\n" + ex.Message +
                    "\n\n若提示缺少运行库，请安装 .NET Framework 4.0 或更高版本（推荐 4.8）：\n" +
                    "https://dotnet.microsoft.com/download/dotnet-framework/net48",
                    "LanIpWatcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>供安装脚本/外部调用检测（本进程能跑起来通常已满足）。</summary>
        public static bool HasNet4()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key != null && Equals(key.GetValue("Install"), 1)) return true;
                }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Client"))
                {
                    if (key != null && Equals(key.GetValue("Install"), 1)) return true;
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
