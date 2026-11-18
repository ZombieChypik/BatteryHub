using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BatteryHub;

/// <summary>
/// Значок трея «мини-мышка + процент заряда».
/// Собирается как многослойный ICO (16/20/24/32/48 px) для чёткости на любом DPI.
/// </summary>
public static class TrayIconFactory
{
    private static readonly int[] FrameSizes = { 16, 20, 24, 32, 48 };
    private static readonly Color OutlineDark = Color.FromArgb(25, 25, 25);
    private static readonly Color OutlineLight = Color.White;
    private static readonly Color TextColor = Color.White;

    /// <summary>Создаёт многоразмерный совмещённый значок: мышь с точкой-логотипом.</summary>
    public static Icon CreateCombined(int? pct, bool charging, Color? bodyColor = null)
    {
        return CreateFromRender((g, s) => Render(g, s, pct, charging, bodyColor));
    }

    /// <summary>Создаёт значок-мышку с бейджем состояния (⚡ / ?).</summary>
    public static Icon CreateMouse(int? pct, bool charging)
    {
        return CreateFromRender((g, s) => RenderMouse(g, s, pct, charging));
    }

    /// <summary>Создаёт значок с процентом заряда.</summary>
    public static Icon CreatePercent(int? pct, bool charging)
    {
        return CreateFromRender((g, s) => RenderPercent(g, s, pct, charging));
    }

    private static Icon CreateFromRender(Action<Graphics, int> render)
    {
        using var ms = new MemoryStream();
        WriteIco(ms, render);
        ms.Position = 0;
        return new Icon(ms);
    }

    /// <summary>Отрисовка одного кадра значка: крупная мышка, внутри — точка-«логотип» с цветом заряда.</summary>
    public static void Render(Graphics g, int size, int? pct, bool charging, Color? bodyColor = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        DrawMouseGlyph(g, size, bodyColor);
        DrawLogoDot(g, size, pct, charging);
    }

