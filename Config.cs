using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LanIpWatcher
{
    [DataContract]
    public class AppConfig
    {
        [DataMember] public string StartIp { get; set; }
        [DataMember] public string EndIp { get; set; }
        [DataMember] public int IntervalSeconds { get; set; }
        [DataMember] public bool AutoStartScan { get; set; }
        [DataMember] public bool UseIcmp { get; set; }
        [DataMember] public bool UseArp { get; set; }
        [DataMember] public bool UseTcp { get; set; }
        [DataMember] public bool UseDns { get; set; }
        [DataMember] public string TcpPorts { get; set; }
        [DataMember] public int TimeoutMs { get; set; }
        [DataMember] public int MaxParallel { get; set; }
        [DataMember] public bool MinimizeToTray { get; set; }

        public static AppConfig Default()
        {
            return new AppConfig
            {
                StartIp = "192.168.1.1",
                EndIp = "192.168.1.254",
                IntervalSeconds = 60,
                AutoStartScan = false,
                UseIcmp = true,
                UseArp = true,
                UseTcp = true,
                UseDns = false,
                TcpPorts = "445,80,3389",
                TimeoutMs = 100,
                MaxParallel = 128,
                MinimizeToTray = true
            };
        }

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LanIpWatcher");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return Default();
                using (var fs = File.OpenRead(ConfigPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(AppConfig));
                    var cfg = (AppConfig)ser.ReadObject(fs);
                    if (cfg == null) return Default();
                    // 迁移旧版易误报配置
                    if (!string.IsNullOrEmpty(cfg.TcpPorts) &&
                        (cfg.TcpPorts.IndexOf("9100", StringComparison.Ordinal) >= 0
                         || cfg.TcpPorts.IndexOf("8291", StringComparison.Ordinal) >= 0
                         || cfg.TcpPorts.IndexOf("548", StringComparison.Ordinal) >= 0))
                    {
                        cfg.TcpPorts = Default().TcpPorts;
                    }
                    if (string.IsNullOrWhiteSpace(cfg.TcpPorts)) cfg.TcpPorts = Default().TcpPorts;
                    // 旧版偏慢默认值自动提速
                    if (cfg.TimeoutMs >= 300) cfg.TimeoutMs = 100;
                    if (cfg.MaxParallel > 0 && cfg.MaxParallel <= 48) cfg.MaxParallel = 128;
                    if (cfg.TcpPorts != null && cfg.TcpPorts.Split(',').Length >= 6)
                        cfg.TcpPorts = Default().TcpPorts;
                    cfg.UseDns = false;
                    if (cfg.TimeoutMs < 40) cfg.TimeoutMs = 100;
                    if (cfg.MaxParallel < 1) cfg.MaxParallel = 128;
                    if (cfg.IntervalSeconds < 5) cfg.IntervalSeconds = 60;
                    return cfg;
                }
            }
            catch
            {
                return Default();
            }
        }

        public void Save()
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(AppConfig));
                ser.WriteObject(ms, this);
                File.WriteAllText(ConfigPath, Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8);
            }
        }
    }
}
