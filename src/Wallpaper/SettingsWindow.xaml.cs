using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wallpaper;

public partial class SettingsWindow : Window
{
    /// <summary>ponytail: столько плиток рисуем максимум. Дальше — фильтр по имени.
    /// WrapPanel не виртуализируется, а 600 плиток строятся мгновенно и занимают ~50 МБ.</summary>
    const int TileCap = 600;

    static readonly (string Key, string Title)[] HotkeyActions =
    [
        ("next", "Следующая"),
        ("prev", "Предыдущая"),
        ("nextAll", "Следующая на всех мониторах"),
        ("togglePause", "Пауза слайд-шоу (на мониторе под курсором)"),
        ("quickPicker", "Быстрый выбор"),
        ("toggleSound", "Звук видео вкл/выкл"),
        ("toggleWallpapers", "Обои вкл/выкл"),
        ("revealCurrent", "Показать в проводнике"),
        ("playlist1", "Плейлист 1"),
        ("playlist2", "Плейлист 2"),
        ("playlist3", "Плейлист 3"),
        ("quit", "Выход из приложения"),
        ("deleteCurrent", "Удалить картинку (в корзину)"),
        ("hideCurrent", "Спрятать картинку — «(-)» в начало имени"),
    ];

    /// <summary>Пункт списка, который помнит своё значение и умеет показываться человеку.</summary>
    sealed record Opt(string Text, object? Value)
    {
        public override string ToString() => Text;
    }

    bool _loading;
    string? _monId;
    string? _plId;
    List<Desktop.Display> _mons = [];
    CancellationTokenSource? _tileLoad;
    readonly DispatcherTimer _filterDebounce;
    readonly DispatcherTimer _placeSave;

    static Config Cfg => Program.Cfg;
    Playlist? Pl => Cfg.Playlist(_plId);

    public SettingsWindow()
    {
        InitializeComponent();
        Ui.ApplyPalette(this);
        SourceInitialized += (_, _) => Ui.SyncTitleBar(this);

        _filterDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { _filterDebounce!.Stop(); BuildTiles(); }, Dispatcher);
        _filterDebounce.Stop();

