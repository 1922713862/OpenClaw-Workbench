using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace JFWorkbench;

// Offline visual review only. No gateway actions, browser profiles, or desktop capture.
public partial class MainForm
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    internal void PrepareUiReview()
    {
        if (!demo || p1) throw new InvalidOperationException("Visual review requires offline demo.");
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-16000, -16000);
        ShowInTaskbar = false;
        if (tray is not null) tray.Visible = false;
        Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(800);
                await CaptureUiReviewAsync();
                Environment.ExitCode = 0;
            }
            catch
            {
                Environment.ExitCode = 1;
            }
            finally { ExitApp(); }
        };
    }

    private async Task CaptureUiReviewAsync()
    {
        string[] args = Environment.GetCommandLineArgs();
        string sizeName = ArgumentValue(args, "--review-size") ?? "1440x900";
        string state = ArgumentValue(args, "--review-state") ?? "default";
        int targetDpi = int.TryParse(ArgumentValue(args, "--review-dpi"), out int dpi) ? dpi : DeviceDpi;
        if (targetDpi is not (96 or 144 or 192)) throw new ArgumentOutOfRangeException(nameof(targetDpi));
        if (state is not ("default" or "expanded" or "status" or "logs" or "focus" or "keyboard"))
            throw new ArgumentOutOfRangeException(nameof(state));
        int nativeDpi = DeviceDpi;
        uint windowDpi = GetDpiForWindow(Handle);
        float nativeScale = nativeDpi / 96F;
        bool equivalent = targetDpi != nativeDpi;
        rpcTimer.Stop();
        SelectPage("workbench");
        if (sizeName == "maximized") WindowState = FormWindowState.Maximized;
        else
        {
            string[] dimensions = sizeName.Split('x');
            if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out int width) ||
                !int.TryParse(dimensions[1], out int height) || width is < 1100 or > 2000 || height is < 720 or > 1200)
                throw new ArgumentOutOfRangeException(nameof(sizeName));
            ClientSize = new Size((int)Math.Round(width * nativeScale), (int)Math.Round(height * nativeScale));
        }
        SetRailExpanded(state == "expanded");
        if (state == "status" || state == "logs") SelectPage(state);
        bool keyboardRoundTrip = false;
        if (state == "keyboard")
        {
            Message key = Message.Create(Handle, 0x100, IntPtr.Zero, IntPtr.Zero);
            bool enter = ProcessCmdKey(ref key, Keys.F11);
            bool onlyWorkbench = !sidebar.Visible && !header.Visible && !statusStrip.Visible && !drawer.Visible;
            bool leave = ProcessCmdKey(ref key, Keys.Control | Keys.Shift | Keys.F);
            keyboardRoundTrip = enter && leave && onlyWorkbench && sidebar.Visible && header.Visible && statusStrip.Visible;
            if (!keyboardRoundTrip) throw new InvalidOperationException("Focus shortcut round trip failed.");
        }
        if (state == "focus") SetFocusMode(true);
        PerformLayout();
        await Task.Delay(250);

        var nominalFonts = new { title = headerTitle.Font.SizeInPoints, value = liveValue.Font.SizeInPoints,
            body = startButton.Font.SizeInPoints, secondary = checkLabel.Font.SizeInPoints };
        Size nativeClient = ClientSize;
        Size nativeOuter = Size;
        // These two extra previews are explicitly NOT system-DPI tests. Keep uiScale=1;
        // lay out at the real monitor DPI first, then render the same control tree at
        // the requested pixel density. No claim of PerMonitorV2 reflow validation.
        if (equivalent)
        {
            float ratio = targetDpi / (float)nativeDpi;
            uiReviewDpiOverride = targetDpi;
            ShellButton.ReviewDpiOverride = targetDpi;
            var controls = DescendantsForReview(this).Select(c =>
                (Control: c, Bounds: c.Bounds, Font: c.Font)).ToArray();
            SuspendLayout();
            foreach (var item in controls) item.Control.SuspendLayout();
            MinimumSize = Size.Empty;
            WindowState = FormWindowState.Normal;
            ClientSize = ScaleSize(nativeClient, ratio);
            foreach (var item in controls)
            {
                item.Control.AutoSize = false;
                item.Control.MinimumSize = Size.Empty;
                item.Control.MaximumSize = Size.Empty;
                item.Control.Font = new Font(item.Font.FontFamily, item.Font.SizeInPoints * ratio,
                    item.Font.Style, GraphicsUnit.Point);
                item.Control.Bounds = new Rectangle(new Point((int)Math.Round(item.Bounds.X * ratio), (int)Math.Round(item.Bounds.Y * ratio)),
                    ScaleSize(item.Bounds.Size, ratio));
            }
        }

        Control viewportControl = web is not null ? web : webHost;
        Rectangle viewport = VisibleRectangleForReview(viewportControl);
        Rectangle actualClient = ClientRectangle;
        long visiblePixels = (long)viewport.Width * viewport.Height;
        if (drawer.Visible)
        {
            Rectangle covered = Rectangle.Intersect(viewport, VisibleRectangleForReview(drawer));
            visiblePixels -= (long)covered.Width * covered.Height;
        }
        long clientPixels = (long)actualClient.Width * actualClient.Height;
        double share = clientPixels > 0 ? visiblePixels / (double)clientPixels : 0;
        string kind = equivalent ? "equivalent-DPI-render-NOT-system-test" : "native-monitor-DPI";
        string directory = Path.GetDirectoryName(screenshotPath)!;
        Directory.CreateDirectory(directory);
        using var raw = new Bitmap(Width, Height);
        CaptureOwnWindow(raw);
        using var annotated = new Bitmap(raw.Width, raw.Height + 64);
        using (Graphics graphics = Graphics.FromImage(annotated))
        {
            graphics.Clear(Color.FromArgb(246, 247, 246));
            graphics.DrawImageUnscaled(raw, 0, 0);
            using var caption = new Font("Microsoft YaHei UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(50, 58, 55));
            string first = $"{targetDpi / 96.0:P0} | {sizeName} | {state} | viewport {viewport.Width} x {viewport.Height}px | visible share {share:P2}";
            string second = $"{kind}; monitor/window DPI {nativeDpi}/{windowDpi}; uiScale {uiScale:0.0}; offline; no gateway / browser";
            graphics.DrawString(first, caption, brush, 16, raw.Height + 5);
            graphics.DrawString(second, caption, brush, 16, raw.Height + 33);
        }
        annotated.Save(screenshotPath, ImageFormat.Png);
        var evidence = new
        {
            generatedAt = DateTimeOffset.Now, mode = "offline-demo", gatewayContacted = false,
            browserOpened = false, productionProfileRead = false, evidenceKind = kind, captureMethod = "PrintWindow-own-HWND",
            actualMonitorDpi = nativeDpi, actualWindowDpi = windowDpi, targetRenderDpi = targetDpi,
            realSystemDpiTest = !equivalent, uiScale, requestedSizeDip = sizeName, state,
            nativeClientPixels = nativeClient, nativeOuterPixels = nativeOuter,
            renderedClientPixels = ClientSize, renderedOuterPixels = Size,
            clientDip = new { width = ClientSize.Width * 96.0 / targetDpi, height = ClientSize.Height * 96.0 / targetDpi },
            visibleViewportPixels = viewport, visibleViewportArea = visiblePixels, clientArea = clientPixels,
            visibleShare = share, visiblePercent = Math.Round(share * 100, 3),
            viewportDip = new { width = viewport.Width * 96.0 / targetDpi, height = viewport.Height * 96.0 / targetDpi },
            defaultSharePass = share >= .70,
            defaultViewportPass = viewport.Width * 96.0 / targetDpi >= 1000 && viewport.Height * 96.0 / targetDpi >= 700,
            railWidthDip = sidebar.Width * 96.0 / targetDpi,
            headerHeightDip = header.Height * 96.0 / targetDpi,
            drawerWidthDip = drawer.Width * 96.0 / targetDpi,
            sidebarVisible = sidebar.Visible, headerVisible = header.Visible, drawerVisible = drawer.Visible,
            nominalFontsPt = nominalFonts, keyboardRoundTrip,
            screenshot = Path.GetFileName(screenshotPath), success = true
        };
        await File.WriteAllTextAsync(Path.ChangeExtension(screenshotPath, ".json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static IEnumerable<Control> DescendantsForReview(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in DescendantsForReview(child)) yield return descendant;
        }
    }

    private void CaptureOwnWindow(Bitmap target)
    {
        bool printed;
        using (Graphics graphics = Graphics.FromImage(target))
        {
            IntPtr hdc = graphics.GetHdc();
            try { printed = PrintWindow(Handle, hdc, 2); }
            finally { graphics.ReleaseHdc(hdc); }
        }
        if (!printed) throw new InvalidOperationException("Own-window capture failed.");
    }

    private async Task WriteP1UiMeasurementAsync(Bitmap captured, string directory)
    {
        if (!p1 || web is null) return;
        Rectangle viewport = VisibleRectangleForReview(web);
        long area = (long)viewport.Width * viewport.Height;
        if (drawer.Visible)
        {
            Rectangle covered = Rectangle.Intersect(viewport, VisibleRectangleForReview(drawer));
            area -= (long)covered.Width * covered.Height;
        }
        double share = area / ((double)ClientSize.Width * ClientSize.Height);
        var evidence = new
        {
            generatedAt = DateTimeOffset.Now, mode = "p1", viewportKind = "actual-WebView2",
            actualMonitorDpi = DeviceDpi, actualWindowDpi = GetDpiForWindow(Handle), uiScale,
            clientPixels = ClientSize, viewportPixels = viewport,
            clientDip = new { width = ClientSize.Width * 96.0 / DeviceDpi, height = ClientSize.Height * 96.0 / DeviceDpi },
            viewportDip = new { width = viewport.Width * 96.0 / DeviceDpi, height = viewport.Height * 96.0 / DeviceDpi },
            visiblePercent = Math.Round(share * 100, 3), sharePass = share >= .70,
            sidebarWidthDip = sidebar.Width * 96.0 / DeviceDpi, headerHeightDip = header.Height * 96.0 / DeviceDpi,
            drawerVisible = drawer.Visible, gatewayLifecycleAction = "none", credentialsExported = false
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "p1-ui-measurement.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        using var annotated = new Bitmap(captured.Width, captured.Height + 48);
        using (Graphics graphics = Graphics.FromImage(annotated))
        {
            graphics.Clear(Canvas);
            graphics.DrawImageUnscaled(captured, 0, 0);
            using var font = new Font("Microsoft YaHei UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Ink);
            graphics.DrawString($"P1 actual Control UI | DPI {DeviceDpi} | WebView {viewport.Width} x {viewport.Height}px | visible share {share:P2} | lifecycle untouched",
                font, brush, 16, captured.Height + 12);
        }
        annotated.Save(screenshotPath, ImageFormat.Png);
    }

    private Rectangle VisibleRectangleForReview(Control target)
    {
        if (!target.Visible) return Rectangle.Empty;
        Rectangle result = RectangleToClient(target.RectangleToScreen(target.ClientRectangle));
        for (Control? ancestor = target.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.Visible) return Rectangle.Empty;
            result = Rectangle.Intersect(result, RectangleToClient(ancestor.RectangleToScreen(ancestor.ClientRectangle)));
        }
        return Rectangle.Intersect(result, ClientRectangle);
    }
}
