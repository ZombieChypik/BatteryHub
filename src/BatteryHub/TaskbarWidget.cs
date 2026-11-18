using BatteryHub.Core;
using System.Drawing;
using System.Runtime.InteropServices;

namespace BatteryHub;

/// <summary>
/// Р’РёРґР¶РµС‚ РЅР° РїР°РЅРµР»Рё Р·Р°РґР°С‡ (РєР°Рє РІ Wireless-Device-Battery-Monitor):
/// РѕРєРЅРѕ-СЂРµР±С‘РЅРѕРє Shell_TrayWnd СЃ per-pixel Р°Р»СЊС„РѕР№ (UpdateLayeredWindow).
/// Р¤РѕРЅ РЅРµРІРёРґРёРј (РЅРѕ РєР»РёРєР°Р±РµР»РµРЅ), РїР»РѕСЃРєРёРµ РјРѕРЅРѕС…СЂРѕРјРЅС‹Рµ РіР»РёС„С‹ СѓСЃС‚СЂРѕР№СЃС‚РІ
/// СЂРёСЃСѓСЋС‚СЃСЏ РїРѕРІРµСЂС… В«СЃС‚РµРєР»Р°В» РїР°РЅРµР»Рё вЂ” РєР°Рє СЂРѕРґРЅС‹Рµ Р·РЅР°С‡РєРё.
/// </summary>
public sealed class TaskbarWidget : Form
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi,
        uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE size,
        IntPtr hdcSrc, ref POINT ptSrc, uint crKey, ref BLENDFUNCTION blend, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public readonly bool IsEmpty => Right == 0 && Bottom == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private const uint UlwAlpha = 2;
    private const byte AcSrcAlpha = 1;
    private const byte AcSrcOver = 0;

    private IReadOnlyList<DeviceStatus> _items = Array.Empty<DeviceStatus>();
    private DateTime _refreshRequestedUtc = DateTime.MinValue;
    private int _contentWidthPx = 60;
    private IntPtr _parentedTo = IntPtr.Zero;
    private bool _shouldShow = true;
    private int _appliedX = -1;
    private int _appliedY = -1;
    private int _appliedW = -1;
    private int _appliedH = -1;
    private readonly System.Windows.Forms.Timer _posTimer;

    /// <summary>
    /// Показывать ли виджет (режим «Виджет на панели задач»). В режиме «Значок в трее»
    /// окно должно оставаться скрытым: раньше таймер перестановки каждые 2 с делал
    /// SWP_SHOWWINDOW и виджет «воскресал» рядом со значком в трее.
    /// </summary>
    public void SetShown(bool show)
    {
        _shouldShow = show;
        if (!show && IsHandleCreated)
        {
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x0080 /* SWP_HIDEWINDOW */);
            _appliedW = -1; // при возврате в режим виджета переставить заново
        }
    }

    /// <summary>Р›РµРІС‹Р№ РєР»РёРє РїРѕ РІРёРґР¶РµС‚Сѓ вЂ” РѕС‚РєСЂС‹С‚СЊ РїР°РЅРµР»СЊ.</summary>
    public event Action? WidgetClicked;

    public TaskbarWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(60, 40);

        // РґРµСЂР¶РёРј РїРѕР·РёС†РёСЋ РІ РїР°РЅРµР»Рё: РїРµСЂРµСЃС‚СЂРѕР№РєРё РїР°РЅРµР»Рё Р·Р°РґР°С‡, СЃРјРµРЅР° DPI, РїРµСЂРµР·Р°РїСѓСЃРє Explorer
        _posTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _posTimer.Tick += (_, _) => Reposition();
        _posTimer.Start();
    }

    /// <summary>РћРєРЅРѕ СЂРёСЃСѓРµС‚СЃСЏ С‡РµСЂРµР· UpdateLayeredWindow вЂ” РѕР±С‹С‡РЅР°СЏ РѕС‚СЂРёСЃРѕРІРєР° РЅРµ РЅСѓР¶РЅР°.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    /// <summary>РџРѕРјРµС‡Р°РµС‚ В«Р·Р°РїСЂР°С€РёРІР°РµРј СЃРІРµР¶РёРµ РґР°РЅРЅС‹РµВ».</summary>
    public void MarkRefreshing()
    {
        _refreshRequestedUtc = DateTime.UtcNow;
        RedrawLayered();
    }

    public void UpdateItems(IReadOnlyList<DeviceStatus> items)
    {
        _items = items;
        MeasureContent();
        Reposition();
        RedrawLayered();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _parentedTo = IntPtr.Zero; // новое окно — пробуем встроиться заново
        Reposition();
        RedrawLayered();
    }

    private static IntPtr GetTaskbarHandle() => FindWindow("Shell_TrayWnd", null);

    private static IntPtr GetClockHandle() => FindWindowEx(GetTaskbarHandle(), IntPtr.Zero, "TrayNotifyWnd", null);

    private static RECT GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return rect;
    }

    private void MeasureContent()
    {
        _contentWidthPx = LayoutWidth();
    }

    /// <summary>РўРѕС‡РЅР°СЏ РєРѕРїРёСЏ СЂРµС†РµРїС‚Р° СЂРµС„РµСЂРµРЅСЃР°: Segoe Fluent Icons + Segoe UI Variable Text.</summary>
    private static char GlyphFor(DeviceStatus device)
    {
        return device.Kind switch
        {
            DeviceKind.Headset => (char)0xE7F6,   // Headphone
            DeviceKind.Mouse => (char)0xE962,     // Mouse
            DeviceKind.Keyboard => (char)0xE765,  // KeyboardClassic
            DeviceKind.Gamepad => (char)0xE7FC,   // Game
            _ => (char)0xE702,                    // Bluetooth
        };
    }

    private const char BoltGlyph = (char)0xE945;      // LightningBolt
    private const char UnknownGlyph = (char)0xE996;   // РЅРµС‚ РґР°РЅРЅС‹С…

    private static bool LightTaskbar()
    {
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0);
            return v is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    private Font? _iconsFont;
    private Font? _boltFont;
    private Font? _textFont;

    private void EnsureFonts()
    {
        _iconsFont ??= new Font("Segoe Fluent Icons", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
        _boltFont ??= new Font("Segoe Fluent Icons", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        _textFont ??= new Font("Segoe UI Variable Text", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private string PercentText(DeviceStatus device)
    {
        // СЃРїСЏС‰Р°СЏ РјС‹С€СЊ: РїРѕРєР°Р·С‹РІР°РµРј РїРѕСЃР»РµРґРЅРёР№ РёР·РІРµСЃС‚РЅС‹Р№ РїСЂРѕС†РµРЅС‚ РїСЂРёРіР»СѓС€С‘РЅРЅРѕ (РєР°Рє РІ СЂРµС„РµСЂРµРЅСЃРµ)
        return device.BatteryPct.HasValue ? $"{device.BatteryPct}%" : "?";
    }

    private int LayoutWidth()
    {
        EnsureFonts();
        using var g = CreateGraphics();
        var pad = (int)(DeviceDpi / 96f * 8);
        var width = pad * 2;
        if (_items.Count == 0)
        {
            width += (int)g.MeasureString(UnknownGlyph.ToString(), _iconsFont,
                PointF.Empty, StringFormat.GenericTypographic).Width;
            return width;
        }
        foreach (var device in _items)
        {
            width += (int)g.MeasureString(GlyphFor(device).ToString(), _iconsFont,
                PointF.Empty, StringFormat.GenericTypographic).Width + (int)(DeviceDpi / 96f * 5);
            // точка-маркер заряда + отступ до процента
            width += Math.Max(6, (int)(DeviceDpi / 96f * 7)) + (int)(DeviceDpi / 96f * 4);
            width += (int)g.MeasureString(PercentText(device), _textFont,
                PointF.Empty, StringFormat.GenericTypographic).Width;
            if (device.IsCharging)
            {
                width += (int)(DeviceDpi / 96f * 2);
                width += (int)g.MeasureString(BoltGlyph.ToString(), _boltFont,
                    PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            width += (int)(DeviceDpi / 96f * 14);
        }
        return width;
    }

    private void Reposition()
    {
        try
        {
            var taskbar = GetTaskbarHandle();
            if (taskbar == IntPtr.Zero || !IsHandleCreated)
            {
                return;
            }

            // встраиваемся в панель задач как окно-ребёнок. ВАЖНО: GetParent для
            // pop-up окна всегда возвращает 0 (даже после успешного SetParent),
            // поэтому проверяем родителя через GetAncestor(GA_PARENT) и пытаемся
            // SetParent один раз на каждый дескриптор панели — иначе получался
            // бесконечный цикл «SetParent + перерисовка» каждые 2 секунды.
            var reparented = false;
            if (GetAncestor(Handle, 1 /* GA_PARENT */) != taskbar && _parentedTo != taskbar)
            {
                _parentedTo = taskbar;
                SetParent(Handle, taskbar);
                reparented = true;
            }

            var clockRect = GetRect(GetClockHandle());
            var taskbarRect = GetRect(taskbar);
            if (clockRect.IsEmpty || taskbarRect.IsEmpty)
            {
                return;
            }

            var tbHeight = taskbarRect.Bottom - taskbarRect.Top;
            var height = tbHeight - 8;
            var x = clockRect.Left - taskbarRect.Left - _contentWidthPx - 4;
            var y = (tbHeight - height) / 2;

            // в режиме «Значок в трее» виджет не показываем вовсе
            if (!_shouldShow)
            {
                return;
            }

            // без изменений — ничего не делаем: таймер тикает каждые 2 с,
            // и постоянный UpdateLayeredWindow заставлял виджет «постоянно обновляться»
            if (!reparented
                && _appliedX == x && _appliedY == y
                && _appliedW == _contentWidthPx && _appliedH == height)
            {
                return;
            }

            // SWP_NOACTIVATE обязателен: без него виджет крадёт фокус у открытой панели
            SetWindowPos(Handle, IntPtr.Zero, x, y, _contentWidthPx, height,
                0x0040 /* SWP_SHOWWINDOW */ | 0x0010 /* SWP_NOACTIVATE */);
            _appliedX = x;
            _appliedY = y;
            _appliedW = _contentWidthPx;
            _appliedH = height;
            RedrawLayered();
        }
        catch
        {
            // панель могла пересоздаваться — следующий тик повторит
        }
    }

    /// <summary>Р РёСЃСѓРµС‚ СЃРѕРґРµСЂР¶РёРјРѕРµ РІ Р°Р»СЊС„Р°-Р±РёС‚РјР°Рї Рё РїРµСЂРµРґР°С‘С‚ СЃРёСЃС‚РµРјРµ С‡РµСЂРµР· UpdateLayeredWindow.
    /// РўРѕС‡РЅР°СЏ РєРѕРїРёСЏ СЂРµС†РµРїС‚Р° СЂРµС„РµСЂРµРЅСЃР°: Segoe Fluent Icons 16px, С‚РµРєСЃС‚ Segoe UI Variable Text 13px,
    /// С„РѕРЅ Р°Р»СЊС„Р° 1, РєСЂР°СЃРЅС‹Р№ РЅРёР·РєРёР№ Р·Р°СЂСЏРґ в‰¤15, СЃРёРЅСЏСЏ РјРѕР»РЅРёСЏ Р·Р°СЂСЏРґРєРё.</summary>
    private void RedrawLayered()
    {
        if (!IsHandleCreated)
        {
            return;
        }
        EnsureFonts();
        try
        {
            var taskbarRect = GetRect(GetTaskbarHandle());
            var h = taskbarRect.IsEmpty ? 40 : taskbarRect.Bottom - taskbarRect.Top;
            var w = Math.Max(LayoutWidth(), 1);
            var scale = DeviceDpi / 96f;
            var light = LightTaskbar();

            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                // С„РѕРЅ: Р°Р»СЊС„Р° 1/255 вЂ” РЅРµРІРёРґРёРј РіР»Р°Р·Сѓ (СЃС‚РµРєР»Рѕ), РЅРѕ Р»РѕРІРёС‚ РєР»РёРєРё РїРѕ РІСЃРµР№ РїРѕР»РѕСЃРµ
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                var fg = light ? Color.Black : Color.White;
                var charge = light ? Color.FromArgb(0, 120, 212) : Color.FromArgb(96, 205, 255);
                var dimColor = light ? Color.FromArgb(140, 120, 120, 120) : Color.FromArgb(140, 255, 255, 255);

                using var fgBrush = new SolidBrush(fg);
                using var dimBrush = new SolidBrush(dimColor);

                var x = (int)(8 * scale);
                var format = StringFormat.GenericTypographic;

                if (_items.Count == 0)
                {
                    using var unknownBrush = new SolidBrush(Color.FromArgb(110, fg));
                    var glyph = UnknownGlyph.ToString();
                    var size = g.MeasureString(glyph, _iconsFont, PointF.Empty, format);
                    g.DrawString(glyph, _iconsFont, unknownBrush, x, (h - size.Height) / 2);
                }
                else
                {
                        foreach (var device in _items)
                        {
                            var alpha = device.NeedsWake ? 120 : 255;
                            var levelColor = device.IsCharging
                                ? TrayIconFactory.DotColor(100)
                                : TrayIconFactory.DotColor(device.BatteryPct);
                            var textColor = device.BatteryPct.HasValue ? levelColor : fg;

                            var icon = GlyphFor(device).ToString();
                            var iconSize = g.MeasureString(icon, _iconsFont, PointF.Empty, format);
                            using (var iconBrush = new SolidBrush(Color.FromArgb(alpha, fg)))
                            {
                                g.DrawString(icon, _iconsFont, iconBrush, x, (h - iconSize.Height) / 2);
                            }
                            x += (int)iconSize.Width + (int)(5 * scale);

                            // точка-маркер заряда: красная <20%, жёлтая 20–50%, зелёная >50%, серая — нет данных
                            var dotD = Math.Max(6, (int)(7 * scale));
                            var dotY = (h - dotD) / 2;
                            using (var dotOutline = new Pen(light
                                ? Color.FromArgb(110, 0, 0, 0)
                                : Color.FromArgb(70, 255, 255, 255)))
                            {
                                g.DrawEllipse(dotOutline, x, dotY, dotD, dotD);
                            }
                            using (var dotBrush = new SolidBrush(Color.FromArgb(alpha, levelColor)))
                            {
                                g.FillEllipse(dotBrush, x + 1, dotY + 1, dotD - 2, dotD - 2);
                            }
                            x += dotD + (int)(4 * scale);

                            var text = PercentText(device);
                            var textSize = g.MeasureString(text, _textFont, PointF.Empty, format);
                            using (var textBrush = new SolidBrush(Color.FromArgb(alpha, textColor)))
                            {
                                g.DrawString(text, _textFont, textBrush, x, (h - textSize.Height) / 2);
                            }
                            x += (int)textSize.Width;

                            if (device.IsCharging)
                            {
                                x += (int)(2 * scale);
                                var bolt = BoltGlyph.ToString();
                                var boltSize = g.MeasureString(bolt, _boltFont, PointF.Empty, format);
                                using (var boltBrush = new SolidBrush(Color.FromArgb(alpha, charge)))
                                {
                                    g.DrawString(bolt, _boltFont, boltBrush, x, (h - boltSize.Height) / 2);
                                }
                                x += (int)boltSize.Width;
                            }

                            x += (int)(14 * scale);
                        }
                }
            }

            // premultiply Р°Р»СЊС„С‹ (UpdateLayeredWindow С‚СЂРµР±СѓРµС‚ premultiplied alpha)
            var bytes = new byte[w * h * 4];
            var data = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadWrite,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var total = w * h * 4;
                Marshal.Copy(data.Scan0, bytes, 0, total);
                for (var i = 0; i < total; i += 4)
                {
                    var a = bytes[i + 3];
                    bytes[i] = (byte)(bytes[i] * a / 255);
                    bytes[i + 1] = (byte)(bytes[i + 1] * a / 255);
                    bytes[i + 2] = (byte)(bytes[i + 2] * a / 255);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            // Р’РђР–РќРћ: Bitmap.GetHbitmap С‚РµСЂСЏРµС‚ per-pixel alpha (РІРµСЃСЊ РїСЂСЏРјРѕСѓРіРѕР»СЊРЅРёРє
            // СЃС‚Р°РЅРѕРІРёР»СЃСЏ РЅРµРїСЂРѕР·СЂР°С‡РЅС‹Рј С‚С‘РјРЅС‹Рј Р±Р»РѕРєРѕРј). РџСЂР°РІРёР»СЊРЅС‹Р№ СЃРїРѕСЃРѕР± вЂ”
            // CreateDIBSection + СЂСѓС‡РЅРѕРµ РєРѕРїРёСЂРѕРІР°РЅРёРµ premultiplied РїРёРєСЃРµР»РµР№.
            var hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return;
            }
            try
            {
                var dcMem = CreateCompatibleDC(hdcScreen);
                var bi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                };
                var bitsPtr = IntPtr.Zero;
                var hDib = CreateDIBSection(dcMem, ref bi, 0 /* DIB_RGB_COLORS */,
                    out bitsPtr, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero || bitsPtr == IntPtr.Zero)
                {
                    DeleteDC(dcMem);
                    return;
                }
                Marshal.Copy(bytes, 0, bitsPtr, bytes.Length);
                var old = SelectObject(dcMem, hDib);

                var size = new SIZE { X = w, Y = h };
                var src = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AcSrcOver,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AcSrcAlpha,
                };
                UpdateLayeredWindow(Handle, hdcScreen, IntPtr.Zero, ref size,
                    dcMem, ref src, 0, ref blend, UlwAlpha);

                SelectObject(dcMem, old);
                DeleteObject(hDib);
                DeleteDC(dcMem);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
        catch
        {
            // РїРµСЂРµСЂРёСЃРѕРІРєР° РЅРµ РєСЂРёС‚РёС‡РЅР° вЂ” СЃР»РµРґСѓСЋС‰РёР№ Р°РїРґРµР№С‚ РїРѕРІС‚РѕСЂРёС‚
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            WidgetClicked?.Invoke();
        }
    }
}