        // Рамку пишем не только при закрытии: если приложение снимут через диспетчер задач,
        // положение окна всё равно останется тем, что человек выставил руками.
        _placeSave = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background,
            (_, _) => { _placeSave!.Stop(); SavePlace(); }, Dispatcher);
        _placeSave.Stop();

        RestorePlace();
        LocationChanged += (_, _) => BumpPlaceSave();
        SizeChanged += (_, _) => BumpPlaceSave();
        StateChanged += (_, _) => BumpPlaceSave();
        Closing += (_, _) => { _placeSave.Stop(); SavePlace(); };

        Wire();
        Program.ConfigReloaded += Refresh;
        // Файлы в папке появились — сетка обязана это показать сама, без переоткрытия окна.
        Program.Engine.Rescanned += BuildTiles;
        Closed += (_, _) =>
        {
            Program.ConfigReloaded -= Refresh;
            Program.Engine.Rescanned -= BuildTiles;
            _tileLoad?.Cancel();
            Program.ResumeHotkeys();
        };

        Refresh();
    }

    // ================= рамка окна =================

    void BumpPlaceSave()
    {
        if (!IsLoaded) return;
        _placeSave.Stop();
        _placeSave.Start();
    }

    /// <summary>
    /// Открыть окно там же и такого же размера, как закрыли. Сохранённую рамку проверяем
    /// на пересечение с рабочей областью: монитор могли отключить, и окно уехало бы
    /// за пределы видимого, откуда его уже не достать мышью.
    /// </summary>
    void RestorePlace()
    {
        var p = Program.State.SettingsPlace;
        if (p is null || p.W < MinWidth || p.H < MinHeight) return;

        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth;
        double bottom = top + SystemParameters.VirtualScreenHeight;
        bool visible = p.X + p.W > left + 80 && p.X < right - 80
                    && p.Y + p.H > top + 40 && p.Y < bottom - 40;
        if (!visible) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.X;
        Top = p.Y;
        Width = p.W;
        Height = p.H;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    void SavePlace()
    {
        // У развёрнутого окна RestoreBounds — рамка обычного состояния, её и запоминаем.
        var r = RestoreBounds;
        if (r.Width < MinWidth || r.Height < MinHeight) return;

        Program.State.SettingsPlace = new State.WindowPlace
        {
            X = r.Left,
            Y = r.Top,
            W = r.Width,
            H = r.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        Program.State.Save();
    }

    // ================= проводка =================

    void Wire()
    {
        MonCanvas.SizeChanged += (_, _) => BuildMonitorMap();

        MonLabel.LostFocus += (_, _) => Edit(m => m.Label = string.IsNullOrWhiteSpace(MonLabel.Text) ? null : MonLabel.Text.Trim());
        ModeOff.Checked += (_, _) => Edit(m => m.Mode = Mode.Off);
        ModeStatic.Checked += (_, _) => Edit(m => m.Mode = Mode.Static);
        ModePlaylist.Checked += (_, _) => Edit(m => m.Mode = Mode.Playlist);
        ModeVideo.Checked += (_, _) => Edit(m => m.Mode = Mode.Video);
        MonVideoBrowse.Click += (_, _) => BrowseVideo();
        MonVideoSound.Checked += (_, _) => Edit(m => (m.Video ??= new VideoCfg()).Volume = 100);
        MonVideoSound.Unchecked += (_, _) => Edit(m => (m.Video ??= new VideoCfg()).Volume = 0);
        MonPlaylist.SelectionChanged += (_, _) => Edit(m => m.PlaylistId = (MonPlaylist.SelectedItem as Opt)?.Value as string);
        MonBrowse.Click += (_, _) => BrowseStatic();

        FitBox.SelectionChanged += (_, _) =>
        {
            if (_loading || FitBox.SelectedItem is not Opt o || o.Value is not FitMode f) return;
            Cfg.Fit = f;
            Program.ApplyConfig();
        };
        BgColor.LostFocus += (_, _) => SaveBgColor();
        BgMode.SelectionChanged += (_, _) =>
        {
            if (_loading || BgMode.SelectedItem is not Opt o || o.Value is not BackgroundMode m) return;
            Cfg.Background = m;
            Program.ApplyConfig();
            Program.Engine.Repaint();   // сменился способ показа, а не файл — Sync сам ничего не перевыставит
            ShowBgMode();
        };

        PlList.SelectionChanged += (_, _) => SelectPlaylist((PlList.SelectedItem as Opt)?.Value as string);
        PlAdd.Click += (_, _) => AddPlaylist();
        PlDel.Click += (_, _) => DeletePlaylist();
        PlName.LostFocus += (_, _) => EditPl(p => p.Name = string.IsNullOrWhiteSpace(PlName.Text) ? p.Name : PlName.Text.Trim());
        PlInterval.LostFocus += (_, _) => EditPl(p =>
        {
            if (int.TryParse(PlInterval.Text.Trim(), out int s) && s >= 5) p.IntervalSeconds = s;
        });
        PlOrder.SelectionChanged += (_, _) =>
        {
            if (PlOrder.SelectedItem is Opt o && o.Value is OrderMode m) EditPl(p => p.Order = m);
        };
        PlFilter.TextChanged += (_, _) => { _filterDebounce.Stop(); _filterDebounce.Start(); };

        SrcAddFolder.Click += (_, _) => AddFolders();
        SrcAddFiles.Click += (_, _) => AddFiles();
        SrcDel.Click += (_, _) => RemoveSource();
        SrcRefresh.Click += (_, _) => Program.Engine.Refresh();

        TilesHost.Drop += OnDrop;
        TilesHost.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };

        foreach (var (box, set) in PauseBoxes())
        {
            var apply = set;
            box.Checked += (_, _) => { if (!_loading) { apply(true); Program.ApplyConfig(); } };
            box.Unchecked += (_, _) => { if (!_loading) { apply(false); Program.ApplyConfig(); } };
        }

        SchedAdd.Click += (_, _) => AddScheduleRule();
        SchedDel.Click += (_, _) => RemoveScheduleRule();

        Autostart.Checked += (_, _) => SaveAutostart(true);
        Autostart.Unchecked += (_, _) => SaveAutostart(false);

        ZoomPercent.LostFocus += (_, _) =>
            SaveNumber(ZoomPercent, 10, 100, v => Cfg.PickerZoomPercent = v, () => Cfg.PickerZoomPercent);
        ZoomAlpha.LostFocus += (_, _) =>
            SaveNumber(ZoomAlpha, 0, 90, v => Cfg.PickerZoomTransparency = v, () => Cfg.PickerZoomTransparency);
        EnabledBox.Checked += (_, _) => { if (!_loading && !Cfg.Enabled) Program.ToggleWallpapers(); };
        EnabledBox.Unchecked += (_, _) => { if (!_loading && Cfg.Enabled) Program.ToggleWallpapers(); };

        OpenCfg.Click += (_, _) => Ui.Open(Paths.Config);
        OpenLog.Click += (_, _) => Ui.Open(File.Exists(Paths.Log) ? Paths.Log : Paths.Dir);
        OpenDir.Click += (_, _) => Ui.Open(Paths.Dir);
    }

    // ================= полная перерисовка =================

    public void Refresh()
    {
        _loading = true;
        try
        {
            BuildMonitorMap();
            BuildFitBox();
            BuildPlaylistList();
            BuildHotkeys();
            Autostart.IsChecked = Cfg.StartWithWindows;
            EnabledBox.IsChecked = Cfg.Enabled;
            LoadPauseRules();
            LoadVideoStatus();
            LoadSchedule();
            ZoomPercent.Text = Cfg.PickerZoomPercent.ToString();
            ZoomAlpha.Text = Cfg.PickerZoomTransparency.ToString();
            BgColor.Text = Cfg.BackgroundColor ?? "";
            BuildBgModeBox();
            UpdateSwatch();
            var v = typeof(SettingsWindow).Assembly.GetName().Version;
            AboutLine.Text = $"Wallpaper v{v?.Major}.{v?.Minor} · {Paths.Dir}";
        }
        finally { _loading = false; }

        BuildTiles();
    }

    // ================= мониторы =================

    void BuildMonitorMap()
    {
        MonCanvas.Children.Clear();
        _mons = Desktop.Monitors();
        if (_mons.Count == 0)
        {
            MonTitle.Text = "Мониторов не видно";
            return;
        }
        if (_monId is null || _mons.All(m => m.Id != _monId)) _monId = (_mons.FirstOrDefault(m => m.Primary) ?? _mons[0]).Id;

        double cw = MonCanvas.ActualWidth, ch = MonCanvas.Height;
        if (cw < 50) return;   // ещё не разложились, придём из SizeChanged

        int minX = _mons.Min(m => m.X), minY = _mons.Min(m => m.Y);
        double w = _mons.Max(m => m.X + m.W) - minX, h = _mons.Max(m => m.Y + m.H) - minY;
        double k = Math.Min((cw - 24) / w, (ch - 24) / h);
        double ox = (cw - w * k) / 2, oy = (ch - h * k) / 2;

        for (int i = 0; i < _mons.Count; i++)
        {
            var m = _mons[i];
            bool sel = m.Id == _monId;

            var box = new Border
            {
                Width = Math.Max(40, m.W * k - 6),
                Height = Math.Max(30, m.H * k - 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(sel ? 2 : 1),
                BorderBrush = (Brush)Resources[sel ? "Accent" : "Line"],
                Background = (Brush)Resources[sel ? "AccentSoft" : "Surface2"],
                Cursor = Cursors.Hand,
                Tag = m.Id,
                Child = new TextBlock
                {
                    Text = $"{MonitorName(m, i)}\n{m.W}×{m.H}",
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = (Brush)Resources[sel ? "Accent" : "Muted"],
                },
            };
            box.MouseLeftButtonUp += (s, _) => { _monId = (string)((Border)s).Tag; BuildMonitorMap(); LoadMonitor(); };

            Canvas.SetLeft(box, ox + (m.X - minX) * k + 3);
            Canvas.SetTop(box, oy + (m.Y - minY) * k + 3);
            MonCanvas.Children.Add(box);
        }

        LoadMonitor();
    }

    static string MonitorName(Desktop.Display m, int index)
    {
        var custom = Cfg.Monitors.GetValueOrDefault(m.Id)?.Label;
        return string.IsNullOrWhiteSpace(custom)
            ? $"Монитор {index + 1}{(m.Primary ? " ★" : "")}"
            : custom!;
    }

    void LoadMonitor()
    {
        if (_monId is null) return;
        bool outer = _loading;
        _loading = true;
        try
        {
            var m = _mons.First(x => x.Id == _monId);
            var mc = MonCfg();

            MonTitle.Text = $"{MonitorName(m, _mons.IndexOf(m))} — {m.W}×{m.H} @ {m.X},{m.Y}";
            MonLabel.Text = mc.Label ?? "";
            ModeOff.IsChecked = mc.Mode == Mode.Off;
            ModeStatic.IsChecked = mc.Mode == Mode.Static;
            ModePlaylist.IsChecked = mc.Mode == Mode.Playlist;
            MonPath.Text = mc.Path ?? "";

            MonPlaylist.ItemsSource = Cfg.Playlists.Select(p => new Opt(p.Name, p.Id)).ToList();
            MonPlaylist.SelectedItem = (MonPlaylist.ItemsSource as List<Opt>)?.FirstOrDefault(o => (string?)o.Value == mc.PlaylistId);

            ModeVideo.IsChecked = mc.Mode == Mode.Video;
            MonVideo.Text = mc.Video?.Path ?? "";
            MonVideoSound.IsChecked = (mc.Video?.Volume ?? 0) > 0;

            RowPlaylist.Visibility = mc.Mode == Mode.Playlist ? Visibility.Visible : Visibility.Collapsed;
            RowStatic.Visibility = mc.Mode == Mode.Static ? Visibility.Visible : Visibility.Collapsed;
            RowVideo.Visibility = mc.Mode == Mode.Video ? Visibility.Visible : Visibility.Collapsed;

            var note = mc.Mode == Mode.Video ? VideoProblem() : null;
            VideoNote.Text = note ?? "";
            VideoNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _loading = outer; }
    }

    MonitorCfg MonCfg() =>
        Cfg.Monitors.TryGetValue(_monId!, out var mc) ? mc : Cfg.Monitors[_monId!] = new MonitorCfg();

    void Edit(Action<MonitorCfg> change)
    {
        if (_loading || _monId is null) return;
        change(MonCfg());
        Program.ApplyConfig();
        LoadMonitor();
        BuildMonitorMap();
    }

    void BrowseStatic()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Картинка на рабочий стол",
            Filter = PlaylistEngine.ImageFilter,
        };
        if (d.ShowDialog() != true) return;
        Edit(m => { m.Mode = Mode.Static; m.Path = d.FileName; });
    }

    void BrowseVideo()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Видео или веб-страница на рабочий стол",
            Filter = "Видео, анимация, веб|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.m4v;*.wmv;*.gif;*.apng;*.webp;*.html;*.htm|Все файлы|*.*",
        };
        if (d.ShowDialog() != true) return;
        Edit(m =>
        {
            m.Mode = Mode.Video;
            m.Video = new VideoCfg { Path = d.FileName, Volume = m.Video?.Volume ?? 0 };
        });
    }

    /// <summary>Что мешает видео прямо сейчас. null — всё в порядке.</summary>
    static string? VideoProblem()
    {
        if (!Mpv.Available && Program.Video.ActiveCount == 0)
            return $"Нужна libmpv-2.dll. Положи её рядом с Wallpaper.exe, в {Paths.Dir} "
                 + "или пропиши путь в config.json → mpvPath.";
        return Program.Video.Note;
    }

    (CheckBox Box, Action<bool> Set)[] PauseBoxes() =>
    [
        (PauseFullscreen, v => Cfg.PauseRules.FullscreenApp = v),
        (PauseCovers,     v => Cfg.PauseRules.ForegroundCoversMonitor = v),
        (PauseBattery,    v => Cfg.PauseRules.OnBattery = v),
        (PauseLock,       v => Cfg.PauseRules.LockScreen = v),
        (PauseRemote,     v => Cfg.PauseRules.RemoteSession = v),
    ];

    void LoadPauseRules()
    {
        var r = Cfg.PauseRules;
        PauseFullscreen.IsChecked = r.FullscreenApp;
        PauseCovers.IsChecked = r.ForegroundCoversMonitor;
        PauseBattery.IsChecked = r.OnBattery;
        PauseLock.IsChecked = r.LockScreen;
        PauseRemote.IsChecked = r.RemoteSession;
    }

    void LoadSchedule()
    {
        SchedList.ItemsSource = Cfg.Schedule
            .OrderBy(r => r.From, StringComparer.Ordinal)
            .Select(r => new Opt($"{r.From}   →   {Cfg.Playlist(r.PlaylistId)?.Name ?? "плейлист удалён"}", r))
            .ToList();

        var opts = Cfg.Playlists.Select(p => new Opt(p.Name, p.Id)).ToList();
        SchedPlaylist.ItemsSource = opts;
        SchedPlaylist.SelectedItem = opts.FirstOrDefault();
        SchedAdd.IsEnabled = opts.Count > 0;
    }

    void AddScheduleRule()
    {
        var raw = SchedTime.Text.Trim();
        if (!TimeOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var t))
        {
            SchedTime.Text = "";
            SchedTime.Focus();
            return;
        }
        if (SchedPlaylist.SelectedItem is not Opt o || o.Value is not string plId) return;

        var from = t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Cfg.Schedule.RemoveAll(r => r.From == from);          // одно правило на момент времени
        Cfg.Schedule.Add(new ScheduleRule { From = from, PlaylistId = plId });

        SchedTime.Text = "";
        Program.ApplyConfig();
        LoadSchedule();
    }

    void RemoveScheduleRule()
    {
        if (SchedList.SelectedItem is not Opt o || o.Value is not ScheduleRule r) return;
        Cfg.Schedule.Remove(r);
        Program.ApplyConfig();
        LoadSchedule();
    }

    void LoadVideoStatus()
    {
        var v = Program.Video;
        var lines = new List<string>
        {
            Mpv.Available ? $"libmpv: {Mpv.LoadedFrom}" : "libmpv не найдена — видео недоступно",
            WebLayer.RuntimeVersion() is string wv ? $"WebView2: {wv}" : "WebView2 не установлен — веб-обои недоступны",
        };
        if (v.ActiveCount > 0)
        {
            lines.Add($"играет мониторов: {v.ActiveCount}"
                    + (v.BehindIcons ? ", видео под иконками" : ", видео поверх иконок"));
            if (v.PauseReason.Length > 0) lines.Add($"сейчас на паузе: {v.PauseReason}");
        }
        if (v.Note is string n) lines.Add(n);
        VideoStatus.Text = string.Join("   ·   ", lines);
    }

    void BuildFitBox()
    {
        var opts = new List<Opt>
        {
            new("Заполнить", FitMode.Fill),
            new("Вписать", FitMode.Fit),
            new("Растянуть", FitMode.Stretch),
            new("По центру", FitMode.Center),
            new("Замостить", FitMode.Tile),
            new("Одна на все мониторы", FitMode.Span),
        };
        FitBox.ItemsSource = opts;
        FitBox.SelectedItem = opts.First(o => (FitMode)o.Value! == Cfg.Fit);
    }

    /// <summary>
    /// Прочитать число из поля, зажать в границы и применить. Мусор молча откатываем
    /// к прежнему значению: ругаться модальным окном на опечатку — перебор.
    /// </summary>
    void SaveNumber(TextBox box, int min, int max, Action<int> apply, Func<int> read)
    {
        if (_loading) return;

        if (int.TryParse(box.Text.Trim(), out int v))
        {
            apply(Math.Clamp(v, min, max));
            Program.ApplyConfig();
        }
        box.Text = read().ToString();   // мусор и выход за границы молча откатываем
    }

    void BuildBgModeBox()
    {
        var opts = new List<Opt>
        {
            new("Указать цвет", BackgroundMode.Color),
            new("Автоматически", BackgroundMode.Average),
            new("Размытие", BackgroundMode.Blur),
        };
        BgMode.ItemsSource = opts;
        BgMode.SelectedItem = opts.First(o => (BackgroundMode)o.Value! == Cfg.Background);
        ShowBgMode();
    }

    /// <summary>Поле цвета имеет смысл только в первом режиме — в остальных оно гаснет.</summary>
    void ShowBgMode()
    {
        bool manual = Cfg.Background == BackgroundMode.Color;
        BgColor.IsEnabled = manual;
        BgSwatch.Opacity = manual ? 1 : 0.35;

        BgHint.Text = Cfg.Background switch
        {
            BackgroundMode.Average => "Цвет полей подбирается как средний по картинке. Windows держит один цвет на всю систему, поэтому на разных мониторах он будет от той картинки, что сменилась последней.",
            BackgroundMode.Blur => "Вместо полей — сильно размытая копия самой картинки во весь монитор, поверх неё картинка целиком. Собранные подложки лежат в кэше, в %TEMP%\\Wallpaper\\backdrop.",
            _ => "Пусто — не трогать цвет, заданный в Windows.",
        };
    }

    void SaveBgColor()
    {
        if (_loading) return;
        var t = BgColor.Text.Trim();
        if (t.Length == 0) { Cfg.BackgroundColor = null; }
        else if (Desktop.TryParseColor(t, out _)) { Cfg.BackgroundColor = t.StartsWith('#') ? t : "#" + t; }
        else { BgColor.Text = Cfg.BackgroundColor ?? ""; return; }   // мусор — молча вернуть прежнее
        UpdateSwatch();
        Program.ApplyConfig();
        Program.Engine.Repaint();
    }

    void UpdateSwatch()
    {
        try
        {
            BgSwatch.Background = string.IsNullOrWhiteSpace(Cfg.BackgroundColor)
                ? (Brush)Resources["Surface2"]
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(Cfg.BackgroundColor)!);
        }
        catch { BgSwatch.Background = (Brush)Resources["Surface2"]; }
    }

    // ================= плейлисты =================

    void BuildPlaylistList()
    {
        var opts = Cfg.Playlists.Select(p => new Opt(p.Name, p.Id)).ToList();
        PlList.ItemsSource = opts;
        if (_plId is null || Cfg.Playlist(_plId) is null) _plId = Cfg.Playlists.FirstOrDefault()?.Id;
        PlList.SelectedItem = opts.FirstOrDefault(o => (string?)o.Value == _plId);
        LoadPlaylist();
    }

    void SelectPlaylist(string? id)
    {
        if (_loading || id is null || id == _plId) return;
        _plId = id;
        LoadPlaylist();
        BuildTiles();
    }

    void LoadPlaylist()
    {
        bool outer = _loading;
        _loading = true;
        try
        {
            var pl = Pl;
            PlPane.IsEnabled = pl is not null;
            if (pl is null) { PlName.Text = ""; PlSources.Items.Clear(); PlCount.Text = ""; return; }

            PlName.Text = pl.Name;
            PlInterval.Text = pl.IntervalSeconds.ToString();

            var orders = new List<Opt> { new("Вперемешку", OrderMode.Shuffle), new("По порядку", OrderMode.Sequential) };
            PlOrder.ItemsSource = orders;
            PlOrder.SelectedItem = orders.First(o => (OrderMode)o.Value! == pl.Order);

            BuildSources(pl);
        }
        finally { _loading = outer; }
    }

    /// <summary>
    /// Строки списка источников. Собираем вручную, а не через ItemsSource: у папки в строке
    /// живёт галка «с подпапками», а у файла её нет.
    /// </summary>
    void BuildSources(Playlist pl)
    {
        PlSources.Items.Clear();

        foreach (var f in pl.Folders)
        {
            var fr = f;
            var box = new CheckBox
            {
                Content = "с подпапками",
                IsChecked = fr.Recursive,
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "Брать картинки и из вложенных папок",
            };
            box.Checked += (_, _) => SetRecursive(fr, true);
            box.Unchecked += (_, _) => SetRecursive(fr, false);

            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock
            {
                Text = "\U0001F4C1  " + fr.Path,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                ToolTip = fr.Path,
            });

            PlSources.Items.Add(new ListBoxItem
            {
                Content = row,
                Tag = fr,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }

        foreach (var f in pl.Files)
            PlSources.Items.Add(new ListBoxItem { Content = "\U0001F4C4  " + f, Tag = f });
    }

    void SetRecursive(FolderRef f, bool on)
    {
        if (_loading || f.Recursive == on) return;
        f.Recursive = on;
        Program.ApplyConfig();   // пересобирает и наблюдателей за папкой, и список файлов
        BuildTiles();
    }

    void EditPl(Action<Playlist> change)
    {
        if (_loading || Pl is null) return;
        change(Pl);
        Program.ApplyConfig();
        BuildPlaylistList();
        BuildTiles();
    }

    void AddPlaylist()
    {
        var id = UniqueId("Новый плейлист");
        Cfg.Playlists.Add(new Playlist { Id = id, Name = "Новый плейлист" });
        _plId = id;
        Program.ApplyConfig();
        BuildPlaylistList();
        BuildTiles();
        PlName.Focus();
        PlName.SelectAll();
    }

    void DeletePlaylist()
    {
        var pl = Pl;
        if (pl is null) return;
        if (MessageBox.Show($"Удалить плейлист «{pl.Name}»?\nСами картинки останутся на диске.",
                "Wallpaper", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        Cfg.Playlists.Remove(pl);
        foreach (var mc in Cfg.Monitors.Values.Where(m => m.PlaylistId == pl.Id))
        {
            mc.PlaylistId = null;
            mc.Mode = Mode.Off;
        }
        _plId = null;
        Program.ApplyConfig();
        Refresh();
    }

    static string UniqueId(string name)
    {
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "playlist";
        var id = slug;
        for (int i = 2; Cfg.Playlists.Any(p => p.Id == id); i++) id = $"{slug}-{i}";
        return id;
    }

    void AddFolders()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Папки с обоями", Multiselect = true };
        if (d.ShowDialog() != true) return;
        AddSources(d.FolderNames);
    }

    void AddFiles()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Картинки",
            Multiselect = true,
            Filter = PlaylistEngine.ImageFilter,
        };
        if (d.ShowDialog() != true) return;
        AddSources(d.FileNames);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddSources(paths);
        e.Handled = true;
    }

    /// <summary>Папки идут в folders, файлы — в files. Дубликаты молча пропускаем.</summary>
    void AddSources(IEnumerable<string> paths)
    {
        var pl = Pl;
        if (pl is null)
        {
            AddPlaylist();
            pl = Pl;
            if (pl is null) return;
        }

        bool any = false;
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                if (pl.Folders.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                pl.Folders.Add(new FolderRef { Path = p, Recursive = true });
                any = true;
            }
            else if (File.Exists(p))
            {
                if (pl.Files.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase))) continue;
                pl.Files.Add(p);
                any = true;
            }
        }
        if (!any) return;

        Program.ApplyConfig();
        LoadPlaylist();
        BuildTiles();
    }

    void RemoveSource()
    {
        var pl = Pl;
        if (pl is null || PlSources.SelectedItem is not ListBoxItem row) return;

        if (row.Tag is FolderRef fr) pl.Folders.Remove(fr);
        else if (row.Tag is string f) pl.Files.Remove(f);
        else return;

        Program.ApplyConfig();
        LoadPlaylist();
        BuildTiles();
    }

    // ================= плитки =================

    void BuildTiles()
    {
        _tileLoad?.Cancel();
        Tiles.Children.Clear();

        var pl = Pl;
        if (pl is null) { PlCount.Text = ""; return; }

        // Здесь берём All, а не Order: выключенные файлы тоже надо показать, иначе их негде включить.
        var all = Program.Engine.All(pl.Id);
        var off = new HashSet<string>(pl.Excluded, StringComparer.OrdinalIgnoreCase);
        var filter = PlFilter.Text.Trim();
        var shown = filter.Length == 0
            ? all
            : all.Where(p => Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        int offShown = off.Count == 0 ? 0 : shown.Count(off.Contains);
        PlCount.Text = (shown.Length > TileCap
            ? $"показано {TileCap} из {shown.Length} — уточни фильтр"
            : $"{shown.Length} файлов")
            + (offShown > 0 ? $", выключено {offShown}" : "");

        var cts = _tileLoad = new CancellationTokenSource();
        foreach (var path in shown.Take(TileCap))
        {
            var img = new Image
            {
                Stretch = Stretch.UniformToFill,
                Source = Thumbs.Cached(path),
            };
            var tile = new Border
            {
                Width = 154,
                Height = 92,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Resources["Line"],
                Background = (Brush)Resources["Surface2"],
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Child = img,
            };
            var p = path;
            bool disabled = off.Contains(p);

            // Выключенный файл гасим, но оставляем на месте: так видно, что он никуда не делся.
            tile.Opacity = disabled ? 0.3 : 1.0;
            if (disabled) tile.BorderBrush = (Brush)Resources["Danger"];
            tile.ToolTip = disabled
                ? $"{Path.GetFileName(p)}\nВыключен — в ротацию не идёт.\nПравый клик, чтобы включить обратно"
                : $"{Path.GetFileName(p)}\nКлик — показать сейчас, правый клик — меню";

            tile.ContextMenu = Ui.TileMenu(p, disabled, () => PreviewNow(p), () => SetExcluded(p, !disabled));
            tile.MouseLeftButtonUp += (_, _) => PreviewNow(p);
            Tiles.Children.Add(tile);

            if (img.Source is null) _ = FillAsync(img, p, cts.Token);
        }
    }

    async Task FillAsync(Image target, string path, CancellationToken ct)
    {
        var bmp = await Thumbs.GetAsync(path, ct);
        if (bmp is null || ct.IsCancellationRequested) return;
        target.Source = bmp;
    }

    /// <summary>
    /// Выключить файл из ротации, не убирая его с диска и из папки. Если он сейчас на экране,
    /// ApplyConfig увидит, что курсор указывает в никуда, и тут же покажет следующий.
    /// </summary>
    void SetExcluded(string path, bool off)
    {
        var pl = Pl;
        if (pl is null) return;

        pl.Excluded.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        if (off) pl.Excluded.Add(path);

        Program.ApplyConfig();
        BuildTiles();
    }

    /// <summary>
    /// Показать картинку прямо сейчас, не трогая настройки. Тем же путём, что и слайд-шоу:
    /// прямая установка обоев пропускала заполнение полей, не умела видео и страницы
    /// и сносила видеослой на соседнем мониторе.
    /// </summary>
    void PreviewNow(string path)
    {
        if (_monId is null) return;
        Program.Engine.ShowNow(_monId, path);
    }

    // ================= общее =================

    void BuildHotkeys()
    {
        HotkeyRows.Children.Clear();
        foreach (var (key, title) in HotkeyActions)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool on = Cfg.HotkeyEnabled(key);

            var label = new TextBlock { Text = title, Foreground = (Brush)Resources["Ink"] };
            var box = new TextBox
            {
                Width = 220,
                IsReadOnly = true,
                IsEnabled = on,
                Text = Cfg.Hotkeys.GetValueOrDefault(key, ""),
                Tag = key,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            // Галка гасит комбинацию, но не стирает её: вернуть обратно — один клик.
            var keyRef = key;
            var boxRef = box;
            var toggle = new CheckBox
            {
                IsChecked = on,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Снять галку — комбинация перестанет работать, но останется записанной",
            };
            toggle.Checked += (_, _) => SetHotkeyEnabled(keyRef, true, boxRef);
            toggle.Unchecked += (_, _) => SetHotkeyEnabled(keyRef, false, boxRef);
            // Пока поле ловит комбинацию, глобальные клавиши сняты: иначе нажатая занятая
            // комбинация выполнится, а не запишется. Возвращаем их, когда фокус ушёл.
            box.GotKeyboardFocus += (s, _) =>
            {
                ((TextBox)s).Background = (Brush)Resources["AccentSoft"];
                Program.SuspendHotkeys();
            };
            box.LostKeyboardFocus += (s, _) =>
            {
                ((TextBox)s).Background = (Brush)Resources["Surface"];
                ShowHotkeyFailures(Program.RebindHotkeys());
            };
            box.PreviewKeyDown += HotkeyCapture;

            Grid.SetColumn(toggle, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(box, 2);
            row.Children.Add(toggle);
            row.Children.Add(label);
            row.Children.Add(box);
            HotkeyRows.Children.Add(row);
        }
        ShowHotkeyFailures(Program.HotkeyFailures);
    }

    void SetHotkeyEnabled(string action, bool on, TextBox box)
    {
        if (_loading) return;

        Cfg.HotkeysOff.RemoveAll(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));
        if (!on) Cfg.HotkeysOff.Add(action);

        box.IsEnabled = on;
        ShowHotkeyFailures(Program.RebindHotkeys());
    }

    void HotkeyCapture(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        var action = (string)box.Tag;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete)
        {
            // Пустая строка, а не удаление ключа: пропавшую клавишу FillHotkeyDefaults
            // при следующем запуске молча вернул бы к значению по умолчанию.
            box.Text = "";
            Cfg.Hotkeys[action] = "";
            Cfg.Save();
            return;
        }
        if (key == Key.Tab || key == Key.Escape) { Keyboard.ClearFocus(); return; }

        var combo = Hotkeys.Format(Keyboard.Modifiers, key);
        if (combo.Length == 0) return;              // пока нажаты одни модификаторы

        // Регистрируем, когда фокус уйдёт из поля: пока он здесь, клавиши сняты.
        box.Text = combo;
        Cfg.Hotkeys[action] = combo;
        Cfg.Save();
    }

    void ShowHotkeyFailures(IReadOnlyList<string> failed)
    {
        HotkeyWarn.Visibility = failed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HotkeyWarn.Text = failed.Count == 0 ? "" : "Не удалось занять: " + string.Join(", ", failed);
    }

    void SaveAutostart(bool on)
    {
        if (_loading) return;
        Cfg.StartWithWindows = on;
        Config.AutostartEnabled = on;
        Cfg.Save();
    }

}
