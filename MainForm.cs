using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LanIpWatcher
{
    public class MainForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly IpScanner _scanner = new IpScanner();
        private CancellationTokenSource _cts;
        private readonly System.Windows.Forms.Timer _scheduleTimer = new System.Windows.Forms.Timer();
        private bool _scanRunning;
        private NotifyIcon _tray;
        private bool _exitRequested;
        private readonly HashSet<string> _everOccupied =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ScanResult> _latest =
            new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IdentityTrack> _identity =
            new Dictionary<string, IdentityTrack>(StringComparer.OrdinalIgnoreCase);

        private TextBox txtStart;
        private TextBox txtEnd;
        private NumericUpDown numInterval;
        private CheckBox chkAutoStart;
        private CheckBox chkSchedule;
        private Button btnScan;
        private Button btnStop;
        private Button btnSettings;
        private Button btnExport;
        private Label lblStatus;
        private Label lblSummary;
        private ProgressBar progress;
        private HeatMapControl heat;
        private Panel settingsPanel;
        private bool _settingsOpen;
        private NumericUpDown numTimeout;
        private NumericUpDown numParallel;
        private TextBox txtPorts;
        private CheckBox chkIcmp;
        private CheckBox chkArp;
        private CheckBox chkTcp;
        private CheckBox chkDns;
        private CheckBox chkTray;

        public MainForm(bool launchedFromAutoStart)
        {
            _cfg = AppConfig.Load();
            LoadHistory();
            LoadConflicts();

            Text = "IP 占用图";
            Width = 980;
            Height = 720;
            MinimumSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = UiFonts.Create(9F);
            BackColor = Color.FromArgb(248, 249, 251);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            BuildUi();
            LoadConfigToUi();
            InitTray();
            PrepareGridFromRange(false);
            // 预热厂商库（oui.txt）
            ThreadPool.QueueUserWorkItem(_ => { try { OuiLookup.Find("000000000000"); } catch { } });

            _scheduleTimer.Tick += ScheduleTimer_Tick;
            FormClosing += MainForm_FormClosing;
            Resize += MainForm_Resize;

            Shown += (s, e) =>
            {
                if (launchedFromAutoStart)
                {
                    if (_cfg.MinimizeToTray)
                    {
                        Hide();
                        _tray.Visible = true;
                    }
                    chkSchedule.Checked = true;
                    StartScheduledMode();
                }
                BeginScanAsync(false);
            };
        }

        private void BuildUi()
        {
            var top = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.White,
                Padding = new Padding(12, 10, 12, 10)
            };
            top.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(230, 232, 236)))
                    e.Graphics.DrawLine(pen, 0, top.Height - 1, top.Width, top.Height - 1);
            };

            txtStart = new TextBox { Left = 12, Top = 12, Width = 118 };
            txtEnd = new TextBox { Left = 142, Top = 12, Width = 118 };
            var dash = new Label { Text = "—", Left = 130, Top = 14, AutoSize = true, ForeColor = Color.Gray };

            btnScan = MakeBtn("扫描", 278, 10, 72, (s, e) => BeginScanAsync(true));
            btnStop = MakeBtn("停止", 356, 10, 64, Stop_Click);
            btnStop.Enabled = false;

            chkSchedule = new CheckBox { Text = "定时", Left = 436, Top = 14, Width = 55, Checked = false };
            numInterval = new NumericUpDown
            {
                Left = 492,
                Top = 12,
                Width = 58,
                Minimum = 5,
                Maximum = 86400,
                Value = 60
            };
            var sec = new Label { Text = "秒", Left = 552, Top = 14, AutoSize = true, ForeColor = Color.Gray };

            chkAutoStart = new CheckBox { Text = "开机启动", Left = 590, Top = 14, Width = 88 };
            chkAutoStart.CheckedChanged += (s, e) =>
            {
                try { AutoStart.SetEnabled(chkAutoStart.Checked); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "开机启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            btnSettings = MakeBtn("设置", 690, 10, 64, (s, e) => ToggleSettings());
            btnExport = MakeBtn("导出", 760, 10, 64, Export_Click);

            lblSummary = new Label
            {
                Left = 830,
                Top = 14,
                Width = 120,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(60, 70, 85),
                Text = ""
            };

            top.Controls.Add(txtStart);
            top.Controls.Add(dash);
            top.Controls.Add(txtEnd);
            top.Controls.Add(btnScan);
            top.Controls.Add(btnStop);
            top.Controls.Add(chkSchedule);
            top.Controls.Add(numInterval);
            top.Controls.Add(sec);
            top.Controls.Add(chkAutoStart);
            top.Controls.Add(btnSettings);
            top.Controls.Add(btnExport);
            top.Controls.Add(lblSummary);

            settingsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 0,
                Visible = false,
                BackColor = Color.FromArgb(252, 252, 253),
                Padding = new Padding(12)
            };
            BuildSettingsPanel();

            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.White
            };
            bottom.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(230, 232, 236)))
                    e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
            };

            progress = new ProgressBar { Left = 12, Top = 11, Width = 160, Height = 16 };
            lblStatus = new Label
            {
                Left = 186,
                Top = 11,
                Width = 420,
                Height = 18,
                ForeColor = Color.FromArgb(90, 98, 110),
                Text = "就绪"
            };

            // 图例
            int lx = 560;
            bottom.Controls.Add(MakeLegend(lx, HeatMapControl.ColorOccupied, "占用"));
            bottom.Controls.Add(MakeLegend(lx + 58, HeatMapControl.ColorFree, "空闲"));
            bottom.Controls.Add(MakeLegend(lx + 116, HeatMapControl.ColorWas, "曾占用"));
            bottom.Controls.Add(MakeLegend(lx + 188, HeatMapControl.ColorConflict, "冲突"));
            bottom.Controls.Add(progress);
            bottom.Controls.Add(lblStatus);

            heat = new HeatMapControl { Dock = DockStyle.Fill };
            heat.CellClick += (s, cell) =>
            {
                ScanResult r;
                _latest.TryGetValue(cell.Ip, out r);
                MessageBox.Show(this, BuildTip(cell.Ip, cell.Visual, r), "设备详情",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            heat.CellHover += (s, cell) =>
            {
                if (cell == null) return;
                ScanResult r;
                if (_latest.TryGetValue(cell.Ip, out r) && !string.IsNullOrEmpty(r.Hostname))
                    lblStatus.Text = cell.Ip + "  ·  " + r.Hostname
                        + (string.IsNullOrEmpty(r.Vendor) ? "" : ("  ·  " + r.Vendor));
                else if (cell != null)
                    lblStatus.Text = cell.Ip;
            };

            Controls.Add(heat);
            Controls.Add(bottom);
            Controls.Add(settingsPanel);
            Controls.Add(top);

            chkSchedule.CheckedChanged += (s, e) =>
            {
                if (chkSchedule.Checked) StartScheduledMode();
                else StopScheduledMode();
            };
            txtStart.Leave += (s, e) => PrepareGridFromRange(false);
            txtEnd.Leave += (s, e) => PrepareGridFromRange(false);
        }

        private void BuildSettingsPanel()
        {
            chkArp = new CheckBox { Left = 12, Top = 12, Width = 60, Text = "ARP", Checked = true };
            chkIcmp = new CheckBox { Left = 80, Top = 12, Width = 70, Text = "ICMP", Checked = true };
            chkTcp = new CheckBox { Left = 155, Top = 12, Width = 60, Text = "TCP", Checked = true };
            chkDns = new CheckBox { Left = 220, Top = 12, Width = 120, Text = "反查主机名", Checked = false };
            chkTray = new CheckBox { Left = 350, Top = 12, Width = 140, Text = "关闭到托盘", Checked = true };

            var l1 = new Label { Text = "超时ms", Left = 12, Top = 48, AutoSize = true };
            numTimeout = new NumericUpDown { Left = 70, Top = 44, Width = 70, Minimum = 40, Maximum = 5000, Value = 100 };
            var l2 = new Label { Text = "并发", Left = 160, Top = 48, AutoSize = true };
            numParallel = new NumericUpDown { Left = 200, Top = 44, Width = 60, Minimum = 1, Maximum = 256, Value = 48 };
            var l3 = new Label { Text = "TCP端口", Left = 280, Top = 48, AutoSize = true };
            txtPorts = new TextBox { Left = 350, Top = 44, Width = 400 };

            var btnLan = MakeBtn("填本机网段", 760, 42, 100, (s, e) =>
            {
                string a, b;
                string local = IpScanner.SuggestLocalRange(out a, out b);
                txtStart.Text = a;
                txtEnd.Text = b;
                PrepareGridFromRange(true);
                lblStatus.Text = string.IsNullOrEmpty(local) ? "未检测到网卡" : ("本机 " + local);
            });
            var btnClearHist = MakeBtn("清除曾占用", 760, 10, 100, (s, e) =>
            {
                _everOccupied.Clear();
                SaveHistory();
                RefreshAllVisuals();
                lblStatus.Text = "已清除历史曾占用标记";
            });
            var btnClearConflict = MakeBtn("清除冲突", 870, 10, 90, (s, e) =>
            {
                _identity.Clear();
                SaveConflicts();
                RefreshAllVisuals();
                lblStatus.Text = "已清除 IP 冲突记录";
            });
            var tip = new Label
            {
                Left = 500,
                Top = 14,
                Width = 250,
                ForeColor = Color.FromArgb(120, 125, 135),
                Text = "紫格=同IP出现过不同MAC/名称"
            };

            settingsPanel.Controls.Add(chkArp);
            settingsPanel.Controls.Add(chkIcmp);
            settingsPanel.Controls.Add(chkTcp);
            settingsPanel.Controls.Add(chkDns);
            settingsPanel.Controls.Add(chkTray);
            settingsPanel.Controls.Add(l1);
            settingsPanel.Controls.Add(numTimeout);
            settingsPanel.Controls.Add(l2);
            settingsPanel.Controls.Add(numParallel);
            settingsPanel.Controls.Add(l3);
            settingsPanel.Controls.Add(txtPorts);
            settingsPanel.Controls.Add(btnLan);
            settingsPanel.Controls.Add(btnClearHist);
            settingsPanel.Controls.Add(btnClearConflict);
            settingsPanel.Controls.Add(tip);
        }

        private void ToggleSettings()
        {
            _settingsOpen = !_settingsOpen;
            settingsPanel.Visible = _settingsOpen;
            settingsPanel.Height = _settingsOpen ? 88 : 0;
            btnSettings.Text = _settingsOpen ? "收起" : "设置";
        }

        private static Button MakeBtn(string text, int x, int y, int w, EventHandler click)
        {
            var b = new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = w,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(245, 246, 248),
                ForeColor = Color.FromArgb(40, 45, 55),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(220, 224, 230);
            b.Click += click;
            return b;
        }

        private Control MakeLegend(int x, Color c, string text)
        {
            var p = new Panel { Left = x, Top = 10, Width = 68, Height = 20, BackColor = Color.Transparent };
            p.Paint += (s, e) =>
            {
                using (var br = new SolidBrush(c))
                    e.Graphics.FillRectangle(br, 0, 4, 12, 12);
                using (var font = UiFonts.Create(8.5F))
                using (var br = new SolidBrush(Color.FromArgb(90, 98, 110)))
                    e.Graphics.DrawString(text, font, br, 16, 2);
            };
            return p;
        }

        private void LoadConfigToUi()
        {
            string a, b;
            string local = IpScanner.SuggestLocalRange(out a, out b);
            txtStart.Text = string.IsNullOrWhiteSpace(_cfg.StartIp) ? a : _cfg.StartIp;
            txtEnd.Text = string.IsNullOrWhiteSpace(_cfg.EndIp) ? b : _cfg.EndIp;
            if (string.IsNullOrWhiteSpace(_cfg.StartIp) || _cfg.StartIp == "192.168.1.1")
            {
                txtStart.Text = a;
                txtEnd.Text = b;
            }
            numInterval.Value = Clamp(_cfg.IntervalSeconds, 5, 86400);
            numTimeout.Value = Clamp(_cfg.TimeoutMs, 40, 5000);
            numParallel.Value = Clamp(_cfg.MaxParallel, 1, 256);
            txtPorts.Text = string.IsNullOrWhiteSpace(_cfg.TcpPorts)
                ? "445,80,3389"
                : _cfg.TcpPorts;
            chkIcmp.Checked = _cfg.UseIcmp;
            chkArp.Checked = _cfg.UseArp;
            chkTcp.Checked = _cfg.UseTcp;
            chkDns.Checked = _cfg.UseDns;
            chkTray.Checked = _cfg.MinimizeToTray;
            chkAutoStart.Checked = AutoStart.IsEnabled();
            if (!string.IsNullOrEmpty(local))
                lblStatus.Text = "本机 " + local + " · 打开即扫描";
        }

        private static decimal Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void SaveUiToConfig()
        {
            _cfg.StartIp = txtStart.Text.Trim();
            _cfg.EndIp = txtEnd.Text.Trim();
            _cfg.IntervalSeconds = (int)numInterval.Value;
            _cfg.TimeoutMs = (int)numTimeout.Value;
            _cfg.MaxParallel = (int)numParallel.Value;
            _cfg.TcpPorts = txtPorts.Text.Trim();
            _cfg.UseIcmp = chkIcmp.Checked;
            _cfg.UseArp = chkArp.Checked;
            _cfg.UseTcp = chkTcp.Checked;
            _cfg.UseDns = chkDns.Checked;
            _cfg.MinimizeToTray = chkTray.Checked;
            _cfg.AutoStartScan = chkSchedule.Checked;
        }

        private void PrepareGridFromRange(bool force)
        {
            IPAddress start, end;
            if (!IpScanner.TryParseIp(txtStart.Text, out start) || !IpScanner.TryParseIp(txtEnd.Text, out end))
                return;
            List<IPAddress> range;
            try { range = IpScanner.ExpandRange(start, end); }
            catch { return; }

            var ips = new List<string>();
            foreach (var ip in range) ips.Add(ip.ToString());
            heat.ResetCells(ips);
            foreach (var ip in ips)
            {
                if (HasConflict(ip))
                    heat.UpdateCell(ip, CellVisual.Conflict, BuildConflictTip(ip, null), "");
                else if (_everOccupied.Contains(ip))
                    heat.UpdateCell(ip, CellVisual.WasOccupied, ip + "\n曾占用");
            }
        }

        private void RefreshAllVisuals()
        {
            foreach (var cell in heat.Cells)
            {
                ScanResult r;
                if (_latest.TryGetValue(cell.Ip, out r))
                    ApplyResultVisual(r);
                else if (HasConflict(cell.Ip))
                    heat.UpdateCell(cell.Ip, CellVisual.Conflict, BuildConflictTip(cell.Ip, null), "");
                else if (_everOccupied.Contains(cell.Ip))
                    heat.UpdateCell(cell.Ip, CellVisual.WasOccupied, cell.Ip + "\n曾占用");
                else
                    heat.UpdateCell(cell.Ip, CellVisual.Pending, cell.Ip);
            }
        }

        private void StartScheduledMode()
        {
            _scheduleTimer.Interval = (int)numInterval.Value * 1000;
            _scheduleTimer.Start();
            lblStatus.Text = "定时每 " + numInterval.Value + " 秒";
        }

        private void StopScheduledMode()
        {
            _scheduleTimer.Stop();
            if (!_scanRunning) lblStatus.Text = "定时已关";
        }

        private void ScheduleTimer_Tick(object sender, EventArgs e)
        {
            if (_scanRunning) return;
            _scheduleTimer.Interval = (int)numInterval.Value * 1000;
            BeginScanAsync(false);
        }

        private void Stop_Click(object sender, EventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void Export_Click(object sender, EventArgs e)
        {
            if (heat.Cells == null || heat.Cells.Count == 0)
            {
                MessageBox.Show(this, "当前没有可导出的数据，请先设定网段并扫描。", "导出表格");
                return;
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "导出表格";
                dlg.Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*";
                dlg.DefaultExt = "csv";
                dlg.FileName = "IP占用_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    int n = ExportTable(dlg.FileName);
                    lblStatus.Text = "已导出 " + n + " 行";
                    MessageBox.Show(this, "已导出 " + n + " 条记录到：\n" + dlg.FileName,
                        "导出表格", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "导出失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private int ExportTable(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("IP,状态,名称,MAC,制造商,探测方式,延迟ms,曾见MAC,曾见名称");

            int count = 0;
            foreach (var cell in heat.Cells)
            {
                ScanResult r;
                _latest.TryGetValue(cell.Ip, out r);

                string state;
                if (HasConflict(cell.Ip) || cell.Visual == CellVisual.Conflict)
                    state = "冲突";
                else if (r != null && r.Status == HostStatus.Occupied)
                    state = "占用";
                else if (_everOccupied.Contains(cell.Ip)
                         || cell.Visual == CellVisual.WasOccupied)
                    state = "曾占用";
                else if (r != null)
                    state = "空闲";
                else if (cell.Visual == CellVisual.Free)
                    state = "空闲";
                else if (cell.Visual == CellVisual.Occupied)
                    state = "占用";
                else
                    state = "未扫描";

                string name = r != null ? (r.Hostname ?? "") : (cell.DeviceName ?? "");
                string mac = r != null ? (r.Mac ?? "") : "";
                string vendor = r != null ? (r.Vendor ?? "") : "";
                if (string.IsNullOrEmpty(vendor) && !string.IsNullOrEmpty(mac))
                {
                    try { vendor = OuiLookup.Find(mac) ?? ""; }
                    catch { vendor = ""; }
                }
                string methods = r != null ? (r.Methods ?? "") : "";
                if (methods == "-") methods = "";
                string latency = (r != null && r.LatencyMs >= 0) ? r.LatencyMs.ToString() : "";
                string seenMacs = "", seenNames = "";
                IdentityTrack track;
                if (_identity.TryGetValue(cell.Ip, out track))
                {
                    seenMacs = string.Join(" | ", track.MacDisplays());
                    seenNames = string.Join(" | ", track.NameList());
                }

                sb.Append(CsvEscape(cell.Ip)).Append(',')
                    .Append(CsvEscape(state)).Append(',')
                    .Append(CsvEscape(name)).Append(',')
                    .Append(CsvEscape(mac)).Append(',')
                    .Append(CsvEscape(vendor)).Append(',')
                    .Append(CsvEscape(methods)).Append(',')
                    .Append(CsvEscape(latency)).Append(',')
                    .Append(CsvEscape(seenMacs)).Append(',')
                    .Append(CsvEscape(seenNames))
                    .AppendLine();
                count++;
            }

            // UTF-8 BOM，方便 Excel 直接打开中文
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return count;
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private void BeginScanAsync(bool fromButton)
        {
            if (_scanRunning) return;

            IPAddress start, end;
            if (!IpScanner.TryParseIp(txtStart.Text, out start) || !IpScanner.TryParseIp(txtEnd.Text, out end))
            {
                if (fromButton)
                    MessageBox.Show(this, "请输入有效 IPv4 起止地址。", "提示");
                return;
            }
            if (!chkArp.Checked && !chkIcmp.Checked && !chkTcp.Checked)
            {
                if (fromButton)
                    MessageBox.Show(this, "请至少开启 ARP / ICMP / TCP 之一。", "提示");
                return;
            }

            SaveUiToConfig();
            _cfg.Save();

            List<IPAddress> range;
            try { range = IpScanner.ExpandRange(start, end); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "范围过大");
                return;
            }

            var ips = new List<string>();
            foreach (var ip in range) ips.Add(ip.ToString());
            heat.ResetCells(ips);
            // 扫描前：冲突先标紫，曾占用标黄，其余待扫
            foreach (var ip in ips)
            {
                if (HasConflict(ip))
                    heat.UpdateCell(ip, CellVisual.Conflict, BuildConflictTip(ip, null) + "\n扫描中", "");
                else if (_everOccupied.Contains(ip))
                    heat.UpdateCell(ip, CellVisual.WasOccupied, ip + "\n曾占用 · 扫描中");
            }

            _scanRunning = true;
            btnScan.Enabled = false;
            btnStop.Enabled = true;
            progress.Value = 0;
            progress.Maximum = Math.Max(1, range.Count);
            _latest.Clear();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var cfgCopy = CloneConfig();
            lblStatus.Text = "扫描中 " + range.Count + " 个…";

            _scanner.Progress -= ScannerOnProgress;
            _scanner.Progress += ScannerOnProgress;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<ScanResult> results = null;
                Exception error = null;
                try { results = _scanner.ScanRange(start, end, cfgCopy, token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }

                try
                {
                    BeginInvoke(new Action(() => FinishScan(results, error, token, fromButton)));
                }
                catch (InvalidOperationException)
                {
                    _scanRunning = false;
                }
            });
        }

        private void FinishScan(List<ScanResult> results, Exception error, CancellationToken token, bool fromButton)
        {
            _scanner.Progress -= ScannerOnProgress;
            if (error != null)
            {
                lblStatus.Text = "出错: " + error.Message;
                if (fromButton) MessageBox.Show(this, error.Message, "错误");
            }
            else if (results != null)
            {
                int occ = 0, free = 0, was = 0, conflict = 0;
                int newConflicts = 0;
                foreach (var r in results)
                {
                    _latest[r.Ip] = r;
                    if (ObserveIdentity(r)) newConflicts++;
                    ApplyResultVisual(r);
                    if (HasConflict(r.Ip))
                    {
                        conflict++;
                        if (r.Status == HostStatus.Occupied)
                            _everOccupied.Add(r.Ip);
                    }
                    else if (r.Status == HostStatus.Occupied)
                    {
                        occ++;
                        _everOccupied.Add(r.Ip);
                    }
                    else
                    {
                        free++;
                        if (_everOccupied.Contains(r.Ip)) was++;
                    }
                }
                SaveHistory();
                SaveConflicts();
                lblSummary.Text = "红 " + occ + "  绿 " + (free - was)
                    + "  黄 " + was + "  紫 " + conflict;
                string statusTail = "完成 占用 " + (occ + conflict) + " / " + results.Count
                    + (conflict > 0 ? ("  冲突 " + conflict) : "")
                    + "  " + DateTime.Now.ToString("HH:mm:ss");
                lblStatus.Text = token.IsCancellationRequested ? "已停止" : statusTail;
                if (newConflicts > 0 && !token.IsCancellationRequested)
                    lblStatus.Text = "⚠ 新发现冲突 " + newConflicts + " 个（紫格）  "
                        + DateTime.Now.ToString("HH:mm:ss");
                if (_tray != null && _tray.Visible)
                {
                    string tip = "占用 " + (occ + conflict) + " / " + results.Count;
                    if (newConflicts > 0) tip += " · 新冲突 " + newConflicts;
                    _tray.ShowBalloonTip(2500, "IP 占用图", tip,
                        newConflicts > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
                }
            }
            else
            {
                lblStatus.Text = "已取消";
            }
            _scanRunning = false;
            btnScan.Enabled = true;
            btnStop.Enabled = false;
            progress.Value = progress.Maximum;
        }

        private void ScannerOnProgress(object sender, ScanProgressEventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ScannerOnProgress(sender, e)));
                return;
            }
            progress.Maximum = Math.Max(progress.Maximum, e.Total);
            progress.Value = Math.Min(e.Done, progress.Maximum);
            if (e.Result != null)
            {
                _latest[e.Result.Ip] = e.Result;
                ObserveIdentity(e.Result);
                if (e.Result.Status == HostStatus.Occupied)
                    _everOccupied.Add(e.Result.Ip);
                ApplyResultVisual(e.Result);
            }
            lblStatus.Text = (string.IsNullOrEmpty(e.Phase) ? "扫描" : e.Phase) + " " + e.Done + " / " + e.Total;
        }

        private void ApplyResultVisual(ScanResult r)
        {
            CellVisual vis;
            if (HasConflict(r.Ip))
                vis = CellVisual.Conflict;
            else if (r.Status == HostStatus.Occupied)
                vis = CellVisual.Occupied;
            else if (_everOccupied.Contains(r.Ip))
                vis = CellVisual.WasOccupied;
            else
                vis = CellVisual.Free;

            string tip = vis == CellVisual.Conflict
                ? BuildConflictTip(r.Ip, r)
                : BuildTip(r.Ip, vis, r);
            heat.UpdateCell(r.Ip, vis, tip, r.Hostname ?? "");
        }

        private static string BuildTip(string ip, CellVisual vis, ScanResult r)
        {
            string state = vis == CellVisual.Occupied ? "占用"
                : vis == CellVisual.Conflict ? "IP 冲突"
                : vis == CellVisual.WasOccupied ? "曾占用（当前空闲）"
                : vis == CellVisual.Free ? "空闲" : "未扫描";

            var sb = new System.Text.StringBuilder();
            // 设备名称置顶（与 Advanced IP Scanner「名称」列对应）
            string deviceName = (r != null && !string.IsNullOrEmpty(r.Hostname)) ? r.Hostname : "";
            if (!string.IsNullOrEmpty(deviceName))
                sb.Append("名称: ").Append(deviceName).AppendLine();
            else if (vis == CellVisual.Occupied || vis == CellVisual.Conflict)
                sb.AppendLine("名称: （未获取到）");

            sb.Append("IP: ").Append(ip).AppendLine();
            sb.Append("状态: ").Append(state);
            if (r == null) return sb.ToString();

            if (!string.IsNullOrEmpty(r.Vendor))
                sb.AppendLine().Append("制造商: ").Append(r.Vendor);
            else if (!string.IsNullOrEmpty(r.Mac))
            {
                string v = OuiLookup.Find(r.Mac);
                if (!string.IsNullOrEmpty(v))
                    sb.AppendLine().Append("制造商: ").Append(v);
            }
            if (!string.IsNullOrEmpty(r.Mac))
                sb.AppendLine().Append("MAC: ").Append(r.Mac);
            if (!string.IsNullOrEmpty(r.Methods) && r.Methods != "-")
                sb.AppendLine().Append("方式: ").Append(r.Methods);
            if (r.LatencyMs >= 0)
                sb.AppendLine().Append("延迟: ").Append(r.LatencyMs).Append(" ms");
            return sb.ToString();
        }

        private string BuildConflictTip(string ip, ScanResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("⚠ IP 冲突（多次扫描身份不一致）");
            sb.Append("IP: ").Append(ip);

            IdentityTrack track;
            if (_identity.TryGetValue(ip, out track))
            {
                var macs = track.MacDisplays();
                var names = track.NameList();
                if (macs.Count > 0)
                    sb.AppendLine().Append("曾见 MAC: ").Append(string.Join(" / ", macs));
                if (names.Count > 0)
                    sb.AppendLine().Append("曾见名称: ").Append(string.Join(" / ", names));
                if (track.DetectedAt.HasValue)
                    sb.AppendLine().Append("判定于: ").Append(track.DetectedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            if (r != null)
            {
                if (!string.IsNullOrEmpty(r.Hostname))
                    sb.AppendLine().Append("本次名称: ").Append(r.Hostname);
                if (!string.IsNullOrEmpty(r.Mac))
                    sb.AppendLine().Append("本次 MAC: ").Append(r.Mac);
                if (!string.IsNullOrEmpty(r.Vendor))
                    sb.AppendLine().Append("制造商: ").Append(r.Vendor);
                sb.AppendLine().Append("当前: ")
                    .Append(r.Status == HostStatus.Occupied ? "在线" : "空闲");
            }
            return sb.ToString();
        }

        /// <summary>记录该 IP 历次 MAC/名称；若新形成冲突返回 true。</summary>
        private bool ObserveIdentity(ScanResult r)
        {
            if (r == null || r.Status != HostStatus.Occupied) return false;
            bool hasMac = !string.IsNullOrWhiteSpace(r.Mac);
            // 拒绝把 IP 自身 / 纯 IPv4 字符串当成设备名（曾导致误报冲突）
            string host = NameResolver.AcceptHostName(r.Hostname, r.Ip);
            r.Hostname = host ?? "";
            bool hasName = !string.IsNullOrWhiteSpace(host);
            if (!hasMac && !hasName) return false;

            IdentityTrack track;
            if (!_identity.TryGetValue(r.Ip, out track))
            {
                track = new IdentityTrack();
                _identity[r.Ip] = track;
            }

            bool was = track.IsConflict;
            if (hasMac) track.AddMac(r.Mac);
            if (hasName) track.AddName(host, r.Ip);
            if (track.IsConflict && !was)
            {
                track.DetectedAt = DateTime.Now;
                return true;
            }
            return false;
        }

        private bool HasConflict(string ip)
        {
            IdentityTrack track;
            return _identity.TryGetValue(ip, out track) && track.IsConflict;
        }

        private AppConfig CloneConfig()
        {
            return new AppConfig
            {
                UseIcmp = _cfg.UseIcmp,
                UseArp = _cfg.UseArp,
                UseTcp = _cfg.UseTcp,
                UseDns = true, // 阶段3 反查主机名（不参与占用判定）
                TcpPorts = _cfg.TcpPorts,
                TimeoutMs = _cfg.TimeoutMs,
                MaxParallel = _cfg.MaxParallel
            };
        }

        private static string HistoryPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LanIpWatcher");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "history.json");
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                using (var fs = File.OpenRead(HistoryPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(string[]));
                    var arr = ser.ReadObject(fs) as string[];
                    if (arr == null) return;
                    foreach (var ip in arr) if (!string.IsNullOrEmpty(ip)) _everOccupied.Add(ip);
                }
            }
            catch { }
        }

        private void SaveHistory()
        {
            try
            {
                var arr = new string[_everOccupied.Count];
                _everOccupied.CopyTo(arr);
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(string[]));
                    ser.WriteObject(ms, arr);
                    File.WriteAllText(HistoryPath, Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8);
                }
            }
            catch { }
        }

        private void InitTray()
        {
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            _tray = new NotifyIcon
            {
                Icon = appIcon ?? SystemIcons.Application,
                Text = "IP 占用图",
                Visible = false
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示", null, (s, e) => RestoreFromTray());
            menu.Items.Add("扫描", null, (s, e) => { RestoreFromTray(); BeginScanAsync(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) =>
            {
                _exitRequested = true;
                _tray.Visible = false;
                Close();
            });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            _tray.Visible = false;
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized && chkTray.Checked)
            {
                Hide();
                _tray.Visible = true;
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_exitRequested && chkTray.Checked && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.Visible = true;
                return;
            }
            SaveUiToConfig();
            _cfg.Save();
            SaveHistory();
            SaveConflicts();
            if (_cts != null) _cts.Cancel();
            _scheduleTimer.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }

        private static string ConflictsPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LanIpWatcher");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "conflicts.json");
            }
        }

        private void LoadConflicts()
        {
            try
            {
                if (!File.Exists(ConflictsPath)) return;
                using (var fs = File.OpenRead(ConflictsPath))
                {
                    var ser = new DataContractJsonSerializer(typeof(ConflictDto[]));
                    var arr = ser.ReadObject(fs) as ConflictDto[];
                    if (arr == null) return;
                    foreach (var dto in arr)
                    {
                        if (dto == null || string.IsNullOrEmpty(dto.Ip)) continue;
                        var track = new IdentityTrack();
                        if (dto.Macs != null)
                            foreach (var m in dto.Macs)
                                if (!string.IsNullOrWhiteSpace(m)) track.AddMac(m);
                        if (dto.Names != null)
                            foreach (var n in dto.Names)
                                if (!string.IsNullOrWhiteSpace(n)) track.AddName(n, dto.Ip);
                        DateTime dt;
                        if (!string.IsNullOrEmpty(dto.DetectedAt)
                            && DateTime.TryParse(dto.DetectedAt, out dt))
                            track.DetectedAt = dt;
                        // 旧数据可能把 IP 写进名称导致误判冲突；清洗后不再冲突则丢掉判定时间
                        if (!track.IsConflict) track.DetectedAt = null;
                        if (track.MacCount > 0 || track.NameCount > 0)
                            _identity[dto.Ip] = track;
                    }
                }
                // 回写清洗后的记录，去掉历史误报
                SaveConflicts();
            }
            catch { }
        }

        private void SaveConflicts()
        {
            try
            {
                var list = new List<ConflictDto>();
                foreach (var kv in _identity)
                {
                    if (kv.Value == null) continue;
                    // 只持久化已形成冲突或至少有身份线索的记录
                    if (!kv.Value.IsConflict && kv.Value.MacCount < 1 && kv.Value.NameCount < 1)
                        continue;
                    list.Add(new ConflictDto
                    {
                        Ip = kv.Key,
                        Macs = kv.Value.MacDisplays().ToArray(),
                        Names = kv.Value.NameList().ToArray(),
                        DetectedAt = kv.Value.DetectedAt.HasValue
                            ? kv.Value.DetectedAt.Value.ToString("o")
                            : null
                    });
                }
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(ConflictDto[]));
                    ser.WriteObject(ms, list.ToArray());
                    File.WriteAllText(ConflictsPath, Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8);
                }
            }
            catch { }
        }

        [DataContract]
        private class ConflictDto
        {
            [DataMember] public string Ip { get; set; }
            [DataMember] public string[] Macs { get; set; }
            [DataMember] public string[] Names { get; set; }
            [DataMember] public string DetectedAt { get; set; }
        }

        /// <summary>跨次扫描累计同一 IP 出现过的 MAC / 主机名。</summary>
        private sealed class IdentityTrack
        {
            private readonly Dictionary<string, string> _macs =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _names =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public DateTime? DetectedAt { get; set; }

            public int MacCount { get { return _macs.Count; } }
            public int NameCount { get { return _names.Count; } }

            /// <summary>
            /// MAC 出现 ≥2 个为真冲突；
            /// 仅有 1 个 MAC 时，名称波动（DNS/NB 不稳定）不判冲突；
            /// 尚无 MAC 时，才用 ≥2 个有效名称辅助判定。
            /// </summary>
            public bool IsConflict
            {
                get
                {
                    if (_macs.Count >= 2) return true;
                    if (_macs.Count == 1) return false;
                    return _names.Count >= 2;
                }
            }

            public void AddMac(string mac)
            {
                string norm = NormalizeMac(mac);
                if (string.IsNullOrEmpty(norm) || norm.Length < 12) return;
                if (!_macs.ContainsKey(norm))
                    _macs[norm] = FormatMac(norm);
            }

            public void AddName(string name, string ip)
            {
                string n = NameResolver.AcceptHostName(name, ip);
                if (string.IsNullOrEmpty(n)) return;
                _names.Add(n);
            }

            public List<string> MacDisplays()
            {
                return new List<string>(_macs.Values);
            }

            public List<string> NameList()
            {
                return new List<string>(_names);
            }

            private static string NormalizeMac(string mac)
            {
                if (string.IsNullOrEmpty(mac)) return "";
                var sb = new StringBuilder(12);
                foreach (char c in mac)
                {
                    if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                        sb.Append(char.ToUpperInvariant(c));
                }
                return sb.ToString();
            }

            private static string FormatMac(string norm12)
            {
                if (norm12 == null || norm12.Length < 12) return norm12 ?? "";
                return string.Format("{0}-{1}-{2}-{3}-{4}-{5}",
                    norm12.Substring(0, 2),
                    norm12.Substring(2, 2),
                    norm12.Substring(4, 2),
                    norm12.Substring(6, 2),
                    norm12.Substring(8, 2),
                    norm12.Substring(10, 2));
            }
        }
    }
}
