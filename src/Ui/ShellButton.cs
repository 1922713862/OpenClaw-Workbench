using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Windows.Forms;

namespace JFWorkbench;

/// <summary>One small native button surface for the rail and its toolbar.</summary>
internal sealed class ShellButton : Button
{
    internal static int? ReviewDpiOverride;
    [DefaultValue("")] public string IconKey { get; set; } = "";
    [DefaultValue(true)] public bool ShowCaption { get; set; } = true;
    [DefaultValue(false)] public bool Selected { get; set; }
    [DefaultValue(false)] public bool FilledAccent { get; set; }
    [DefaultValue(true)] public bool DrawOutline { get; set; } = true;
    private bool hovered;

    public ShellButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(System.EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(System.EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(System.EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(System.EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(System.EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = (ReviewDpiOverride ?? DeviceDpi) / 96F;
        var accent = Color.FromArgb(33, 106, 85);
        var foreground = Enabled ? FilledAccent ? Color.White : Selected ? accent : ForeColor : Color.FromArgb(158, 166, 160);
        var background = FilledAccent ? Enabled ? hovered ? Color.FromArgb(42, 119, 95) : accent : Color.FromArgb(230, 235, 229)
            : Selected ? Color.FromArgb(235, 242, 237) : hovered && Enabled ? Color.FromArgb(238, 241, 236) : BackColor;
        graphics.Clear(Parent?.BackColor ?? BackColor);
        using var shape = Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 12 * scale);
        using (var brush = new SolidBrush(background)) graphics.FillPath(brush, shape);
        if (DrawOutline || Focused)
        {
            using var outline = new Pen(Focused ? accent : Color.FromArgb(230, 232, 228), Math.Max(1F, scale));
            graphics.DrawPath(outline, shape);
        }
        bool icon = !string.IsNullOrEmpty(IconKey);
        float iconX = ShowCaption && icon ? 24 * scale : Width / 2F;
        if (icon) DrawIcon(graphics, IconKey, new PointF(iconX, Height / 2F), foreground, scale);
        if (ShowCaption)
        {
            var textBounds = icon ? new Rectangle((int)(46 * scale), 0, Math.Max(0, Width - (int)(54 * scale)), Height) : ClientRectangle;
            TextRenderer.DrawText(graphics, Text, Font, textBounds, foreground,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding
                | (icon ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        }
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = System.Math.Min(radius * 2, System.Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter < 1) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawIcon(Graphics graphics, string key, PointF center, Color color, float scale)
    {
        using var pen = new Pen(color, 1.5F * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        float x = center.X - 9 * scale, y = center.Y - 9 * scale;
        PointF P(float px, float py) => new(x + px * scale, y + py * scale);
        RectangleF R(float px, float py, float w, float h) => new(x + px * scale, y + py * scale, w * scale, h * scale);
        switch (key)
        {
            case "start": graphics.FillPolygon(brush, new[] { P(5, 2), P(16, 9), P(5, 16) }); break;
            case "stop": graphics.FillRectangle(brush, R(4, 4, 11, 11)); break;
            case "refresh":
                // 心跳/探针：与"重启"彻底区分
                graphics.DrawLines(pen, new[] { P(1, 9), P(4.5f, 9), P(7, 3.5f), P(11, 14.5f), P(13.5f, 9), P(17, 9) });
                break;
            case "reconnect":
                // 链环：两个斜置胶囊互扣
                {
                    var state = graphics.Save();
                    graphics.TranslateTransform(center.X, center.Y);
                    graphics.RotateTransform(-45);
                    using (var c1 = Rounded(new RectangleF(-8.5f * scale, -3f * scale, 10.5f * scale, 6f * scale), 3f * scale))
                        graphics.DrawPath(pen, c1);
                    using (var c2 = Rounded(new RectangleF(-2f * scale, -3f * scale, 10.5f * scale, 6f * scale), 3f * scale))
                        graphics.DrawPath(pen, c2);
                    graphics.Restore(state);
                }
                break;
            case "restart":
                // 循环箭头：圆弧 + 实心三角箭头（不再是分离的折线）
                graphics.DrawArc(pen, R(2, 2, 14, 14), -40, 280);
                graphics.FillPolygon(brush, new[] { P(14.2f, 0.8f), P(17.4f, 3.6f), P(13.4f, 5.4f) });
                break;
            case "expand":
                for (int i = 0; i < 3; i++) graphics.DrawLine(pen, P(2, 4 + i * 5), P(16, 4 + i * 5));
                break;
            case "collapse":
                graphics.DrawLines(pen, new[] { P(11, 3), P(5, 9), P(11, 15) });
                break;
            case "close":
                graphics.DrawLine(pen, P(4, 4), P(14, 14)); graphics.DrawLine(pen, P(14, 4), P(4, 14));
                break;
            case "focus":
                graphics.DrawLines(pen, new[] { P(2, 7), P(2, 2), P(7, 2) });
                graphics.DrawLines(pen, new[] { P(11, 2), P(16, 2), P(16, 7) });
                graphics.DrawLines(pen, new[] { P(16, 11), P(16, 16), P(11, 16) });
                graphics.DrawLines(pen, new[] { P(7, 16), P(2, 16), P(2, 11) });
                break;
            case "bolt":
                graphics.FillPolygon(brush, new[] { P(10, 0), P(3, 10), P(8, 10), P(7, 18), P(16, 7), P(10, 7) });
                break;
            case "browser":
                graphics.DrawEllipse(pen, R(1, 1, 16, 16)); graphics.DrawEllipse(pen, R(5, 1, 8, 16));
                graphics.DrawLine(pen, P(1, 9), P(17, 9));
                break;
            case "status":
                graphics.DrawLines(pen, new[] { P(1, 10), P(4, 10), P(7, 3), P(11, 15), P(14, 8), P(17, 8) });
                break;
            case "logs":
                for (int i = 0; i < 3; i++) { graphics.FillEllipse(brush, R(1, 3 + i * 5, 2, 2)); graphics.DrawLine(pen, P(6, 4 + i * 5), P(16, 4 + i * 5)); }
                break;
            default:
                graphics.DrawRectangle(pen, x + scale, y + 2 * scale, 16 * scale, 14 * scale);
                graphics.DrawLine(pen, P(1, 7), P(17, 7)); graphics.DrawLine(pen, P(6, 7), P(6, 16));
                break;
        }
    }
}
