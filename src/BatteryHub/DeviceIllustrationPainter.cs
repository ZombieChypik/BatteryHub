using BatteryHub.Core;
using System.Drawing.Drawing2D;

namespace BatteryHub;

/// <summary>
/// Качественные плоские 2D-иллюстрации устройств в едином стиле:
/// мягкие градиенты, аккуратные детали, мягкая тень. Без грубых контуров.
/// </summary>
public static class DeviceIllustrationPainter
{
    /// <summary>Рисует модель устройства. bodyColor — цвет корпуса хаба (меню «Цвет»); null — светлый по умолчанию.</summary>
    public static void Draw(Graphics g, Rectangle r, DeviceKind kind, bool charging, Color? bodyColor = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        _bodyOverride = bodyColor;
        try
        {
            switch (kind)
            {
                case DeviceKind.Mouse:
                    DrawMouse(g, r);
                    break;
                case DeviceKind.Keyboard:
                    DrawKeyboard(g, r);
                    break;
                case DeviceKind.Headset:
                    DrawHeadset(g, r);
                    break;
                case DeviceKind.Gamepad:
                    DrawGamepad(g, r);
                    break;
                default:
                    DrawGeneric(g, r);
                    break;
            }

            if (charging)
            {
                DrawChargingBadge(g, r);
            }
        }
        finally
        {
            _bodyOverride = null;
        }
    }

    // палитра иллюстраций: белый корпус по умолчанию или выбранный цвет хаба
    private static Color? _bodyOverride;

    private static Color Lighten(Color c, float f) => MixTo(c, Color.White, f);
    private static Color Darken(Color c, float f) => MixTo(c, Color.Black, f);

    private static Color MixTo(Color c, Color to, float f)
    {
        return Color.FromArgb(
            (int)(c.R + (to.R - c.R) * f),
            (int)(c.G + (to.G - c.G) * f),
            (int)(c.B + (to.B - c.B) * f));
    }

    private static Color BodyTop => _bodyOverride is { } c ? Lighten(c, 0.42f) : Color.FromArgb(244, 246, 248);
    private static Color BodyBottom => _bodyOverride is { } c ? Darken(c, 0.20f) : Color.FromArgb(198, 204, 212);
    private static Color Seam => _bodyOverride is { } c
        ? Color.FromArgb(150, Darken(c, 0.40f))
        : Color.FromArgb(150, 120, 126, 134);
    private static Color WheelTop => _bodyOverride is { } c ? Darken(c, 0.50f) : Color.FromArgb(72, 74, 80);
    private static Color WheelBottom => _bodyOverride is { } c ? Darken(c, 0.72f) : Color.FromArgb(46, 48, 54);
    private static Color Detail => _bodyOverride is { } c ? Darken(c, 0.48f) : Color.FromArgb(88, 92, 100);
    private static Color BandTop => _bodyOverride is { } c ? Darken(c, 0.15f) : Color.FromArgb(214, 220, 228);
    private static Color BandBottom => _bodyOverride is { } c ? Darken(c, 0.42f) : Color.FromArgb(140, 148, 160);
    private static Color CupTop => _bodyOverride is { } c ? Lighten(c, 0.30f) : Color.FromArgb(238, 240, 244);
    private static Color CupBottom => _bodyOverride is { } c ? Darken(c, 0.28f) : Color.FromArgb(150, 158, 170);

    private static LinearGradientBrush BodyBrush(RectangleF rect)
    {
        return new LinearGradientBrush(rect, BodyTop, BodyBottom, 92f);
    }

