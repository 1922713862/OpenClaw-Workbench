using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace JFWorkbench
{
    static class TrayIconFactory
    {
        public static Icon Make(int size, bool rounded)
            => Draw(size, null);

        public static Icon MakeStatus(int size, string state)
        {
            Color color = state.ToLowerInvariant() switch
            {
                "ready" or "green" or "online" => Color.FromArgb(31, 145, 89),
                "starting" or "degraded" or "yellow" or "slow" => Color.FromArgb(225, 165, 37),
                "paused" or "fault" or "error" or "red" or "unknown" => Color.FromArgb(201, 65, 65),
                _ => Color.FromArgb(139, 147, 150)
            };
            return Draw(size, color);
        }

        private static Icon Draw(int size, Color? statusColor)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var rect = new Rectangle(0, 0, size - 1, size - 1);
                using (var path = Rounded(rect, Math.Max(2, size / 5)))
                using (var bg = new SolidBrush(Color.FromArgb(255, 15, 23, 42)))
                    g.FillPath(bg, path);
                float s = size / 64f;
                var bolt = new PointF[]
                {
                    new PointF(38 * s, 6 * s),
                    new PointF(16 * s, 36 * s),
                    new PointF(29 * s, 36 * s),
                    new PointF(25 * s, 58 * s),
                    new PointF(48 * s, 26 * s),
                    new PointF(34 * s, 26 * s)
                };
                using (var b = new SolidBrush(Color.FromArgb(255, 250, 204, 21)))
                    g.FillPolygon(b, bolt);
                if (statusColor.HasValue)
                {
                    int diameter = Math.Max(8, (int)(size * 0.44));
                    var badge = new Rectangle(size - diameter - 1, size - diameter - 1, diameter, diameter);
                    using var border = new Pen(Color.White, Math.Max(1F, size / 18F));
                    using var fill = new SolidBrush(statusColor.Value);
                    g.FillEllipse(fill, badge);
                    g.DrawEllipse(border, badge);
                }
            }
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var tmp = Icon.FromHandle(h))
                {
                    return (Icon)tmp.Clone();
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(h);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
