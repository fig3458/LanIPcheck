using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LanIpWatcher
{
    public enum CellVisual
    {
        Pending,
        Free,
        Occupied,
        WasOccupied,
        Conflict
    }

    public class HeatCell
    {
        public string Ip { get; set; }
        public string Label { get; set; }
        public string DeviceName { get; set; }
        public CellVisual Visual { get; set; }
        public string Tip { get; set; }
        public Rectangle Bounds { get; set; }
    }

    public class HeatMapControl : Control
    {
        private readonly List<HeatCell> _cells = new List<HeatCell>();
        private readonly Dictionary<string, HeatCell> _byIp =
            new Dictionary<string, HeatCell>(StringComparer.OrdinalIgnoreCase);
        private readonly ToolTip _tip = new ToolTip();
        private int _hoverIndex = -1;
        private string _lastTipIp = "";

        public static readonly Color ColorOccupied = Color.FromArgb(220, 68, 68);
        public static readonly Color ColorFree = Color.FromArgb(56, 168, 92);
        public static readonly Color ColorWas = Color.FromArgb(230, 180, 40);
        public static readonly Color ColorConflict = Color.FromArgb(156, 39, 176);
        public static readonly Color ColorPending = Color.FromArgb(210, 214, 220);

        public event EventHandler<HeatCell> CellClick;
        public event EventHandler<HeatCell> CellHover;

        public HeatMapControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(248, 249, 251);
            _tip.ShowAlways = true;
            _tip.AutoPopDelay = 15000;
            _tip.InitialDelay = 180;
            _tip.ReshowDelay = 80;
        }

        public IList<HeatCell> Cells { get { return _cells; } }

        public void ResetCells(IEnumerable<string> ips)
        {
            _cells.Clear();
            _byIp.Clear();
            _hoverIndex = -1;
            _lastTipIp = "";
            foreach (var ip in ips)
            {
                string label = ip;
                int dot = ip.LastIndexOf('.');
                if (dot >= 0 && dot < ip.Length - 1) label = ip.Substring(dot + 1);
                var cell = new HeatCell
                {
                    Ip = ip,
                    Label = label,
                    Visual = CellVisual.Pending,
                    Tip = ip
                };
                _cells.Add(cell);
                _byIp[ip] = cell;
            }
            LayoutCells();
            Invalidate();
        }

        public void UpdateCell(string ip, CellVisual visual, string tip)
        {
            UpdateCell(ip, visual, tip, null);
        }

        public void UpdateCell(string ip, CellVisual visual, string tip, string deviceName)
        {
            HeatCell cell;
            if (!_byIp.TryGetValue(ip, out cell)) return;
            cell.Visual = visual;
            cell.Tip = tip ?? ip;
            if (deviceName != null) cell.DeviceName = deviceName;
            Invalidate(cell.Bounds);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCells();
            Invalidate();
        }

        private void LayoutCells()
        {
            int n = _cells.Count;
            if (n == 0) return;

            int pad = 10;
            int gap = 3;
            int w = Math.Max(1, ClientSize.Width - pad * 2);
            int h = Math.Max(1, ClientSize.Height - pad * 2);

            int cols = (int)Math.Ceiling(Math.Sqrt(n * (w / (double)Math.Max(1, h))));
            cols = Math.Max(1, Math.Min(n, cols));
            int rows = (int)Math.Ceiling(n / (double)cols);

            int cellW = Math.Max(12, (w - gap * (cols - 1)) / cols);
            int cellH = Math.Max(12, (h - gap * (rows - 1)) / rows);

            // 尽量接近正方形
            int side = Math.Min(cellW, cellH);
            cellW = side;
            cellH = side;

            int gridW = cols * cellW + (cols - 1) * gap;
            int gridH = rows * cellH + (rows - 1) * gap;
            int ox = pad + Math.Max(0, (w - gridW) / 2);
            int oy = pad + Math.Max(0, (h - gridH) / 2);

            for (int i = 0; i < n; i++)
            {
                int r = i / cols;
                int c = i % cols;
                _cells[i].Bounds = new Rectangle(
                    ox + c * (cellW + gap),
                    oy + r * (cellH + gap),
                    cellW,
                    cellH);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            if (_cells.Count == 0)
            {
                using (var font = UiFonts.Create(11F))
                using (var br = new SolidBrush(Color.FromArgb(140, 145, 155)))
                {
                    string msg = "点击「扫描」开始";
                    var sz = g.MeasureString(msg, font);
                    g.DrawString(msg, font, br,
                        (ClientSize.Width - sz.Width) / 2f,
                        (ClientSize.Height - sz.Height) / 2f);
                }
                return;
            }

            for (int i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                if (!e.ClipRectangle.IntersectsWith(Rectangle.Inflate(cell.Bounds, 1, 1)))
                    continue;

                Color fill = ColorPending;
                if (cell.Visual == CellVisual.Conflict) fill = ColorConflict;
                else if (cell.Visual == CellVisual.Occupied) fill = ColorOccupied;
                else if (cell.Visual == CellVisual.Free) fill = ColorFree;
                else if (cell.Visual == CellVisual.WasOccupied) fill = ColorWas;

                bool hover = i == _hoverIndex;
                Rectangle rc = cell.Bounds;
                int radius = Math.Max(2, Math.Min(rc.Width, rc.Height) / 6);

                using (var path = RoundRect(rc, radius))
                using (var br = new SolidBrush(fill))
                {
                    g.FillPath(br, path);
                    if (hover)
                    {
                        using (var pen = new Pen(Color.FromArgb(40, 40, 45), 2f))
                            g.DrawPath(pen, path);
                    }
                }

                // 格子够大：有设备名显示名称，否则显示 IP 末段
                if (rc.Width >= 22 && rc.Height >= 18)
                {
                    float fs = Math.Max(6.5f, Math.Min(10f, rc.Height * 0.34f));
                    string text = cell.Label;
                    if (!string.IsNullOrEmpty(cell.DeviceName) && rc.Width >= 36)
                    {
                        text = cell.DeviceName;
                        if (text.Length > 8 && rc.Width < 70)
                            text = text.Substring(0, 7) + "…";
                        else if (text.Length > 12)
                            text = text.Substring(0, 11) + "…";
                        fs = Math.Max(6f, Math.Min(9f, rc.Width * 0.14f));
                    }
                    using (var font = UiFonts.Create(fs, FontStyle.Bold))
                    using (var br = new SolidBrush(Color.FromArgb(245, 245, 245)))
                    {
                        var sf = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center,
                            FormatFlags = StringFormatFlags.NoWrap,
                            Trimming = StringTrimming.EllipsisCharacter
                        };
                        g.DrawString(text, font, br, rc, sf);
                    }
                }
            }
        }

        private static GraphicsPath RoundRect(Rectangle rc, int r)
        {
            int d = r * 2;
            var path = new GraphicsPath();
            if (d <= 0 || d > rc.Width || d > rc.Height)
            {
                path.AddRectangle(rc);
                return path;
            }
            path.AddArc(rc.X, rc.Y, d, d, 180, 90);
            path.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            path.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
            path.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.Location);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                Invalidate();
            }
            if (idx >= 0)
            {
                var cell = _cells[idx];
                if (_lastTipIp != cell.Ip)
                {
                    _lastTipIp = cell.Ip;
                    _tip.SetToolTip(this, cell.Tip);
                    if (CellHover != null) CellHover(this, cell);
                }
            }
            else
            {
                if (_lastTipIp != "")
                {
                    _lastTipIp = "";
                    _tip.SetToolTip(this, "");
                    if (CellHover != null) CellHover(this, null);
                }
            }
            Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            _lastTipIp = "";
            _tip.SetToolTip(this, "");
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int idx = HitTest(e.Location);
            if (idx >= 0 && CellClick != null)
                CellClick(this, _cells[idx]);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _cells.Count; i++)
                if (_cells[i].Bounds.Contains(p)) return i;
            return -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
