using BatteryHub.Core;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BatteryHub;

/// <summary>
/// Панель устройств (открывается кликом по виджету): модели, проценты, оценка времени.
/// Простая и тёмная, строки интерфейса — на выбранном языке.
/// </summary>
public sealed class FlyoutForm : Form
{
    private IReadOnlyList<DeviceStatus> _items = Array.Empty<DeviceStatus>();
    private Rectangle _settingsLinkRect = Rectangle.Empty;
    private Rectangle _githubRect = Rectangle.Empty;
    private Rectangle _closeRect = Rectangle.Empty;
    private DateTime _refreshRequestedUtc = DateTime.MinValue;
    // одноразовый таймер: убирает надпись «Запрашиваем свежие данные…» через 3 с.
    // Постоянного ежесекундного обновления панели больше нет — она перерисовывается
    // только при реальном обновлении данных.
    private readonly System.Windows.Forms.Timer _refreshResetTimer;

    private const int CardHeight = 112;
    private const int PaddingSide = 14;
    private const int PaddingTop = 16;
    private const int HeaderHeight = 54;
    private const int FooterHeight = 36;
    private const int CardGap = 10;
    private const int WidthPx = 400;
    /// <summary>Максимальная высота панели: при большем числе устройств — прокрутка.</summary>
    private const int MaxHeight = 620;

    private int _contentHeight;
    private bool _scrollable;

