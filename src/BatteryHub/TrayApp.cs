using BatteryHub.Core;
using BatteryHub.Core.Sources;

namespace BatteryHub;

/// <summary>
/// Хаб: виджет на панели задач (мышке + точка заряда + процент) или значок в трее,
/// панель устройств по левому клику, полное меню настроек, уведомления о низком заряде.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private static readonly int[] LowBatteryThresholds = { 20, 10 };

    private readonly DeviceRegistry _registry = new();
    private readonly BluetoothSource _bluetooth;
    private readonly X11DongleSource _x11;
    private readonly NotifyIcon _tray;
    private readonly TaskbarWidget _widget;
    private readonly EventWaitHandle _pokeEvent;
    private readonly RegisteredWaitHandle _pokeRegistration;
    private readonly Dictionary<(string DeviceId, int Threshold), bool> _notified = new();
    private readonly Control _uiAnchor = new(); // создаёт WindowsFormsSynchronizationContext

    private readonly SynchronizationContext _sync;
    private Icon _icon;
    private FlyoutForm? _flyout;
    private (int? MinPct, bool AnyCharging, string BodyColor) _lastIconState = ((int?)null, false, "");
    private string? _lastTooltip;
    private readonly PerformanceMonitor _perf = new();

    public TrayApp()
    {
        _sync = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Нет UI-синхронизационного контекста");
        _bluetooth = new BluetoothSource(_registry);
        _x11 = new X11DongleSource(_registry);

        _pokeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowPanelEventName);
        _pokeRegistration = ThreadPool.RegisterWaitForSingleObject(
            _pokeEvent, (_, _) => _sync.Post(_ => ShowFlyout(), null),
            null, Timeout.Infinite, executeOnlyOnce: false);

        _icon = TrayIconFactory.CreateCombined(null, charging: false);
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Battery Hub",
            Visible = AppSettings.Mode == DisplayMode.TrayIcon,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += OnTrayClick;

        _widget = new TaskbarWidget();
        _widget.WidgetClicked += ShowFlyout;
        _widget.ContextMenuStrip = _tray.ContextMenuStrip;
        _widget.SetShown(AppSettings.Mode == DisplayMode.TaskbarWidget);

        // в режиме значка в трее сразу закрепляем его на панели задач (возле РУС/ENG)
        if (AppSettings.Mode == DisplayMode.TrayIcon)
        {
            TrayPromotion.PromoteWithRetry();
        }

        _registry.Changed += () => _sync.Post(_ => RefreshUi(), null);

        // не конкурируем с играми и рабочими приложениями за CPU
        try
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                System.Diagnostics.ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }

        Autostart.EnsureEnabled();

        _bluetooth.Start();
        _x11.Start();

        var flushTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        flushTimer.Tick += (_, _) => _registry.Flush();
        flushTimer.Start();

        _perf.Start();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowFlyout();
        }
    }

    private void ShowFlyout()
    {
        // реальное время по клику: немедленно перезапрашиваем оба источника
        _bluetooth.RefreshNow();
        _x11.Poke();

        _flyout ??= new FlyoutForm();
        _flyout.UpdateItems(_registry.GetSnapshot());
        _flyout.MarkRefreshing();
        _flyout.ShowNear(Cursor.Position);
        // панель не закрывается повторным кликом и кликом мимо —
        // только «✕» на панели или Escape
        if (_flyout.Visible)
        {
            _flyout.Activate();
        }
    }

    /// <summary>Тёмная тема контекстного меню с подкраской цветом хаба.</summary>
    private sealed class HubMenuColors : ProfessionalColorTable
    {
        private readonly Color _bg;
        private readonly Color _accent;

        public HubMenuColors(Color theme, Color accent)
        {
            _bg = theme;
            _accent = accent;
        }

        private Color Selected => Mix(_bg, _accent, 0.38f);

        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color MenuBorder => Mix(_bg, _accent, 0.30f);
        public override Color MenuItemBorder => Color.FromArgb(70, 0, 0, 0);
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color MenuItemPressedGradientBegin => Mix(_bg, _accent, 0.5f);
        public override Color MenuItemPressedGradientEnd => Mix(_bg, _accent, 0.5f);
        public override Color CheckBackground => Mix(_bg, _accent, 0.45f);
        public override Color CheckSelectedBackground => Mix(_bg, _accent, 0.5f);
        public override Color CheckPressedBackground => Mix(_bg, _accent, 0.5f);
        public override Color SeparatorDark => Color.FromArgb(72, 74, 78);
        public override Color SeparatorLight => Color.FromArgb(72, 74, 78);
    }

    private sealed class HubMenuRenderer : ToolStripProfessionalRenderer
    {
        public HubMenuRenderer(Color theme, Color accent) : base(new HubMenuColors(theme, accent))
        {
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(210, 226, 232);
            base.OnRenderArrow(e);
        }
    }

    private static Color Mix(Color a, Color b, float f)
    {
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * f),
            (int)(a.G + (b.G - a.G) * f),
            (int)(a.B + (b.B - a.B) * f));
    }

    private static void ApplyStyleRecursive(ToolStripDropDown drop, Color accent)
    {
        // фон меню — выбранный цвет, затемнённый до ~45% яркости: оттенок виден сразу,
        // а светлый текст остаётся читаемым на любом цвете
        var themeBg = Mix(accent, Color.Black, 0.55f);
        drop.Renderer = new HubMenuRenderer(themeBg, accent);
        drop.ForeColor = Color.FromArgb(235, 238, 242);
        foreach (var item in drop.Items.OfType<ToolStripMenuItem>())
        {
            if (item.HasDropDownItems)
            {
                ApplyStyleRecursive(item.DropDown, accent);
            }
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L10n.T("menu.open"), null, (_, _) => ShowFlyout());
        menu.Items.Add(L10n.T("menu.refresh"), null, (_, _) =>
        {
            _bluetooth.RefreshNow();
            _x11.Poke();
            _flyout?.UpdateItems(_registry.GetSnapshot());
        });
        menu.Items.Add(new ToolStripSeparator());

        // Отображение: виджет на панели задач или значок в трее
        var displayMenu = new ToolStripMenuItem(L10n.T("menu.display"));
        var widgetMode = new ToolStripMenuItem(L10n.T("menu.widget"))
        {
            Checked = AppSettings.Mode == DisplayMode.TaskbarWidget,
        };
        widgetMode.Click += (_, _) => SwitchMode(DisplayMode.TaskbarWidget);
        var trayMode = new ToolStripMenuItem(L10n.T("menu.tray"))
        {
            Checked = AppSettings.Mode == DisplayMode.TrayIcon,
        };
        trayMode.Click += (_, _) => SwitchMode(DisplayMode.TrayIcon);
        displayMenu.DropDownItems.Add(widgetMode);
        displayMenu.DropDownItems.Add(trayMode);
        menu.Items.Add(displayMenu);

        var placement = new ToolStripMenuItem(L10n.T("menu.placement"));
        placement.Click += (_, _) => OpenSettings("ms-settings:taskbar");
        menu.Items.Add(placement);

        // Цвет хаба: модели в панели, значок, само меню настроек
        var colorMenu = new ToolStripMenuItem(L10n.T("menu.color"));
        var currentColor = AppSettings.GetColorName();
        foreach (var name in AppSettings.Palette)
        {
            var item = new ToolStripMenuItem(L10n.ColorName(name))
            {
                Checked = name == currentColor,
                Image = TrayIconFactory.MakeSwatch(AppSettings.FromName(name)),
            };
            var captured = name;
            item.Click += (_, _) =>
            {
                AppSettings.SetGlobalColor(captured);
                RefreshUi();
                ReplaceMenu();
            };
            colorMenu.DropDownItems.Add(item);
        }

        // произвольный цвет — какой пожелает человек
        var custom = new ToolStripMenuItem(L10n.T("menu.customColor"))
        {
            Checked = AppSettings.IsCustomColor,
            Image = AppSettings.IsCustomColor
                ? TrayIconFactory.MakeSwatch(AppSettings.GetColor())
                : TrayIconFactory.MakeSwatch(Color.FromArgb(120, 130, 140)),
        };
        custom.Click += (_, _) =>
        {
            using var dialog = new ColorDialog
            {
                FullOpen = true,
                Color = AppSettings.GetColor(),
                AnyColor = true,
            };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                AppSettings.SetCustomColor(dialog.Color);
                RefreshUi();
                ReplaceMenu();
            }
        };
        colorMenu.DropDownItems.Add(custom);
        menu.Items.Add(colorMenu);
        menu.Items.Add(new ToolStripSeparator());

        // Язык / Language
        var langMenu = new ToolStripMenuItem(L10n.T("menu.lang"));
        var ruItem = new ToolStripMenuItem("Русский") { Checked = L10n.Lang != "en" };
        ruItem.Click += (_, _) => SwitchLanguage("ru");
        var enItem = new ToolStripMenuItem("English") { Checked = L10n.Lang == "en" };
        enItem.Click += (_, _) => SwitchLanguage("en");
        langMenu.DropDownItems.Add(ruItem);
        langMenu.DropDownItems.Add(enItem);
        menu.Items.Add(langMenu);

        var autostart = new ToolStripMenuItem(L10n.T("menu.autostart"))
        {
            Checked = Autostart.IsEnabled(),
        };
        autostart.Click += (_, _) =>
        {
            Autostart.Set(!Autostart.IsEnabled());
            ReplaceMenu();
        };
        menu.Items.Add(autostart);

        var bluetooth = new ToolStripMenuItem(L10n.T("menu.bt"));
        bluetooth.Click += (_, _) => OpenSettings("ms-settings:bluetooth");
        menu.Items.Add(bluetooth);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("menu.exit"), null, (_, _) => ExitApp());

        // тёмная тема меню с подкраской цветом хаба
        ApplyStyleRecursive(menu, AppSettings.GetColor());
        return menu;
    }

    private static void OpenSettings(string uri)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // настройки недоступны — игнорируем
        }
    }

    private void SwitchLanguage(string lang)
    {
        if (L10n.Lang == lang)
        {
            return;
        }
        AppSettings.SetLanguage(lang);
        ReplaceMenu();
        _flyout?.UpdateItems(_registry.GetSnapshot());
    }

    private void SwitchMode(DisplayMode mode)
    {
        if (AppSettings.Mode == mode)
        {
            return;
        }
        AppSettings.SetMode(mode);
        ApplyMode();
        ReplaceMenu();
        RefreshUi();

        if (mode == DisplayMode.TrayIcon)
        {
            // закрепляем значок на панели задач сразу — возле РУС/ENG
            TrayPromotion.PromoteWithRetry();

            try
            {
                _tray.BalloonTipTitle = "Battery Hub";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.BalloonTipText = L10n.T("balloon.trayhint");
                _tray.ShowBalloonTip(8000);
            }
            catch
            {
            }

            // никаких автооткрытий параметров Windows — куда ставить значок
            // (на панель или в скрытые), человек решает сам через пункт меню
            // «Настройка расположения значка Windows…»
        }
    }

    private void ApplyMode()
    {
        var isWidget = AppSettings.Mode == DisplayMode.TaskbarWidget;
        _widget.SetShown(isWidget);
        _tray.Visible = !isWidget;
        if (isWidget)
        {
            _widget.UpdateItems(_registry.GetSnapshot());
        }
    }

    private void ReplaceMenu()
    {
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        _widget.ContextMenuStrip = _tray.ContextMenuStrip;
        old?.Dispose();
    }

    private void RefreshUi()
    {
        var snapshot = _registry.GetSnapshot();

        // Значок: уровень самого разряженного незаряжающегося устройства
        var candidates = snapshot.Where(d => d.BatteryPct.HasValue && !d.IsCharging)
            .Select(d => d.BatteryPct!.Value).ToList();
        var minPct = candidates.Count > 0 ? candidates.Min() : (int?)null;
        var anyCharging = snapshot.Any(d => d.IsCharging);

        // Панель обновляем только когда она открыта — дешёвая перерисовка
        if (_flyout is { Visible: true })
        {
            _flyout.UpdateItems(snapshot);
        }

        if (AppSettings.Mode == DisplayMode.TrayIcon)
        {
            var bodyColor = AppSettings.GetColorName();
            var state = (minPct, anyCharging, bodyColor);
            if (state != _lastIconState)
            {
                var oldIcon = _icon;
                _icon = TrayIconFactory.CreateCombined(minPct, anyCharging, AppSettings.GetColor());
                _tray.Icon = _icon;
                oldIcon.Dispose();
                _lastIconState = state;
            }

            var tooltip = BuildTooltip(snapshot, limit: 63);
            if (tooltip != _lastTooltip)
            {
                _tray.Text = tooltip;
                _lastTooltip = tooltip;
            }
        }
        else
        {
            // виджет дешёвый — обновляем при каждом изменении
            _widget.UpdateItems(snapshot);
        }

        NotifyLowBattery(snapshot);
    }

    private static string BuildTooltip(IReadOnlyList<DeviceStatus> snapshot, int limit)
    {
        var parts = snapshot
            .Where(d => d.BatteryPct.HasValue)
            .Select(d =>
            {
                var eta = d.EtaHours.HasValue ? " " + BatteryEstimator.FormatEta(d.EtaHours) : "";
                var charging = d.IsCharging ? " ⚡" : "";
                return $"{d.Name}: {d.BatteryPct}%{charging}{eta}";
            })
            .ToList();
        var text = parts.Count > 0
            ? "Battery Hub\n" + string.Join(" · ", parts)
            : "Battery Hub\n" + L10n.T("tooltip.empty");
        if (text.Length > limit)
        {
            text = text[..(limit - 1)] + "…";
        }
        return text;
    }

    private void NotifyLowBattery(IReadOnlyList<DeviceStatus> snapshot)
    {
        foreach (var device in snapshot)
        {
            if (!device.BatteryPct.HasValue || device.IsCharging)
            {
                continue;
            }
            var pct = device.BatteryPct.Value;
            foreach (var threshold in LowBatteryThresholds)
            {
                var key = (device.Id, threshold);
                if (_notified.TryGetValue(key, out var done) && done)
                {
                    if (pct > threshold + 5)
                    {
                        _notified[key] = false;
                    }
                    continue;
                }
                if (pct <= threshold)
                {
                    _notified[key] = true;
                    try
                    {
                        _tray.BalloonTipTitle = "Battery Hub";
                        _tray.BalloonTipIcon = ToolTipIcon.Warning;
                        _tray.BalloonTipText = L10n.F("balloon.low", device.Name, pct);
                        _tray.ShowBalloonTip(5000);
                    }
                    catch
                    {
                        // скрытый значок не показывает balloon — в виджете точка краснеет
                    }
                }
            }
        }
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        var menu = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = null;
        _tray.Dispose();
        menu?.Dispose();
        _widget.Visible = false;
        _widget.Dispose();
        _pokeRegistration.Unregister(null);
        _pokeEvent.Dispose();
        _bluetooth.Dispose();
        _x11.Dispose();
        _registry.Flush();
        _icon.Dispose();
        _flyout?.Dispose();
        _uiAnchor.Dispose();
        Application.Exit();
    }
}