    private static void DrawShadow(Graphics g, RectangleF area)
    {
        using var shadow = new SolidBrush(Color.FromArgb(55, 10, 12, 16));
        g.FillEllipse(shadow, area.X + area.Width * 0.14f, area.Bottom - area.Height * 0.055f,
            area.Width * 0.72f, area.Height * 0.085f);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
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

    // ────────────────────────── МЫШЬ ──────────────────────────

    private static void DrawMouse(Graphics g, Rectangle r)
    {
        var body = new RectangleF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.06f,
            r.Width * 0.64f, r.Height * 0.80f);
        DrawShadow(g, r);

        // корпус: вертикальная капсула с мягким вертикальным градиентом
        using (var brush = BodyBrush(body))
        using (var path = Rounded(body, body.Width * 0.48f))
        {
            g.FillPath(brush, path);
        }

        // мягкая тень справа-снизу корпуса (объём)
        using (var shade = new SolidBrush(Color.FromArgb(40, 90, 100, 115)))
        using (var shadePath = Rounded(
            new RectangleF(body.X + body.Width * 0.55f, body.Y + body.Height * 0.12f,
                body.Width * 0.45f, body.Height * 0.84f), body.Width * 0.22f))
        {
            g.FillPath(shade, shadePath);
        }

        // разделение кнопок: выдавленный шов
        var splitY = body.Y + body.Height * 0.38f;
        var cx = body.X + body.Width / 2f;
        using (var seam = new Pen(Seam, 1.6f))
        {
            g.DrawLine(seam, body.Left + body.Width * 0.08f, splitY, body.Right - body.Width * 0.08f, splitY);
            g.DrawLine(seam, cx, body.Top + body.Height * 0.06f, cx, splitY);
        }

        // колесико: капсула с градиентом и бликом
        var wheel = new RectangleF(cx - body.Width * 0.075f, body.Y + body.Height * 0.10f,
            body.Width * 0.15f, body.Height * 0.26f);
        using (var wheelBrush = new LinearGradientBrush(wheel, WheelTop, WheelBottom, 90f))
        using (var wheelPath = Rounded(wheel, wheel.Width / 2f))
        {
            g.FillPath(wheelBrush, wheelPath);
        }
        using (var wheelGloss = new SolidBrush(Color.FromArgb(130, 255, 255, 255)))
        {
            g.FillRectangle(wheelGloss, wheel.X + wheel.Width * 0.25f, wheel.Y + wheel.Height * 0.10f,
                wheel.Width * 0.24f, wheel.Height * 0.55f);
        }

        // верхний глянец корпуса
        using (var gloss = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
        using (var glossPath = Rounded(
            new RectangleF(body.X + body.Width * 0.16f, body.Y + body.Height * 0.05f,
                body.Width * 0.68f, body.Height * 0.30f), body.Width * 0.3f))
        {
            g.FillPath(gloss, glossPath);
        }

        // нижняя подсветка кромки
        using (var rim = new Pen(Color.FromArgb(70, 255, 255, 255), 1.4f))
        {
            g.DrawLine(rim, body.Left + body.Width * 0.18f, body.Bottom - 1.2f,
                body.Right - body.Width * 0.18f, body.Bottom - 1.2f);
        }
    }

    // ────────────────────────── КЛАВИАТУРА ──────────────────────────

    private static void DrawKeyboard(Graphics g, Rectangle r)
    {
        var body = new RectangleF(r.X + r.Width * 0.05f, r.Y + r.Height * 0.30f,
            r.Width * 0.90f, r.Height * 0.48f);
        DrawShadow(g, r);

        using (var brush = BodyBrush(body))
        using (var path = Rounded(body, 8))
        {
            g.FillPath(brush, path);
        }

        // блик по верхней части
        using (var gloss = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
        using (var glossPath = Rounded(
            new RectangleF(body.X + body.Width * 0.04f, body.Y + body.Height * 0.06f,
                body.Width * 0.92f, body.Height * 0.40f), 5))
        {
            g.FillPath(gloss, glossPath);
        }

        // клавиши: два ряда с градиентом
        var keyH = body.Height * 0.26f;
        for (var row = 0; row < 2; row++)
        {
            for (var i = 0; i < 9; i++)
            {
                var key = new RectangleF(
                    body.X + body.Width * 0.05f + i * (body.Width * 0.100f),
                    body.Y + body.Height * 0.18f + row * (keyH * 1.55f),
                    body.Width * 0.082f, keyH);
                using (var keyBrush = new LinearGradientBrush(key,
                    Color.FromArgb(96, 100, 108), Color.FromArgb(58, 60, 66), 90f))
                using (var keyPath = Rounded(key, 2.5f))
                {
                    g.FillPath(keyBrush, keyPath);
                }
                using var keyTop = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
                g.DrawLine(keyTop, key.X + 1, key.Y + 0.8f, key.Right - 1, key.Y + 0.8f);
            }
        }

        // нижняя подсветка кромки
        using (var rim = new Pen(Color.FromArgb(70, 255, 255, 255), 1.3f))
        {
            g.DrawLine(rim, body.Left + 3, body.Bottom - 1.2f, body.Right - 3, body.Bottom - 1.2f);
        }
    }

    // ────────────────────────── НАУШНИКИ ──────────────────────────

    private static void DrawHeadset(Graphics g, Rectangle r)
    {
        var band = new RectangleF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.08f,
            r.Width * 0.56f, r.Height * 0.52f);
        DrawShadow(g, r);

        // дуга с градиентом
        using (var bandBrush = new LinearGradientBrush(
            new RectangleF(band.X, band.Y, band.Width, band.Height + 6),
            BandTop, BandBottom, 90f))
        using (var bandPen = new Pen(bandBrush, 5.5f))
        {
            g.DrawArc(bandPen, band, 180, 180);
        }

        // чашки: капсулы с градиентом и бликом
        var cupY = r.Y + r.Height * 0.42f;
        foreach (var side in new[] { 0f, 1f })
        {
            var cup = new RectangleF(
                r.X + r.Width * 0.14f + side * (r.Width * 0.55f), cupY,
                r.Width * 0.28f, r.Height * 0.38f);
            using (var cupBrush = new LinearGradientBrush(cup, CupTop, CupBottom, 90f))
            using (var cupPath = Rounded(cup, 7))
            {
                g.FillPath(cupBrush, cupPath);
            }
            using (var cupGloss = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
            using (var glossPath = Rounded(
                new RectangleF(cup.X + cup.Width * 0.12f, cup.Y + cup.Height * 0.08f,
                    cup.Width * 0.5f, cup.Height * 0.30f), 4))
            {
                g.FillPath(cupGloss, glossPath);
            }
        }
    }

    // ────────────────────────── ГЕЙМПАД ──────────────────────────

    private static void DrawGamepad(Graphics g, Rectangle r)
    {
        var body = new RectangleF(r.X + r.Width * 0.08f, r.Y + r.Height * 0.30f,
            r.Width * 0.84f, r.Height * 0.44f);
        DrawShadow(g, r);

        using (var brush = BodyBrush(body))
        using (var path = Rounded(body, body.Height / 2f))
        {
            g.FillPath(brush, path);
        }

        // блик
        using (var gloss = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
        using (var glossPath = Rounded(
            new RectangleF(body.X + body.Width * 0.06f, body.Y + body.Height * 0.08f,
                body.Width * 0.88f, body.Height * 0.45f), body.Height / 2f))
        {
            g.FillPath(gloss, glossPath);
        }

        // стики и кнопки
        using (var dot = new SolidBrush(Detail))
        {
            g.FillEllipse(dot, body.X + body.Width * 0.16f, body.Y + body.Height * 0.30f, 7, 7);
            g.FillEllipse(dot, body.X + body.Width * 0.26f, body.Y + body.Height * 0.14f, 7, 7);
            g.FillEllipse(dot, body.X + body.Width * 0.72f, body.Y + body.Height * 0.26f, 8, 8);
        }
    }

    // ────────────────────── ПРОЧЕЕ ──────────────────────

    private static void DrawGeneric(Graphics g, Rectangle r)
    {
        var body = new RectangleF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.18f,
            r.Width * 0.56f, r.Height * 0.58f);
        DrawShadow(g, r);

        using (var brush = BodyBrush(body))
        using (var path = Rounded(body, body.Width / 2f))
        {
            g.FillPath(brush, path);
        }

        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var brush2 = new SolidBrush(Detail);
        var size = g.MeasureString("?", font);
        g.DrawString("?", font, brush2,
            body.X + (body.Width - size.Width) / 2, body.Y + (body.Height - size.Height) / 2 + 1);
    }

    // ────────────────────── ЗНАЧОК ЗАРЯДКИ ──────────────────────

    private static void DrawChargingBadge(Graphics g, Rectangle r)
    {
        // яркая молния зарядки в кружке — у нижнего правого угла модели
        var d = Math.Min(r.Width, r.Height) * 0.30f;
        var bx = r.Right - d * 1.15f;
        var by = r.Bottom - d * 1.15f;

        using (var badge = new SolidBrush(Color.FromArgb(210, 24, 26, 30)))
        {
            g.FillEllipse(badge, bx, by, d, d);
        }
        using (var ring = new Pen(Color.FromArgb(235, 250, 248), 1.6f))
        {
            g.DrawEllipse(ring, bx, by, d, d);
        }

        var cx = bx + d / 2f;
        var cy = by + d / 2f;
        var s = d * 0.30f;
        PointF[] bolt =
        {
            new(cx + s * 0.35f, cy - s),
            new(cx - s * 0.45f, cy + s * 0.15f),
            new(cx - s * 0.05f, cy + s * 0.15f),
            new(cx - s * 0.35f, cy + s),
            new(cx + s * 0.45f, cy - s * 0.1f),
            new(cx + s * 0.05f, cy - s * 0.1f),
        };
        using var boltFill = new SolidBrush(Color.FromArgb(61, 220, 132));
        g.FillPolygon(boltFill, bolt);
    }
}