    public FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 25, 28);

        _refreshResetTimer = new System.Windows.Forms.Timer { Interval = 3000, Enabled = false };
        _refreshResetTimer.Tick += (_, _) => Invalidate();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _refreshResetTimer.Dispose();
        base.OnHandleDestroyed(e);
    }

    public void UpdateItems(IReadOnlyList<DeviceStatus> items)
    {
        _items = items;
        var rows = Math.Max(items.Count, 1);
        _contentHeight = PaddingTop + HeaderHeight + rows * CardHeight + (rows - 1) * CardGap + FooterHeight;
        // много устройств — панель фиксируется по высоте и прокручивается колесом
        _scrollable = _contentHeight > MaxHeight;
        AutoScroll = _scrollable;
        AutoScrollMinSize = _scrollable ? new Size(WidthPx, _contentHeight) : Size.Empty;
        ClientSize = new Size(WidthPx, _scrollable ? MaxHeight : _contentHeight);
        ApplyRoundedRegion();
        Refresh();
    }

    public void MarkRefreshing()
    {
        _refreshRequestedUtc = DateTime.UtcNow;
        _refreshResetTimer.Stop();
        _refreshResetTimer.Start();
    }

    private void ApplyRoundedRegion()
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        const int r = 14;
        var rect = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        Region?.Dispose();
        Region = new Region(path);
    }

    /// <summary>Показывает панель у указанной точки (клик по виджету).</summary>
    public void ShowNear(Point screenPoint)
    {
        var screen = Screen.FromPoint(screenPoint).WorkingArea;
        var x = Math.Clamp(screenPoint.X - WidthPx / 2, screen.Left + 8, screen.Right - WidthPx - 8);
        var y = Math.Clamp(screenPoint.Y - Height - 12, screen.Top + 8, screen.Bottom - Height - 8);
        Location = new Point(x, y);
        UpdateItems(_items);
        if (!Visible)
        {
            Show();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // при прокрутке рисуем содержимое со смещением
        if (AutoScrollPosition != Point.Empty)
        {
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        }

        var y = PaddingTop;

        using (var titleFont = new Font("Segoe UI Semibold", 14f))
        using (var titleBrush = new SolidBrush(Color.FromArgb(240, 242, 245)))
        {
            g.DrawString("Battery Hub", titleFont, titleBrush, PaddingSide + 2, y);
        }

        var lastUpdate = _items.Select(d => d.LastUpdateUtc).Where(t => t.HasValue)
            .OrderByDescending(t => t).FirstOrDefault();
        string subtitle;
        if (_refreshRequestedUtc > lastUpdate && DateTime.UtcNow - _refreshRequestedUtc < TimeSpan.FromSeconds(3))
        {
            subtitle = L10n.T("flyout.refreshing");
        }
        else
        {
            // статичное время — без ежесекундного «Обновлено 5 с назад»
            subtitle = lastUpdate.HasValue
                ? L10n.F("flyout.updated", lastUpdate.Value.ToLocalTime().ToString("HH:mm"))
                : L10n.T("flyout.collecting");
        }
        // кнопка-крестик в правом верхнем углу (панель больше не закрывается кликом мимо)
        _closeRect = new Rectangle(WidthPx - 32, y - 2, 28, 28);
        using (var closeFont = new Font("Segoe MDL2 Assets", 11f))
        using (var closeBrush = new SolidBrush(Color.FromArgb(160, 156, 164)))
        {
            var glyph = ((char)0xE711).ToString(); // ChromeClose
            var gs = g.MeasureString(glyph, closeFont, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(glyph, closeFont, closeBrush,
                _closeRect.X + (_closeRect.Width - gs.Width) / 2,
                _closeRect.Y + (_closeRect.Height - gs.Height) / 2);
        }

        using (var subFont = new Font("Segoe UI", 9f))
        using (var subBrush = new SolidBrush(Color.FromArgb(150, 156, 164)))
        {
            var subSize = g.MeasureString(subtitle, subFont);
            g.DrawString(subtitle, subFont, subBrush, WidthPx - subSize.Width - PaddingSide - 26, y + 12);
        }
        y += HeaderHeight;

        if (_items.Count == 0)
        {
            DrawEmptyCard(g, y);
            y += CardHeight;
        }
        else
        {
            using var nameFont = new Font("Segoe UI Semibold", 12.5f);
            using var metaFont = new Font("Segoe UI", 9f);
            using var pctFont = new Font("Segoe UI Semibold", 21f);
            foreach (var device in _items)
            {
                DrawDeviceCard(g, device, y, nameFont, metaFont, pctFont);
                y += CardHeight + CardGap;
            }
        }

        y += 4;
        using (var linkFont = new Font("Segoe UI", 9f, FontStyle.Underline))
        using (var linkBrush = new SolidBrush(Color.FromArgb(76, 194, 255)))
        {
            var linkText = L10n.T("footer.bt");
            var linkSize = g.MeasureString(linkText, linkFont);
            _settingsLinkRect = new Rectangle(PaddingSide + 2, y,
                (int)linkSize.Width + 6, (int)linkSize.Height + 2);
            g.DrawString(linkText, linkFont, linkBrush, _settingsLinkRect.X, _settingsLinkRect.Y);
        }

        using (var verFont = new Font("Segoe UI", 8.5f))
        using (var verBrush = new SolidBrush(Color.FromArgb(110, 116, 124)))
        using (var ghFont = new Font("Segoe UI", 8.5f, FontStyle.Underline))
        using (var ghBrush = new SolidBrush(Color.FromArgb(96, 168, 250)))
        {
            var verText = "Battery Hub v1.2.9 · ";
            var verSize = g.MeasureString(verText, verFont);
            var ghText = "github.com/ZombieChypik";
            var ghSize = g.MeasureString(ghText, ghFont);
            var ghX = WidthPx - ghSize.Width - PaddingSide - 2;
            var verX = ghX - verSize.Width + 2;
            g.DrawString(verText, verFont, verBrush, verX, y + 1);
            _githubRect = new Rectangle((int)ghX, y, (int)ghSize.Width + 4, (int)ghSize.Height + 2);
            g.DrawString(ghText, ghFont, ghBrush, ghX, y + 1);
        }
    }

    private void DrawDeviceCard(Graphics g, DeviceStatus device, int y,
        Font nameFont, Font metaFont, Font pctFont)
    {
        var cardRect = new Rectangle(PaddingSide, y, WidthPx - PaddingSide * 2, CardHeight - CardGap);

        using (var cardBrush = new SolidBrush(Color.FromArgb(38, 40, 44)))
        using (var cardPath = Rounded(cardRect, 12))
        {
            g.FillPath(cardBrush, cardPath);
        }

        // иллюстрация модели (в выбранном цвете хаба)
        var illoRect = new Rectangle(
            cardRect.X + 14, cardRect.Y + 12, (int)(cardRect.Height * 0.62f), cardRect.Height - 24);
        DeviceIllustrationPainter.Draw(g, illoRect, device.Kind, device.IsCharging, AppSettings.GetColor());

        var textX = illoRect.Right + 12;

        // название
        using (var nameBrush = new SolidBrush(Color.FromArgb(240, 242, 245)))
        {
            g.DrawString(device.Name, nameFont, nameBrush, textX, cardRect.Y + 10);
        }

        // тип подключения
        using (var metaBrush = new SolidBrush(Color.FromArgb(150, 156, 164)))
        {
            g.DrawString(ConnectionLabel(device), metaFont, metaBrush, textX, cardRect.Y + 36);
        }

        // статус / оценка времени
        using var statusFont = new Font("Segoe UI", 9.5f);
        var statusText = StatusLine(device);
        var statusColor = device.IsCharging ? Color.FromArgb(61, 220, 132)
            : device.NeedsWake ? Color.FromArgb(249, 171, 0)
            : device.BatteryPct is < 20 ? Color.FromArgb(234, 90, 82)
            : Color.FromArgb(150, 156, 164);
        using (var statusBrush = new SolidBrush(statusColor))
        {
            g.DrawString(statusText, statusFont, statusBrush, textX, cardRect.Bottom - 34);
        }

        // крупный процент
        var pctText = device.BatteryPct.HasValue ? $"{device.BatteryPct}%" : "—";
        var pctColor = device.IsCharging
            ? Color.FromArgb(61, 220, 132)
            : device.BatteryPct is < 20 ? Color.FromArgb(235, 100, 90)
            : device.BatteryPct is <= 50 ? Color.FromArgb(249, 190, 70)
            : Color.FromArgb(96, 205, 122);
        using (var pctBrush = new SolidBrush(pctColor))
        {
            var pctSize = g.MeasureString(pctText, pctFont);
            g.DrawString(pctText, pctFont, pctBrush,
                cardRect.Right - pctSize.Width - 14, cardRect.Y + 8);
        }

        // полоска заряда
        var barRect = new RectangleF(textX, cardRect.Bottom - 16,
            cardRect.Width - (textX - cardRect.X) - 14, 5);
        using (var track = new SolidBrush(Color.FromArgb(56, 58, 62)))
        {
            g.FillRectangle(track, barRect);
        }
        if (device.BatteryPct.HasValue)
        {
            var fillWidth = barRect.Width * device.BatteryPct.Value / 100f;
            if (fillWidth >= 1)
            {
                using var fillBrush = new SolidBrush(pctColor);
                g.FillRectangle(fillBrush, barRect.X, barRect.Y, fillWidth, barRect.Height);
            }
        }
    }

    private void DrawEmptyCard(Graphics g, int y)
    {
        var cardRect = new Rectangle(PaddingSide, y, WidthPx - PaddingSide * 2, CardHeight - CardGap);
        using (var cardBrush = new SolidBrush(Color.FromArgb(38, 40, 44)))
        using (var cardPath = Rounded(cardRect, 12))
        {
            g.FillPath(cardBrush, cardPath);
        }
        using var msgFont = new Font("Segoe UI", 10f);
        using var msgBrush = new SolidBrush(Color.FromArgb(150, 156, 164));
        var msg = L10n.T("status.empty");
        var box = g.MeasureString(msg, msgFont, cardRect.Width - 32).ToSize();
        g.DrawString(msg, msgFont, msgBrush,
            cardRect.X + 16, y + (cardRect.Height - box.Height) / 2);
    }

    private static string ConnectionLabel(DeviceStatus device)
    {
        return device.Source switch
        {
            DeviceSource.X11Wired => L10n.T("conn.wired"),
            DeviceSource.X11Dongle => L10n.T("conn.dongle"),
            _ => L10n.T("conn.bt"),
        };
    }

    private static string StatusLine(DeviceStatus device)
    {
        if (device.IsCharging)
        {
            return L10n.T("status.charging");
        }
        if (device.NeedsWake)
        {
            return L10n.T("status.wake");
        }
        if (!device.BatteryPct.HasValue)
        {
            return L10n.T("status.nodata");
        }
        var parts = new List<string>();
        if (device.EtaHours.HasValue)
        {
            parts.Add(BatteryEstimator.FormatEta(device.EtaHours.Value));
        }
        if (device.LastUpdateUtc.HasValue)
        {
            var updated = device.LastUpdateUtc.Value.ToLocalTime().ToString("HH:mm");
            parts.Add(L10n.F("flyout.updated", updated));
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        if (rect.Width <= d || rect.Height <= d)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // клики проверяем с учётом прокрутки
        var point = new Point(e.Location.X - AutoScrollPosition.X, e.Location.Y - AutoScrollPosition.Y);
        if (_closeRect.Contains(point))
        {
            Hide();
            return;
        }
        if (_githubRect.Contains(point))
        {
            try
            {
                using var _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("https://github.com/ZombieChypik") { UseShellExecute = true });
            }
            catch
            {
            }
            return;
        }
        if (_settingsLinkRect.Contains(point))
        {
            try
            {
                using var _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }
            catch
            {
                // настройки недоступны — игнорируем
            }
        }
    }
}