    /// <summary>Кружок-свотч цвета для меню (18×18).</summary>
    public static Bitmap MakeSwatch(Color color)
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 14, 14);
        using var pen = new Pen(Color.FromArgb(70, 0, 0, 0));
        g.DrawEllipse(pen, 1, 1, 16, 16);
        return bmp;
    }

    /// <summary>Цвет точки состояния: зелёный &gt;50%, жёлтый 20–50%, красный &lt;20%, серый — нет данных.</summary>
    public static Color DotColor(int? pct)
    {
        return pct switch
        {
            null => Color.FromArgb(138, 143, 150),   // серый — нет данных
            < 20 => Color.FromArgb(234, 67, 53),     // красный
            <= 50 => Color.FromArgb(249, 171, 0),    // жёлтый
            _ => Color.FromArgb(61, 220, 132),       // зелёный
        };
    }

    private static void DrawLogoDot(Graphics g, int s, int? pct, bool charging)
    {
        // точка-«логотип»: крупнее, чуть ниже, с БЕЛЫМ кольцом — видна на корпусе любого цвета
        var d = 0.28f * s;
        var rect = new RectangleF((s - d) / 2f, 0.48f * s, d, d);
        var color = charging ? DotColor(100) : DotColor(pct);
        var ringW = Math.Max(1.2f, 0.055f * s);

        // тёмная кромка снаружи (для светлой панели задач)
        using (var outer = new Pen(OutlineDark, Math.Max(1f, 0.045f * s)))
        {
            var o = ringW + Math.Max(1f, 0.045f * s);
            g.DrawEllipse(outer, rect.X - o, rect.Y - o, rect.Width + o * 2, rect.Height + o * 2);
        }
        // белое кольцо
        using (var ring = new Pen(Color.FromArgb(242, 248, 246), ringW))
        {
            g.DrawEllipse(ring, rect.X - ringW / 2, rect.Y - ringW / 2,
                rect.Width + ringW, rect.Height + ringW);
        }
        // заливка
        using (var fill = new SolidBrush(color))
        {
            g.FillEllipse(fill, rect);
        }
        // блик — точка выглядит подсвеченной, как логотип
        using (var gloss = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
        {
            g.FillEllipse(gloss, rect.X + rect.Width * 0.16f, rect.Y + rect.Height * 0.10f,
                rect.Width * 0.38f, rect.Height * 0.38f);
        }
    }

    private static void DrawBigNumber(Graphics g, int s, int? pct, bool charging)
    {
        if (charging)
        {
            DrawBoltCentered(g, s);
            return;
        }

        var text = pct.HasValue ? pct.Value.ToString() : "?";
        var fontSize = s * (text.Length <= 2 ? 0.76f : 0.58f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        var width = g.MeasureString(text, font, PointF.Empty, format).Width;
        DrawOutlinedText(g, text, fontSize, (s - width) / 2f, (s - fontSize) / 2f, format);
    }

    // ---------- элементы ----------

    private static void DrawMouseGlyph(Graphics g, int s, Color? bodyColor = null)
    {
        // корпус мыши: нормальные пропорции (не тонкая и не сплющенная)
        var rect = new RectangleF(0.19f * s, 0.07f * s, 0.62f * s, 0.86f * s);
        var body = bodyColor ?? TextColor;

        var isDarkBody = body.GetBrightness() < 0.45f;
        // тёмный корпус на тёмной панели: толстый СВЕТЛЫЙ ореол, иначе — тёмный
        using (var haloPen = new Pen(
            isDarkBody ? Color.FromArgb(235, 238, 242) : OutlineDark,
            Math.Max(1.4f, (isDarkBody ? 0.10f : 0.06f) * s)))
        using (var fill = new SolidBrush(body))
        using (var path = MouseBodyPath(rect))
        {
            g.DrawPath(haloPen, path);
            g.FillPath(fill, path);
        }

        // детали (колесико, швы) — цветом, контрастным корпусу
        var detailColor = isDarkBody ? Color.FromArgb(225, 228, 232) : Color.FromArgb(50, 52, 56);

        // колесико
        var wheel = new RectangleF(0.465f * s, 0.10f * s, 0.07f * s, 0.16f * s);
        using (var wheelBrush = new SolidBrush(detailColor))
        using (var wheelPath = RoundedPath(wheel, wheel.Width / 2f))
        {
            g.FillPath(wheelBrush, wheelPath);
        }

        // Т-разделитель кнопок
        using (var darkPen = new Pen(detailColor, Math.Max(1f, 0.05f * s)))
        {
            var cx = rect.X + rect.Width / 2f;
            var splitY = rect.Y + rect.Height * 0.36f;
            g.DrawLine(darkPen, rect.Left + 1.5f, splitY, rect.Right - 1.5f, splitY);
            g.DrawLine(darkPen, cx, rect.Top + 1.5f, cx, splitY);
        }

        // блик на корпусе (на светлых корпусах)
        if (!isDarkBody)
        {
            using (var gloss = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
            using (var glossPath = RoundedPath(
                new RectangleF(rect.X + rect.Width * 0.14f, rect.Y + rect.Height * 0.05f,
                    rect.Width * 0.72f, rect.Height * 0.24f), rect.Width * 0.3f))
            {
                g.FillPath(gloss, glossPath);
            }
        }
    }

    /// <summary>Контур корпуса мыши: полукруглый верх (купол), прямые бока, скруглённый низ.</summary>
    private static GraphicsPath MouseBodyPath(RectangleF b)
    {
        var path = new GraphicsPath();
        var rT = b.Width / 2f;      // купол — полукруг во всю ширину
        var rB = b.Width * 0.26f;   // мягкие нижние углы

        path.AddArc(b.X, b.Y, 2 * rT, 2 * rT, 180, 180);                    // купол
        path.AddLine(b.Right, b.Y + rT, b.Right, b.Bottom - rB);            // правая грань
        path.AddArc(b.Right - 2 * rB, b.Bottom - 2 * rB, 2 * rB, 2 * rB, 0, 90);  // правый нижний угол
        path.AddLine(b.Right - rB, b.Bottom, b.X + rB, b.Bottom);           // низ
        path.AddArc(b.X, b.Bottom - 2 * rB, 2 * rB, 2 * rB, 90, 90);        // левый нижний угол
        path.AddLine(b.X, b.Bottom - rB, b.X, b.Y + rT);                    // левая грань
        path.CloseFigure();
        return path;
    }

    private static void DrawPercent(Graphics g, int s, int? pct, bool charging)
    {
        if (charging)
        {
            DrawBolt(g, s);
            return;
        }

        // На мелких кадрах (24px = реальный размер панели при 100% DPI) знак %
        // физически нечитаем — цифры делаем максимально крупными без него.
        // % остаётся на кадрах 32/48 px (высокие DPI, там он читается).
        var digits = pct.HasValue ? pct.Value.ToString() : "?";
        var hasPercentSign = s >= 32 && pct.HasValue && digits.Length <= 2;

        var digitsFont = s * (hasPercentSign ? 0.46f : 0.56f);
        var percentFont = s * 0.32f;
        using var format = new StringFormat(StringFormat.GenericTypographic);
        using var digitsFontObj = new Font("Segoe UI", digitsFont, FontStyle.Bold, GraphicsUnit.Pixel);
        using var percentFontObj = new Font("Segoe UI", percentFont, FontStyle.Bold, GraphicsUnit.Pixel);

        var digitsWidth = g.MeasureString(digits, digitsFontObj, PointF.Empty, format).Width;
        var percentWidth = hasPercentSign
            ? g.MeasureString("%", percentFontObj, PointF.Empty, format).Width
            : 0f;

        // размещение: от конца мыши до правого края с запасом под обводку
        var textStart = 0.31f * s;
        var rightReserve = 0.05f * s;
        var budget = s - rightReserve - textStart;
        var total = digitsWidth + (hasPercentSign ? percentWidth + 0.01f * s : 0f);
        if (total > budget && total > 0)
        {
            var shrink = budget / total;
            digitsFont *= shrink;
            percentFont *= shrink;
            digitsWidth *= shrink;
            percentWidth *= shrink;
        }

        var digitsY = 0.14f * s;
        // % поднят к верху цифр (superscript)
        var percentY = digitsY + (digitsFont - percentFont) * 0.62f;

        DrawOutlinedText(g, digits, digitsFont, textStart, digitsY, format);
        if (hasPercentSign)
        {
            DrawOutlinedText(g, "%", percentFont, textStart + digitsWidth + 0.01f * s,
                percentY, format);
        }
    }

    private static void DrawOutlinedText(Graphics g, string text, float fontSize,
        float x, float y, StringFormat format)
    {
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)FontStyle.Bold, fontSize,
            new PointF(x, y), format);

        // тёмная подложка для читаемости на светлой панели + белая заливка
        using (var outlinePen = new Pen(OutlineDark, Math.Max(1.2f, 0.22f * fontSize)))
        {
            outlinePen.LineJoin = LineJoin.Round;
            g.DrawPath(outlinePen, path);
        }
        using (var fillBrush = new SolidBrush(TextColor))
        {
            g.FillPath(fillBrush, path);
        }
    }

    private static void DrawBolt(Graphics g, int s)
    {
        // молния в зоне числа, по вертикали по центру
        var cx = 0.62f * s;
        var top = 0.16f * s;
        var bottom = 0.82f * s;
        DrawBoltShape(g, cx, top, bottom, s * 0.10f);
    }

    private static void DrawBoltCentered(Graphics g, int s)
    {
        // молния на весь кадр (16px, без мышки)
        DrawBoltShape(g, 0.5f * s, 0.10f * s, 0.90f * s, s * 0.12f);
    }

    private static void DrawBoltShape(Graphics g, float cx, float top, float bottom, float stroke)
    {
        var height = bottom - top;
        PointF[] points =
        {
            new(cx + 0.20f * height, top),
            new(cx - 0.24f * height, top + 0.60f * height),
            new(cx - 0.02f * height, top + 0.60f * height),
            new(cx - 0.20f * height, bottom),
            new(cx + 0.24f * height, top + 0.45f * height),
            new(cx + 0.02f * height, top + 0.45f * height),
        };
        using var outline = new Pen(OutlineDark, Math.Max(1f, stroke))
        {
            LineJoin = LineJoin.Round,
        };
        using var fill = new SolidBrush(TextColor);
        g.DrawPolygon(outline, points);
        g.FillPolygon(fill, points);
    }



    private static Color ColorFor(int? pct, bool charging)
    {
        if (charging)
        {
            return Color.FromArgb(52, 168, 83); // зелёный
        }
        return pct switch
        {
            null => Color.FromArgb(154, 160, 166),   // серый — нет данных
            < 20 => Color.FromArgb(234, 67, 53),     // красный
            < 40 => Color.FromArgb(249, 171, 0),     // жёлтый
            _ => Color.FromArgb(52, 168, 83),        // зелёный
        };
    }

    /// <summary>Цвет уровня заряда для UI (панель, прогресс-бары).</summary>
    public static Color LevelColor(int? pct) => ColorFor(pct, charging: false);

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
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

    // ---------- сборка ICO ----------

    /// <summary>Значок-мышкa с бейджем: ⚡ если заряжается, ? если заряд неизвестен.</summary>
    public static void RenderMouse(Graphics g, int s, int? pct, bool charging)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // крупная залитая мышь, чуть смещена влево — справа место под бейдж
        var rect = new RectangleF(0.06f * s, 0.14f * s, 0.52f * s, 0.72f * s);

        using (var haloPen = new Pen(OutlineDark, Math.Max(1f, 0.05f * s)))
        using (var fill = new SolidBrush(TextColor))
        using (var path = RoundedPath(rect, rect.Width * 0.48f))
        {
            g.DrawPath(haloPen, path);
            g.FillPath(fill, path);
        }

        using (var darkPen = new Pen(OutlineDark, Math.Max(1f, 0.055f * s)))
        {
            var cx = rect.X + rect.Width / 2f;
            var splitY = rect.Y + rect.Height * 0.42f;
            g.DrawLine(darkPen, rect.Left + 1f, splitY, rect.Right - 1f, splitY);
            g.DrawLine(darkPen, cx, rect.Top + 1f, cx, splitY);
        }

        if (charging)
        {
            DrawBoltShape(g, 0.80f * s, 0.02f * s, 0.44f * s, Math.Max(1f, 0.06f * s));
        }
        else if (!pct.HasValue)
        {
            using var format = new StringFormat(StringFormat.GenericTypographic);
            DrawOutlinedText(g, "?", s * 0.36f, 0.60f * s, 0.02f * s, format);
        }
    }

    /// <summary>Значок процента: крупное число / молния / «?».</summary>
    public static void RenderPercent(Graphics g, int s, int? pct, bool charging)
    {
        DrawBigNumber(g, s, pct, charging);
    }

    private static void WriteIco(System.IO.Stream stream, Action<Graphics, int> render)
    {
        var pngs = FrameSizes.Select(size =>
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                render(g, size);
            }
            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((short)0);      // reserved
        writer.Write((short)1);      // type: icon
        writer.Write((short)pngs.Count);

        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            var size = FrameSizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size)); // width
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);   // palette
            writer.Write((byte)0);   // reserved
            writer.Write((short)1);  // planes
            writer.Write((short)32); // bpp
            writer.Write((int)pngs[i].Length);
            writer.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var png in pngs)
        {
            writer.Write(png);
        }
        writer.Flush();
    }
}
