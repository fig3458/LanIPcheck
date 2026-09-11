using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LanIpWatcher
{
    /// <summary>
    /// MAC OUI → 制造商。优先加载程序目录 oui.txt，否则用内置少量常见厂商。
    /// </summary>
    public static class OuiLookup
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _map;
        private static bool _loaded;

        public static string Find(string mac)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(mac) || _map == null || _map.Count == 0) return "";

            string hex = Normalize(mac);
            if (hex.Length < 6) return "";

            // 最长前缀优先（36/28/24 bit）
            for (int len = Math.Min(hex.Length, 12); len >= 6; len--)
            {
                if ((len % 2) != 0) continue;
                string key = hex.Substring(0, len);
                string name;
                if (_map.TryGetValue(key, out name)) return name;
            }
            return "";
        }

        private static string Normalize(string mac)
        {
            var chars = new char[mac.Length];
            int n = 0;
            foreach (char c in mac)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                    chars[n++] = char.ToUpperInvariant(c);
            }
            return new string(chars, 0, n);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                _map = new Dictionary<string, string>(65536, StringComparer.OrdinalIgnoreCase);

                // 1) 嵌入压缩库（单文件独立运行）
                try { LoadEmbeddedGzip("LanIpWatcher.oui.gz"); }
                catch { }
                if (_map.Count == 0)
                {
                    try
                    {
                        foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                        {
                            if (n.EndsWith("oui.gz", StringComparison.OrdinalIgnoreCase))
                            {
                                LoadEmbeddedGzip(n);
                                if (_map.Count > 0) break;
                            }
                        }
                    }
                    catch { }
                }

                // 2) 外部 oui.txt 可覆盖/补充（方便更新厂商库）
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    string path = Path.Combine(dir, "oui.txt");
                    if (File.Exists(path))
                        LoadFile(path);
                }
                catch
                {
                }

                // 3) 兜底
                if (_map.Count == 0)
                    LoadBuiltin();

                _loaded = true;
            }
        }

        private static void LoadEmbeddedGzip(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var raw = asm.GetManifestResourceStream(resourceName))
            {
                if (raw == null) return;
                using (var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, System.Text.Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string key = line.Substring(0, tab).Trim().ToUpperInvariant();
                        string name = line.Substring(tab + 1).Trim();
                        if (key.Length >= 6 && name.Length > 0)
                            _map[key] = name;
                    }
                }
            }
        }

        private static void LoadFile(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string key = line.Substring(0, tab).Trim().ToUpperInvariant();
                string name = line.Substring(tab + 1).Trim();
                if (key.Length >= 6 && name.Length > 0)
                    _map[key] = name;
            }
        }

        private static void LoadBuiltin()
        {
            // 少量兜底
            Add("F8BC12", "Dell Inc.");
            Add("8CEC4B", "Dell Inc.");
            Add("D4BED9", "Dell Inc.");
            Add("941457", "Shenzhen Sundray Technologies");
            Add("001A2B", "Ayecom Technology");
            Add("000C29", "VMware");
            Add("005056", "VMware");
            Add("00155D", "Microsoft Hyper-V");
            Add("525400", "QEMU/KVM");
            Add("B827EB", "Raspberry Pi");
            Add("DCA632", "Raspberry Pi");
            Add("E45F01", "Raspberry Pi");
            Add("001D0F", "TP-Link");
            Add("50C7BF", "TP-Link");
            Add("F4F26D", "TP-Link");
            Add("C0463D", "TP-Link");
            Add("A0F3C1", "TP-Link");
            Add("14CC20", "TP-Link");
            Add("EC086B", "TP-Link");
            Add("28EE52", "TP-Link");
            Add("60E327", "TP-Link");
            Add("001E58", "D-Link");
            Add("1C7EE5", "D-Link");
            Add("00265A", "D-Link");
            Add("000D88", "D-Link");
            Add("001632", "Samsung");
            Add("5C0A5B", "Samsung");
            Add("8C7716", "Samsung");
            Add("F0EE10", "Samsung");
            Add("001A11", "Google");
            Add("3C5AB4", "Google");
            Add("54E019", "Apple");
            Add("F0D1A9", "Apple");
            Add("A4C361", "Apple");
            Add("ACBC32", "Apple");
            Add("F4F15A", "Apple");
            Add("88E9FE", "Apple");
            Add("001B63", "Apple");
            Add("3C0754", "Apple");
            Add("A4B197", "Apple");
            Add("DC2B61", "Apple");
            Add("F0B479", "Apple");
            Add("001F5B", "Apple");
            Add("002608", "Apple");
            Add("7C6D62", "Apple");
            Add("9801A7", "Apple");
            Add("BC4CC4", "Apple");
            Add("000C43", "Ralink/MediaTek");
            Add("00E04C", "Realtek");
            Add("52:54:00", "QEMU"); // will normalize
            Add("001E10", "Huawei");
            Add("00E0FC", "Huawei");
            Add("0C96BF", "Huawei");
            Add("20F17C", "Huawei");
            Add("48AD08", "Huawei");
            Add("7054F5", "Huawei");
            Add("AC853D", "Huawei");
            Add("CC53B5", "Huawei");
            Add("F4C714", "Huawei");
            Add("000FE2", "Huawei");
            Add("04BD88", "Xiaomi");
            Add("64CC2E", "Xiaomi");
            Add("78D752", "Xiaomi");
            Add("F0B429", "Xiaomi");
            Add("28E31F", "Xiaomi");
            Add("0019E0", "TP-Link");
            Add("002719", "TP-Link");
            Add("60A4D0", "Huawei");
            Add("B41513", "Huawei");
            Add("D4EE07", "HIWIFI");
            Add("001B44", "SanDisk");
            Add("00054F", "Garmin");
            Add("0017FA", "Microsoft");
            Add("605BB4", "AzureWave");
            Add("74D02B", "ASUSTek");
            Add("2C4D54", "ASUSTek");
            Add("04D4C4", "ASUSTek");
            Add("1C872C", "ASUSTek");
            Add("AC9E17", "ASUSTek");
            Add("50465D", "ASUSTek");
            Add("00E018", "ASUSTek");
            Add("001FC6", "ASUSTek");
            Add("2C56DC", "ASUSTek");
            Add("107B44", "ASUSTek");
            Add("08EDB9", "Asus");
            Add("00D0B7", "Intel");
            Add("001B21", "Intel");
            Add("3CD92B", "Intel");
            Add("A0A8CD", "Intel");
            Add("F8F21E", "Intel");
            Add("8C8D28", "Intel");
            Add("DC41A9", "Intel");
            Add("001651", "Intel");
            Add("4C3488", "Intel");
            Add("5C514F", "Intel");
            Add("001E67", "Intel");
            Add("000E0C", "Intel");
            Add("7C7A91", "Intel");
            Add("B80305", "Intel");
            Add("0013CE", "Intel");
            Add("0016EA", "Intel");
            Add("0019D1", "Intel");
            Add("001B77", "Intel");
            Add("001E64", "Intel");
            Add("00215C", "Intel");
            Add("00216A", "Intel");
            Add("0022FA", "Intel");
            Add("0024D6", "Intel");
            Add("0026C6", "Intel");
            Add("3C970E", "Intel");
            Add("448500", "Intel");
            Add("88532E", "Intel");
            Add("9C4E36", "Intel");
            Add("B4B676", "Intel");
            Add("D8FC93", "Intel");
            Add("F46D04", "ASUSTek");
            Add("00A0C9", "Intel");
            Add("0C8BFD", "Intel");
            Add("E4B318", "Intel");
            Add("F8AC65", "Intel");
        }

        private static void Add(string oui, string name)
        {
            string key = Normalize(oui);
            if (key.Length >= 6) _map[key.Substring(0, Math.Min(key.Length, 12))] = name;
        }
    }
}
