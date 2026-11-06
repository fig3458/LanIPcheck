using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanIpWatcher
{
    /// <summary>
    /// 设备名称：NBSTAT(UDP/137) → NetAPI → DNS → nbtstat 兜底。
    /// </summary>
    public static class NameResolver
    {
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetServerGetInfo(string serverName, int level, out IntPtr bufptr);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVER_INFO_101
        {
            public int PlatformId;
            public IntPtr Name;
            public int VersionMajor;
            public int VersionMinor;
            public int Type;
            public IntPtr Comment;
        }

        public static string Resolve(IPAddress ip, int timeoutMs)
        {
            if (ip == null) return "";
            int t = Math.Max(200, timeoutMs);
            string ipText = ip.ToString();

            string name = TryNetBiosUdp(ip, t);
            name = AcceptHostName(name, ipText);
            if (!string.IsNullOrEmpty(name)) return name;

            name = AcceptHostName(TryNetServerGetInfo(ip), ipText);
            if (!string.IsNullOrEmpty(name)) return name;

            name = AcceptHostName(TryDns(ip, t), ipText);
            if (!string.IsNullOrEmpty(name)) return name;

            // nbtstat 很慢且会扫多块网卡，仅作最后兜底（UDP 失败时）
            name = AcceptHostName(TryNbtstatProcess(ip, 2500), ipText);
            if (!string.IsNullOrEmpty(name)) return name;

            return "";
        }

        /// <summary>清洗并拒绝把 IP 自身当成主机名。</summary>
        public static string AcceptHostName(string name, string ipText)
        {
            name = CleanName(name);
            if (string.IsNullOrEmpty(name)) return "";
            if (!string.IsNullOrEmpty(ipText)
                && name.Equals(ipText, StringComparison.OrdinalIgnoreCase))
                return "";
            if (LooksLikeIPv4(name)) return "";
            return name;
        }

        public static bool LooksLikeIPv4(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            IPAddress addr;
            return IPAddress.TryParse(s.Trim().TrimEnd('.'), out addr)
                   && addr.AddressFamily == AddressFamily.InterNetwork;
        }

        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Trim().TrimEnd('$').TrimEnd('.');
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                string suffix = name.Substring(dot + 1).ToLowerInvariant();
                if (suffix == "local" || suffix == "lan" || suffix == "home"
                    || suffix == "workgroup" || suffix.EndsWith(".local"))
                    name = name.Substring(0, dot);
            }
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c >= 32 && c != 127) sb.Append(c);
            }
            name = sb.ToString().Trim();
            if (name.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase)) return "";
            if (name.Equals("MSBROWSE", StringComparison.OrdinalIgnoreCase)) return "";
            if (LooksLikeIPv4(name)) return "";
            return name;
        }

        private static string TryNetServerGetInfo(IPAddress ip)
        {
            try
            {
                IntPtr buf;
                int rc = NetServerGetInfo(@"\\" + ip, 101, out buf);
                if (rc != 0 || buf == IntPtr.Zero) return "";
                try
                {
                    var info = (SERVER_INFO_101)Marshal.PtrToStructure(buf, typeof(SERVER_INFO_101));
                    if (info.Name != IntPtr.Zero)
                        return Marshal.PtrToStringUni(info.Name);
                }
                finally
                {
                    NetApiBufferFree(buf);
                }
            }
            catch { }
            return "";
        }

        private static string TryDns(IPAddress ip, int timeoutMs)
        {
            string result = null;
            var done = new ManualResetEvent(false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var entry = Dns.GetHostEntry(ip);
                    if (entry != null) result = entry.HostName;
                }
                catch { }
                finally { done.Set(); }
            });
            if (!done.WaitOne(Math.Max(100, timeoutMs))) return "";
            if (string.IsNullOrEmpty(result)) return "";
            if (string.Equals(result, ip.ToString(), StringComparison.OrdinalIgnoreCase)) return "";
            return result;
        }

        private static string TryNetBiosUdp(IPAddress ip, int timeoutMs)
        {
            try
            {
                byte[] req = BuildNbStatQuery();
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = timeoutMs;
                    udp.Client.SendTimeout = timeoutMs;
                    // 不要 Connect：部分环境用 Connect+Receive 会丢包；改 Send 到指定终点
                    var target = new IPEndPoint(ip, 137);
                    udp.Send(req, req.Length, target);
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] resp = udp.Receive(ref remote);
                    return ParseNbStatResponse(resp);
                }
            }
            catch
            {
                return "";
            }
        }

        private static byte[] BuildNbStatQuery()
        {
            var pkt = new byte[50];
            int tid = Environment.TickCount ^ Thread.CurrentThread.ManagedThreadId;
            pkt[0] = (byte)(tid >> 8);
            pkt[1] = (byte)tid;
            pkt[2] = 0x00;
            pkt[3] = 0x00;
            pkt[4] = 0x00;
            pkt[5] = 0x01; // QDCOUNT=1
            pkt[12] = 0x20;
            // '*' 的标准 NB 编码
            byte[] enc = Encoding.ASCII.GetBytes("CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            Buffer.BlockCopy(enc, 0, pkt, 13, 32);
            pkt[45] = 0x00;
            pkt[46] = 0x00;
            pkt[47] = 0x21; // NBSTAT
            pkt[48] = 0x00;
            pkt[49] = 0x01; // IN
            return pkt;
        }

        private static string ParseNbStatResponse(byte[] data)
        {
            if (data == null || data.Length < 13) return "";

            int qd = (data[4] << 8) | data[5];
            int an = (data[6] << 8) | data[7];
            int offset = 12;

            for (int i = 0; i < qd; i++)
            {
                offset = SkipName(data, offset);
                if (offset + 4 > data.Length) return "";
                offset += 4; // type + class
            }

            for (int i = 0; i < an; i++)
            {
                offset = SkipName(data, offset);
                if (offset + 10 > data.Length) return "";
                int type = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                offset += 2; // class
                offset += 4; // ttl
                int rdlen = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                if (offset + rdlen > data.Length) return "";

                if (type == 0x0021 && rdlen > 1)
                {
                    string name = ParseNameList(data, offset, rdlen);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                offset += rdlen;
            }

            // 兜底：在整包里扫 15 字节名字块（防止计数异常）
            return ScanNamesHeuristic(data);
        }

        private static string ParseNameList(byte[] data, int offset, int rdlen)
        {
            int end = offset + rdlen;
            if (offset >= end) return "";
            int numNames = data[offset++];
            string best00 = "";
            string best20 = "";
            string bestOther = "";

            for (int i = 0; i < numNames && offset + 18 <= end; i++)
            {
                string raw = Encoding.ASCII.GetString(data, offset, 15).Trim();
                byte nameType = data[offset + 15];
                byte flags = data[offset + 16];
                offset += 18;

                if (string.IsNullOrEmpty(raw)) continue;
                if ((flags & 0x80) != 0) continue; // GROUP
                if (raw.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.StartsWith("\u0001") || raw.IndexOf("__MSBROWSE__", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (nameType == 0x00 && string.IsNullOrEmpty(best00)) best00 = raw;
                else if (nameType == 0x20 && string.IsNullOrEmpty(best20)) best20 = raw;
                else if (string.IsNullOrEmpty(bestOther)) bestOther = raw;
            }

            if (!string.IsNullOrEmpty(best00)) return best00;
            if (!string.IsNullOrEmpty(best20)) return best20;
            return bestOther;
        }

        private static string ScanNamesHeuristic(byte[] data)
        {
            // 找类似 "NAME            <type>" 的 18 字节记录
            string best00 = "", best20 = "";
            for (int i = 0; i + 18 <= data.Length; i++)
            {
                bool ok = true;
                int printable = 0;
                for (int j = 0; j < 15; j++)
                {
                    byte b = data[i + j];
                    if (b == 0) { ok = false; break; }
                    if (b >= 32 && b < 127) printable++;
                    else if (b != 0x20) { ok = false; break; }
                }
                if (!ok || printable < 2) continue;
                byte t = data[i + 15];
                byte fl = data[i + 16];
                if ((fl & 0x80) != 0) continue;
                if (t != 0x00 && t != 0x20 && t != 0x03) continue;
                string raw = Encoding.ASCII.GetString(data, i, 15).Trim();
                if (string.IsNullOrEmpty(raw) || raw.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (t == 0x00) return raw;
                if (t == 0x20 && string.IsNullOrEmpty(best20)) best20 = raw;
                if (t == 0x03 && string.IsNullOrEmpty(best00)) best00 = raw;
            }
            if (!string.IsNullOrEmpty(best00)) return best00;
            return best20;
        }

        private static int SkipName(byte[] data, int offset)
        {
            while (offset < data.Length)
            {
                int len = data[offset];
                if (len == 0) return offset + 1;
                if ((len & 0xC0) == 0xC0) return offset + 2; // pointer
                offset += 1 + len;
            }
            return data.Length;
        }

        private static string TryNbtstatProcess(IPAddress ip, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nbtstat.exe",
                    Arguments = "-A " + ip,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Default
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "";
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    // 匹配：NAME <00> UNIQUE  或中文环境「唯一」
                    var rx = new Regex(
                        @"^\s*([A-Za-z0-9][A-Za-z0-9_\-$]{0,14})\s+<(00|20)>\s+",
                        RegexOptions.Multiline);
                    Match m = rx.Match(output);
                    // 优先 <00>
                    Match best = Match.Empty;
                    while (m.Success)
                    {
                        string n = m.Groups[1].Value;
                        if (!n.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase))
                        {
                            if (m.Groups[2].Value == "00") return n;
                            if (!best.Success) best = m;
                        }
                        m = m.NextMatch();
                    }
                    if (best.Success) return best.Groups[1].Value;

                    // 中文码页下类型可能不是 <00> 文本旁的英文名仍在
                    rx = new Regex(@"^\s*([A-Za-z0-9][A-Za-z0-9_\-$]{0,14})\s+<", RegexOptions.Multiline);
                    m = rx.Match(output);
                    while (m.Success)
                    {
                        string n = m.Groups[1].Value;
                        if (!n.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase)
                            && !n.Equals("MAC", StringComparison.OrdinalIgnoreCase))
                            return n;
                        m = m.NextMatch();
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
