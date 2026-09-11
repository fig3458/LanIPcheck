using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LanIpWatcher
{
    /// <summary>
    /// Win7 无 “Microsoft YaHei UI”，回退到雅黑 / Segoe UI / Tahoma。
    /// </summary>
    public static class UiFonts
    {
        private static string _family;

        public static string Family
        {
            get
            {
                if (_family != null) return _family;
                string[] prefer = {
                    "Microsoft YaHei UI",
                    "Microsoft YaHei",
                    "Segoe UI",
                    "Tahoma",
                    "SimSun",
                    "Arial"
                };
                var installed = new InstalledFontCollection();
                foreach (string name in prefer)
                {
                    foreach (FontFamily ff in installed.Families)
                    {
                        if (string.Equals(ff.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            _family = ff.Name;
                            return _family;
                        }
                    }
                }
                _family = SystemFonts.MessageBoxFont.FontFamily.Name;
                return _family;
            }
        }

        public static Font Create(float size)
        {
            return new Font(Family, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font Create(float size, FontStyle style)
        {
            return new Font(Family, size, style, GraphicsUnit.Point);
        }
    }
}
