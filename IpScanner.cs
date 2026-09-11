using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LanIpWatcher
{
    public enum HostStatus
    {
        Unknown,
        Free,
        Occupied
    }

    public class ScanResult
    {
        public string Ip { get; set; }
        public HostStatus Status { get; set; }
        public string Methods { get; set; }
        public string Hostname { get; set; }
        public string Mac { get; set; }
        public string Vendor { get; set; }
        public string Detail { get; set; }
        public int LatencyMs { get; set; }
    }

    public class ScanProgressEventArgs : EventArgs
    {
        public int Done { get; set; }
        public int Total { get; set; }
        public ScanResult Result { get; set; }
        public string Phase { get; set; }
    }

    public class IpScanner
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint physicalAddrLen);

        public event EventHandler<ScanProgressEventArgs> Progress;

        public List<ScanResult> ScanRange(
            IPAddress start,
            IPAddress end,
            AppConfig cfg,
            CancellationToken token)
        {
            // 提高线程池下限，避免高并发时排队变慢
            int workers, io;
            ThreadPool.GetMinThreads(out workers, out io);
            int want = Math.Max(workers, Math.Min(256, Math.Max(64, cfg.MaxParallel * 2)));
            ThreadPool.SetMinThreads(want, Math.Max(io, 64));

            var ips = ExpandRange(start, end);
            var map = new Dictionary<string, ScanResult>(ips.Count);
            var mapLock = new object();
            int total = ips.Count;
            int[] ports = ParsePorts(cfg.TcpPorts);
            var gatewayMacs = GetGatewayMacSet();
            int timeout = Math.Max(60, cfg.TimeoutMs);
            int parallel = Math.Max(32, cfg.MaxParallel);

            // —— 阶段1：ARP + ICMP 快扫（局域网主要靠这个，类似 Advanced IP Scanner）——
            int done = 0;
            RunParallel(ips, parallel, token, ip =>
            {
                var r = ProbeFast(ip, cfg, gatewayMacs, timeout, token);
                lock (mapLock) { map[ip.ToString()] = r; }
                int d = Interlocked.Increment(ref done);
                RaiseProgress(d, total, r, "快扫");
            });

            DemoteProxyArpOnly(map, Math.Max(8, total / 10));

            // —— 阶段2：仅对仍空闲的地址做少量 TCP（避免拖慢整段）——
            if (cfg.UseTcp && ports.Length > 0 && !token.IsCancellationRequested)
            {
                var needTcp = new List<IPAddress>();
                foreach (var ip in ips)
                {
                    ScanResult r;
                    if (map.TryGetValue(ip.ToString(), out r) && r.Status != HostStatus.Occupied)
                        needTcp.Add(ip);
                }

                if (needTcp.Count > 0)
                {
                    int tcpDone = 0;
                    int tcpTotal = needTcp.Count;
                    // TCP 端口少、超时短；并发可以更高
                    int[] fastPorts = ports.Length > 3
                        ? new[] { ports[0], ports[1], ports[2] }
                        : ports;
                    int tcpTimeout = Math.Min(timeout, 180);

                    RunParallel(needTcp, Math.Max(parallel, 96), token, ip =>
                    {
                        ScanResult cur;
                        lock (mapLock) { cur = map[ip.ToString()]; }
                        int openPort;
                        int ms;
                        bool refused;
                        if (TryTcpAny(ip, fastPorts, tcpTimeout, token, out openPort, out ms, out refused))
                        {
                            if (!refused)
                            {
                                cur.Status = HostStatus.Occupied;
                                cur.Methods = AppendMethod(cur.Methods, "TCP");
                                cur.Detail = AppendDetail(cur.Detail, "TCP开放:" + openPort);
                                cur.LatencyMs = ms;
                            }
                            else
                            {
                                // 有 ARP 才认拒绝；快扫阶段若已有 ARP 会已是占用
                                // 这里无 ARP 的 RST 忽略，防误报
                            }
                        }
                        int d = Interlocked.Increment(ref tcpDone);
                        // 进度映射到总进度的后半观感：保持 Done<=Total
                        RaiseProgress(Math.Min(total, total - tcpTotal + d), total, cur, "TCP补扫");
                    });
                }
            }

            DemoteProxyArpOnly(map, Math.Max(8, total / 10));

            // —— 阶段3：占用主机补全制造商 + 主机名（不参与判定，只丰富悬停信息）——
            var occupied = new List<ScanResult>();
            foreach (var ip in ips)
            {
                ScanResult r;
                if (map.TryGetValue(ip.ToString(), out r) && r.Status == HostStatus.Occupied)
                    occupied.Add(r);
            }

            if (occupied.Count > 0 && !token.IsCancellationRequested)
            {
                RunParallel(occupied, Math.Min(64, Math.Max(16, occupied.Count)), token, r =>
                {
                    if (!string.IsNullOrEmpty(r.Mac))
                    {
                        r.Vendor = OuiLookup.Find(r.Mac);
                        if (!string.IsNullOrEmpty(r.Vendor))
                            r.Detail = AppendDetail(r.Detail, "厂商:" + r.Vendor);
                    }

                    // 再试一次 ARP 补 MAC（有的主机 ICMP 通但 ARP 稍后才有）
                    if (string.IsNullOrEmpty(r.Mac))
                    {
                        IPAddress addr;
                        if (IPAddress.TryParse(r.Ip, out addr))
                        {
                            string m;
                            int ms;
                            if (TryArp(addr, out m, out ms))
                            {
                                r.Mac = m;
                                r.Vendor = OuiLookup.Find(m);
                            }
                        }
                    }

                    string host = "";
                    try
                    {
                        // 局域网名主要靠 NBSTAT，超时给足一些
                        host = NameResolver.Resolve(IPAddress.Parse(r.Ip), 800);
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(host))
                        r.Hostname = host;

                    RaiseProgress(total, total, r, "名称");
                });
            }

            var ordered = new List<ScanResult>(ips.Count);
            foreach (var ip in ips)
            {
                ScanResult r;
                if (map.TryGetValue(ip.ToString(), out r))
                    ordered.Add(r);
                else
                    ordered.Add(new ScanResult
                    {
                        Ip = ip.ToString(),
                        Status = HostStatus.Unknown,
                        Methods = "-",
                        Detail = "已取消",
                        LatencyMs = -1
                    });
            }
            return ordered;
        }

        private void RaiseProgress(int done, int total, ScanResult r, string phase)
        {
            var handler = Progress;
            if (handler != null)
                handler(this, new ScanProgressEventArgs
                {
                    Done = done,
                    Total = total,
                    Result = r,
                    Phase = phase
                });
        }

        private static void RunParallel<T>(
            IList<T> items,
            int parallel,
            CancellationToken token,
            Action<T> body)
        {
            if (items == null || items.Count == 0) return;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallel),
                CancellationToken = token
            };
            try
            {
                Parallel.ForEach(items, options, item =>
                {
                    token.ThrowIfCancellationRequested();
                    body(item);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException ae)
            {
                ae.Handle(ex => ex is OperationCanceledException);
            }
        }

        private ScanResult ProbeFast(
            IPAddress ip,
            AppConfig cfg,
            HashSet<string> gatewayMacs,
            int timeout,
            CancellationToken token)
        {
            var hit = new List<string>();
            var detail = new List<string>();
            string mac = "";
            int bestLatency = int.MaxValue;
            bool occupied = false;

            // 先 ARP，未命中再 ICMP（避免每主机再套 Task 导致线程过多）
            if (cfg.UseArp && !token.IsCancellationRequested)
            {
                string m;
                int ms;
                if (TryArp(ip, out m, out ms))
                {
                    mac = m;
                    bool isGw = gatewayMacs != null && gatewayMacs.Contains(NormalizeMac(m));
                    if (isGw)
                    {
                        detail.Add("ARP=网关MAC忽略");
                    }
                    else
                    {
                        occupied = true;
                        hit.Add("ARP");
                        detail.Add("ARP:" + m);
                        if (ms < bestLatency) bestLatency = ms;
                    }
                }
            }

            if (!occupied && cfg.UseIcmp && !token.IsCancellationRequested)
            {
                int ms;
                if (TryIcmp(ip, timeout, out ms))
                {
                    occupied = true;
                    hit.Add("ICMP");
                    detail.Add("ICMP " + ms + "ms");
                    if (ms < bestLatency) bestLatency = ms;
                }
            }

            if (occupied && string.IsNullOrEmpty(mac) && cfg.UseArp && !token.IsCancellationRequested)
            {
                string m;
                int ms;
                if (TryArp(ip, out m, out ms))
                {
                    mac = m;
                    detail.Add("MAC:" + m);
                }
            }

            string vendor = string.IsNullOrEmpty(mac) ? "" : OuiLookup.Find(mac);

            return new ScanResult
            {
                Ip = ip.ToString(),
                Status = occupied ? HostStatus.Occupied : HostStatus.Free,
                Methods = occupied ? string.Join("+", hit.ToArray()) : "-",
                Hostname = "",
                Mac = mac ?? "",
                Vendor = vendor,
                Detail = occupied
                    ? string.Join("; ", detail.ToArray())
                    : (detail.Count > 0 ? string.Join("; ", detail.ToArray()) : "无响应"),
                LatencyMs = occupied ? (bestLatency == int.MaxValue ? -1 : bestLatency) : -1
            };
        }

        private static string AppendMethod(string methods, string add)
        {
            if (string.IsNullOrEmpty(methods) || methods == "-") return add;
            if (methods.IndexOf(add, StringComparison.OrdinalIgnoreCase) >= 0) return methods;
            return methods + "+" + add;
        }

        private static string AppendDetail(string detail, string add)
        {
            if (string.IsNullOrEmpty(detail) || detail == "无响应") return add;
            return detail + "; " + add;
        }

        private static void DemoteProxyArpOnly(Dictionary<string, ScanResult> map, int threshold)
        {
            var macCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in map.Values)
            {
                if (r == null || string.IsNullOrEmpty(r.Mac)) continue;
                if (!macCount.ContainsKey(r.Mac)) macCount[r.Mac] = 0;
                macCount[r.Mac]++;
            }

            foreach (var r in map.Values)
            {
                if (r == null || r.Status != HostStatus.Occupied) continue;
                if (string.IsNullOrEmpty(r.Mac)) continue;
                int c;
                if (!macCount.TryGetValue(r.Mac, out c) || c < threshold) continue;

                bool hasStrong = r.Methods != null
                    && (r.Methods.IndexOf("ICMP", StringComparison.OrdinalIgnoreCase) >= 0
                        || r.Methods.IndexOf("TCP", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasStrong) continue;

                if (r.Methods != null && r.Methods.IndexOf("ARP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    r.Status = HostStatus.Free;
                    r.Methods = "-";
                    r.Detail = "忽略代理ARP(" + r.Mac + ")";
                    r.LatencyMs = -1;
                    r.Vendor = "";
                }
            }
        }

        private static HashSet<string> GetGatewayMacSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var props = ni.GetIPProperties();
                    if (props == null || props.GatewayAddresses == null) continue;
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address == null || gw.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string m;
                        int ms;
                        if (TryArp(gw.Address, out m, out ms) && !string.IsNullOrEmpty(m))
                            set.Add(NormalizeMac(m));
                    }
                }
            }
            catch
            {
            }
            return set;
        }

        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            return mac.Trim().ToUpperInvariant().Replace("-", ":");
        }

        private static bool TryIcmp(IPAddress ip, int timeoutMs, out int latencyMs)
        {
            latencyMs = -1;
            try
            {
                // 每线程独立 Ping，不再全局锁（旧版锁是慢的主因之一）
                using (var ping = new Ping())
                {
                    var reply = ping.Send(ip, Math.Max(40, timeoutMs));
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        latencyMs = (int)reply.RoundtripTime;
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryArp(IPAddress ip, out string mac, out int latencyMs)
        {
            mac = "";
            latencyMs = -1;
            try
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
                byte[] addr = ip.GetAddressBytes();
                int dest = BitConverter.ToInt32(addr, 0);
                byte[] macBytes = new byte[6];
                uint len = (uint)macBytes.Length;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int ret = SendARP(dest, 0, macBytes, ref len);
                sw.Stop();
                if (ret == 0 && len >= 6)
                {
                    bool allZero = true;
                    for (int i = 0; i < 6; i++)
                        if (macBytes[i] != 0) { allZero = false; break; }
                    if (allZero) return false;
                    if ((macBytes[0] & 0x01) != 0) return false;
                    mac = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                        macBytes[0], macBytes[1], macBytes[2], macBytes[3], macBytes[4], macBytes[5]);
                    latencyMs = (int)sw.ElapsedMilliseconds;
                    if (latencyMs == 0) latencyMs = 1;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryTcpAny(
            IPAddress ip,
            int[] ports,
            int timeoutMs,
            CancellationToken token,
            out int openPort,
            out int latencyMs,
            out bool refused)
        {
            openPort = -1;
            latencyMs = -1;
            refused = false;

            // 端口并行探测，任一成功即返回
            int foundPort = -1;
            int foundMs = -1;
            int foundRefused = 0; // 0 none, 1 refused, 2 open
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(ports.Length, 4),
                CancellationToken = token
            };
            try
            {
                Parallel.ForEach(ports, opts, (port, state) =>
                {
                    if (token.IsCancellationRequested) { state.Stop(); return; }
                    int ms;
                    bool wasRefused;
                    if (TryTcp(ip, port, timeoutMs, out ms, out wasRefused))
                    {
                        if (!wasRefused)
                        {
                            Interlocked.CompareExchange(ref foundPort, port, -1);
                            Interlocked.CompareExchange(ref foundMs, ms, -1);
                            Interlocked.Exchange(ref foundRefused, 2);
                            state.Stop();
                        }
                        else if (Interlocked.CompareExchange(ref foundRefused, 1, 0) == 0)
                        {
                            Interlocked.CompareExchange(ref foundPort, port, -1);
                            Interlocked.CompareExchange(ref foundMs, ms, -1);
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            if (foundRefused == 2)
            {
                openPort = foundPort;
                latencyMs = foundMs;
                refused = false;
                return true;
            }
            if (foundRefused == 1)
            {
                openPort = foundPort;
                latencyMs = foundMs;
                refused = true;
                return true;
            }
            return false;
        }

        private static bool TryTcp(IPAddress ip, int port, int timeoutMs, out int latencyMs, out bool refused)
        {
            latencyMs = -1;
            refused = false;
            try
            {
                using (var client = new TcpClient())
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    IAsyncResult ar = client.BeginConnect(ip, port, null, null);
                    bool completed = ar.AsyncWaitHandle.WaitOne(Math.Max(40, timeoutMs));
                    if (!completed)
                    {
                        try { client.Close(); } catch { }
                        return false;
                    }
                    try
                    {
                        client.EndConnect(ar);
                        sw.Stop();
                        latencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds);
                        refused = false;
                        return true;
                    }
                    catch (SocketException ex)
                    {
                        sw.Stop();
                        if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                        {
                            latencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds);
                            refused = true;
                            return true;
                        }
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryReverseDns(IPAddress ip, int timeoutMs, out string name)
        {
            name = "";
            try
            {
                string resultName = null;
                Exception error = null;
                var done = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var entry = Dns.GetHostEntry(ip);
                        if (entry != null) resultName = entry.HostName;
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                if (!done.WaitOne(Math.Max(80, timeoutMs))) return false;
                if (error != null || string.IsNullOrEmpty(resultName)) return false;
                if (string.Equals(resultName, ip.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
                name = resultName;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static int[] ParsePorts(string text)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return list.ToArray();
            foreach (var part in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int p;
                if (int.TryParse(part.Trim(), out p) && p > 0 && p <= 65535 && !list.Contains(p))
                    list.Add(p);
            }
            return list.ToArray();
        }

        public static List<IPAddress> ExpandRange(IPAddress start, IPAddress end)
        {
            uint s = ToUInt(start);
            uint e = ToUInt(end);
            if (e < s)
            {
                uint t = s; s = e; e = t;
            }
            if (e - s > 1024)
                throw new ArgumentException("单次扫描最多支持 1025 个地址。");

            var list = new List<IPAddress>();
            for (uint i = s; i <= e; i++)
            {
                list.Add(FromUInt(i));
                if (i == uint.MaxValue) break;
            }
            return list;
        }

        public static bool TryParseIp(string text, out IPAddress ip)
        {
            return IPAddress.TryParse((text ?? "").Trim(), out ip)
                   && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        private static uint ToUInt(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint v)
        {
            return new IPAddress(new byte[]
            {
                (byte)(v >> 24),
                (byte)(v >> 16),
                (byte)(v >> 8),
                (byte)v
            });
        }

        public static string SuggestLocalRange(out string start, out string end)
        {
            start = "192.168.1.1";
            end = "192.168.1.254";
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask != null ? ua.IPv4Mask.GetAddressBytes() : new byte[] { 255, 255, 255, 0 };
                        byte[] net = new byte[4];
                        byte[] bcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            net[i] = (byte)(ip[i] & mask[i]);
                            bcast[i] = (byte)(net[i] | ~mask[i]);
                        }
                        uint n = ToUInt(new IPAddress(net));
                        uint b = ToUInt(new IPAddress(bcast));
                        uint first = n + 1;
                        uint last = b > 0 ? b - 1 : b;
                        if (last - first > 254)
                        {
                            start = ip[0] + "." + ip[1] + "." + ip[2] + ".1";
                            end = ip[0] + "." + ip[1] + "." + ip[2] + ".254";
                        }
                        else
                        {
                            start = FromUInt(first).ToString();
                            end = FromUInt(last).ToString();
                        }
                        return ua.Address.ToString();
                    }
                }
            }
            catch
            {
            }
            return "";
        }
    }
}
