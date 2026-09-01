using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Windows.Threading;

namespace Wallpaper;

public static class Program
{
    static Config _cfg = null!;
    static State _state = null!;
    static PlaylistEngine _engine = null!;
    static NotifyIcon _tray = null!;
    static ContextMenuStrip _menu = null!;
    static Hotkeys _hotkeys = null!;
    static FileSystemWatcher _cfgWatcher = null!;
    static Dispatcher _ui = null!;
    static SettingsWindow? _settings;
    static QuickPicker? _picker;
    static VideoLayer _video = null!;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    const int HWND_BROADCAST = 0xFFFF;

    /// <summary>Второй запуск просит первый показать настройки — это полезнее окна «уже запущен».</summary>
    static readonly uint ShowSettingsMsg = RegisterWindowMessageW("WallpaperShowSettings");

    /// <summary>Корректный выход снаружи: обои успевают вернуться на место, в отличие от убийства процесса.</summary>
    static readonly uint QuitMsg = RegisterWindowMessageW("WallpaperQuit");

    static System.Windows.Interop.HwndSource? _appMessages;

    // Окна работают с теми же объектами, что и трей: приложение на шесть файлов,
    // прослойка ради прослойки тут ничего бы не улучшила.
    internal static Config Cfg => _cfg;
    internal static State State => _state;
    internal static PlaylistEngine Engine => _engine;
    internal static IReadOnlyList<string> HotkeyFailures => _hotkeys.Failed;
    internal static VideoLayer Video => _video;

    /// <summary>config.json перечитали снаружи — открытому окну настроек пора обновиться.</summary>
    internal static event Action? ConfigReloaded;

    [STAThread]
    public static int Main(string[] args)
    {
        // До создания любого окна: иначе Windows начнёт виртуализировать координаты,
        // и окна с видео поедут на мониторах с нестандартным масштабом.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // PER_MONITOR_AWARE_V2

        if (args.Contains("--selftest")) return Cli.SelfTest();
        if (args.Contains("--workerw")) return Cli.WorkerW();
        if (args.Contains("--videosweep")) return Cli.VideoSweep(args);
        if (args.Contains("--monitors")) return Cli.Monitors();

        if (args.Contains("--quit"))
        {
            PostMessageW(new IntPtr(HWND_BROADCAST), QuitMsg, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\Wallpaper.SingleInstance", out bool first);
        if (!first)
        {
            PostMessageW(new IntPtr(HWND_BROADCAST), ShowSettingsMsg, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Трей-приложение падает молча: окна нет, консоли нет. Без этого причина
        // остаётся только в журнале Windows, где от неё толку мало.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Paths.Write("НЕОБРАБОТАННОЕ ИСКЛЮЧЕНИЕ: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Paths.Write("ИСКЛЮЧЕНИЕ В ЗАДАЧЕ: " + e.Exception);
            e.SetObserved();
        };

        Paths.Ensure();
        Config.EnsureJpegQuality();

        _cfg = Config.LoadOrDefault(out var badBackup);
        _state = State.Load();
        _ui = Dispatcher.CurrentDispatcher;

        // До старта app.Run() контекста синхронизации нет, и продолжения await уезжают
        // на поток пула. WebView2 этого не прощает: его контроллер работает только из UI-потока.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_ui));

        Config.AutostartEnabled = _cfg.StartWithWindows;

        _engine = new PlaylistEngine(_cfg, _state);
        // Пока наших окон в слое нет, синхронный COM безопасен — а дальше уже нельзя.
        Desktop.WallpaperApplied += (monId, path) => _state.LastApplied[monId] = path;
        CaptureOriginals();
        _video = new VideoLayer(_cfg);

        _engine.Changed += UpdateTooltip;
        _engine.ScheduleWantsPlaylist += PlaylistEverywhere;
        _engine.VerifyLayer += () => _video.Verify();
        _engine.Present = Present;

        BuildTray();
        // Именно Reload, а не Sync: следить за папками плейлистов начинает он. Раньше здесь
        // был Sync, и слежение включалось только после первой правки настроек — до неё
        // новые файлы в папке приложение не замечало вовсе.
        _engine.Reload(_cfg);
        _video.Apply(_cfg);

        _hotkeys = new Hotkeys(_cfg.Hotkeys, OnHotkey, _cfg.HotkeyEnabled);
        _cfgWatcher = Config.Watch(() => _ui.BeginInvoke(ReloadConfig));

        // Обычное, не message-only окно: широковещательные сообщения до message-only не доходят.
        _appMessages = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("WallpaperAppMessages")
            {
                WindowStyle = unchecked((int)0x80000000),   // WS_POPUP, не показываем
                Width = 1,
                Height = 1,
            });
        _appMessages.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // Ловушка вызывается из оконной процедуры. Исключение, ушедшее отсюда наружу,
            // .NET через нативный кадр не протаскивает — процесс просто убивают.
            try
            {
                if ((uint)msg == ShowSettingsMsg) OpenSettings();
                else if ((uint)msg == QuitMsg) Quit();
            }
            catch (Exception e) { Paths.Write($"сообщение приложения {msg:X}: {e}"); }
            return IntPtr.Zero;
        });

        if (badBackup is not null)
            Balloon("config.json не читается", $"Копия отложена в {Path.GetFileName(badBackup)}. Работаем на настройках по умолчанию.");
        else if (_hotkeys.Failed.Count > 0)
            Balloon("Часть горячих клавиш занята", string.Join("\n", _hotkeys.Failed));
        else if (_cfg.Playlists.Count == 0)
        {
            // Первый запуск: показывать шар «настрой меня» и ничего не делать — так себе приём.
            OpenSettings();
        }

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

        // Сбой в обработчике UI не должен уносить приложение: обои важнее одной кнопки.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Paths.Write("СБОЙ В ИНТЕРФЕЙСЕ: " + e.Exception);
            e.Handled = true;
        };

        return app.Run();
    }

    // ---------- трей ----------

    static void BuildTray()
    {
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, _) => BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "Wallpaper",
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Next(); };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    static void UpdateTooltip()
    {
        var here = Here();
        var path = here is null ? null : _engine.CurrentPath(here.Id);
        var text = !_cfg.Enabled
            ? "Wallpaper — обои выключены"
            : path is null
            ? "Wallpaper — обои не настроены"
            : $"{(_engine.IsPaused(here!.Id) ? "⏸ " : "")}{Path.GetFileName(path)}";

        // NotifyIcon.Text не переживает больше 63 символов
        _tray.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    static void BuildMenu()
    {
        var items = _menu.Items;
        items.Clear();

        var mons = Desktop.Monitors();
        var here = Here(mons);

        var cur = here is null ? null : _engine.CurrentPath(here.Id);
        var plName = here is null ? null : _cfg.Playlist(_cfg.Monitors.GetValueOrDefault(here.Id)?.PlaylistId)?.Name;
        items.Add(Dead(cur is null
            ? "Обои не настроены"
            : (plName is null ? "" : plName + " — ") + Path.GetFileName(cur)));

        items.Add(new ToolStripSeparator());
        items.Add(Item("Обои включены", ToggleWallpapers, check: _cfg.Enabled));
        items.Add(Item(mons.Count > 1 ? "⏸  Слайд-шоу (под курсором)" : "⏸  Слайд-шоу",
            () => { if (here is not null) TogglePause(here); },
            check: here is not null && !_engine.IsPaused(here.Id),
            enabled: _cfg.Enabled && here is not null));
        items.Add(Item("⏭  Следующая", Next, enabled: here is not null));
        items.Add(new ToolStripSeparator());

        // --- плейлисты, разом на все мониторы
        var pls = new ToolStripMenuItem(mons.Count > 1 ? "Плейлисты (на свободных мониторах)" : "Плейлисты");
        if (_cfg.Playlists.Count == 0) pls.DropDownItems.Add(Dead("Плейлистов нет"));
        foreach (var pl in _cfg.Playlists)
        {
            var id = pl.Id;
            pls.DropDownItems.Add(Item($"{pl.Name}   ({_engine.Order(id).Length})",
                () => PlaylistEverywhere(id),
                check: mons.Any(m => _cfg.Monitors.GetValueOrDefault(m.Id) is { Mode: Mode.Playlist } c && c.PlaylistId == id)));
        }
        items.Add(pls);

        // --- мониторы по отдельности
        var mm = new ToolStripMenuItem("Мониторы");
        if (mons.Count == 0) mm.DropDownItems.Add(Dead("Мониторов не видно"));
        for (int i = 0; i < mons.Count; i++) mm.DropDownItems.Add(MonitorMenu(mons[i], i));
        items.Add(mm);

        // --- вписывание: SetPosition глобальный, per-monitor не бывает (SPEC §4.1)
        var fit = new ToolStripMenuItem("Вписывание (на всех)");
        foreach (var f in Enum.GetValues<FitMode>())
        {
            var v = f;
            fit.DropDownItems.Add(Item(FitName(v), () => { _cfg.Fit = v; Apply(); }, check: _cfg.Fit == v));
        }
        items.Add(fit);

        items.Add(new ToolStripSeparator());
        items.Add(Item("Быстрый выбор…", ShowPicker, enabled: _cfg.Playlists.Count > 0));
        items.Add(Item("Настройки…", OpenSettings));
        items.Add(Item("Добавить папку…", AddFolder));
        items.Add(Item("Показать в проводнике", () => Reveal(here), enabled: cur is not null));
        items.Add(new ToolStripSeparator());
        items.Add(Item("Запускать с Windows", ToggleAutostart, check: _cfg.StartWithWindows));
        items.Add(new ToolStripSeparator());
        items.Add(Item("Выход", Quit));
    }

    static ToolStripMenuItem MonitorMenu(Desktop.Display m, int index)
    {
        var mc = MonCfg(m.Id);
        var root = new ToolStripMenuItem(Label(m, index));

        root.DropDownItems.Add(Item("Выключено", () => { mc.Mode = Mode.Off; Apply(); }, check: mc.Mode == Mode.Off));
        root.DropDownItems.Add(Item("Картинка…", () => PickStatic(mc), check: mc.Mode == Mode.Static));
        root.DropDownItems.Add(Item("Видео…", () => PickVideo(mc), check: mc.Mode == Mode.Video));

        var pls = new ToolStripMenuItem("Плейлист") { Enabled = _cfg.Playlists.Count > 0 };
        foreach (var pl in _cfg.Playlists)
        {
            var id = pl.Id;
            pls.DropDownItems.Add(Item(pl.Name,
                () => { mc.Mode = Mode.Playlist; mc.PlaylistId = id; Apply(); },
                check: mc.Mode == Mode.Playlist && mc.PlaylistId == id));
        }
        root.DropDownItems.Add(pls);

        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(Item("⏸  Слайд-шоу", () => TogglePause(m),
            check: !_engine.IsPaused(m.Id), enabled: mc.Mode == Mode.Playlist));

        var cur = _engine.CurrentPath(m.Id);
        root.DropDownItems.Add(Dead(cur is null ? "—" : Path.GetFileName(cur)));
        return root;
    }

    // ---------- действия ----------

    static void Next()
    {
        if (Here() is { } m) _engine.Advance(m.Id, 1);
    }

    /// <summary>Пауза на одном мониторе. На остальных слайд-шоу продолжает идти.</summary>
    static void TogglePause(Desktop.Display m)
    {
        bool paused = _engine.TogglePaused(m.Id);
        var name = Desktop.Monitors().Count > 1 ? $" — {Label(m, IndexOf(m))}" : "";
        Balloon("Wallpaper", (paused ? "Слайд-шоу на паузе" : "Слайд-шоу продолжено") + name);
    }

    static int IndexOf(Desktop.Display m)
    {
        var mons = Desktop.Monitors();
        for (int i = 0; i < mons.Count; i++) if (mons[i].Id == m.Id) return i;
        return 0;
    }

    /// <summary>
    /// Плейлист на все мониторы — но не на те, которым явно назначено видео или картинка.
    /// Иначе один пункт меню (или срабатывание расписания) молча сносит настройку монитора.
    /// </summary>
    static void PlaylistEverywhere(string playlistId)
    {
        var mons = Desktop.Monitors();
        var free = mons.Where(m => MonCfg(m.Id).Mode is Mode.Playlist or Mode.Off).ToList();

        // Если свободных нет вообще — значит выбирать не из чего, и трогать чужое не станем.
        if (free.Count == 0)
        {
            Balloon("Плейлист не применён",
                "Все мониторы заняты видео или отдельной картинкой. Смени режим в настройках.");
            return;
        }

        foreach (var m in free)
        {
            var mc = MonCfg(m.Id);
            mc.Mode = Mode.Playlist;
            mc.PlaylistId = playlistId;
        }
        Apply();
    }

    static void AddFolder()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с обоями" };
        if (d.ShowDialog() != true) return;

        var name = new DirectoryInfo(d.FolderName).Name;
        var id = UniqueId(name);
        _cfg.Playlists.Add(new Playlist
        {
            Id = id,
            Name = name,
            Folders = [new FolderRef { Path = d.FolderName, Recursive = true }],
        });

        // Первый плейлист — сразу на все свободные мониторы, иначе ничего не произойдёт и это выглядит как баг.
        foreach (var m in Desktop.Monitors())
        {
            var mc = MonCfg(m.Id);
            if (mc.Mode == Mode.Off) { mc.Mode = Mode.Playlist; mc.PlaylistId = id; }
        }
        Apply();
    }

    static void PickStatic(MonitorCfg mc)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Картинка на рабочий стол",
            Filter = "Изображения|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.webp|Все файлы|*.*",
        };
        if (d.ShowDialog() != true) return;
        mc.Mode = Mode.Static;
        mc.Path = d.FileName;
        Apply();
    }

    static void PickVideo(MonitorCfg mc)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Видео или веб-страница на рабочий стол",
            Filter = "Видео, анимация, веб|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.m4v;*.wmv;*.gif;*.apng;*.webp;*.html;*.htm|Все файлы|*.*",
        };
        if (d.ShowDialog() != true) return;
        mc.Mode = Mode.Video;
        mc.Video = new VideoCfg { Path = d.FileName, Volume = mc.Video?.Volume ?? 0 };
        ApplyConfig();
        if (!Mpv.Available)
            Balloon("Нужна libmpv", $"Положи libmpv-2.dll рядом с Wallpaper.exe или в {Paths.Dir}");
    }

    static void Reveal(Desktop.Display? m)
    {
        var p = m is null ? null : _engine.CurrentPath(m.Id);
        if (p is null || !File.Exists(p)) return;
        try { Process.Start("explorer.exe", $"/select,\"{p}\""); }
        catch (Exception e) { Paths.Write($"explorer: {e.Message}"); }
    }

    static void OpenConfig()
    {
        try
        {
            if (!File.Exists(Paths.Config)) _cfg.Save();
            Process.Start(new ProcessStartInfo(Paths.Config) { UseShellExecute = true });
        }
        catch (Exception e) { Paths.Write($"открыть config.json: {e.Message}"); }
    }

    static void ToggleAutostart()
    {
        _cfg.StartWithWindows = !_cfg.StartWithWindows;
        Config.AutostartEnabled = _cfg.StartWithWindows;
        _cfg.Save();
    }

    static void Quit()
    {
        _picker?.Close();
        _settings?.Close();
        _tray.Visible = false;
        _tray.Dispose();

        // Сначала убрать окна из слоя Explorer, и только потом трогать его COM:
        // пока окна там, синхронный вызов вешает и нас, и рабочий стол системы.
        _video.Teardown();
        RestoreOriginals();
        Desktop.Flush();

        _video.Dispose();
        _hotkeys.Dispose();
        _cfgWatcher.Dispose();
        _engine.Dispose();
        _state.Save();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Показать файл на мониторе. Пока слой занят видео, картинки тоже идут через него:
    /// иначе каждая смена обоев через Windows убивает окна слоя на соседних мониторах.
    /// </summary>
    static bool Present(string monitorId, string path)
    {
        if (PlaylistEngine.IsMoving(path) || _video.LayerBusy)
        {
            _video.SetPlaylistItem(monitorId, path);
            return true;
        }

        _video.SetPlaylistItem(monitorId, null);
        return Desktop.SetWallpaper(monitorId, path);
    }

    /// <summary>
    /// Запомнить обои, которые стояли до нас. Проверяется на каждом запуске: если то, что
    /// сейчас на мониторе, мы туда не ставили — значит пользователь выбрал своё, и вернуть
    /// при выходе надо именно это. Иначе оставляем прежний оригинал: обои остались от нас.
    /// </summary>
    static void CaptureOriginals()
    {
        bool changed = false;

        foreach (var m in Desktop.Monitors())
        {
            var current = Desktop.CurrentWallpaper(m.Id);
            if (string.IsNullOrEmpty(current)) continue;

            if (_state.LastApplied.TryGetValue(m.Id, out var ours)
                && string.Equals(ours, current, StringComparison.OrdinalIgnoreCase)
                && _state.OriginalWallpapers.ContainsKey(m.Id))
                continue;   // это наши же обои с прошлого раза, оригинал не трогаем

            _state.OriginalWallpapers[m.Id] = current;
            changed = true;
        }

        if (_state.OriginalFit is null) { _state.OriginalFit = Desktop.CurrentPosition(); changed = true; }
        if (changed) _state.Save();
    }

    static void RestoreOriginals()
    {
        foreach (var (id, path) in _state.OriginalWallpapers) Desktop.SetWallpaper(id, path);
        if (_state.OriginalFit is FitMode f) Desktop.SetPosition(f);
        _state.Save();   // запомнить, что теперь на мониторах стоят оригиналы
    }

    /// <summary>Обои целиком вкл/выкл: выключенные возвращают то, что было до приложения.</summary>
    internal static void ToggleWallpapers()
    {
        _cfg.Enabled = !_cfg.Enabled;
        _cfg.Save();

        if (_cfg.Enabled)
        {
            _engine.Reload(_cfg);
            _video.Apply(_cfg);
        }
        else
        {
            _video.Teardown();
            RestoreOriginals();
        }
        UpdateTooltip();
        Balloon("Обои", _cfg.Enabled ? "включены" : "выключены");
    }

    static void OnHotkey(string action)
    {
        var mons = Desktop.Monitors();
        var here = Here(mons);

        switch (action)
        {
            case "next": if (here is not null) _engine.Advance(here.Id, 1); break;
            case "prev": if (here is not null) _engine.Advance(here.Id, -1); break;
            case "nextAll": _engine.AdvanceAll(1); break;
            case "revealCurrent": Reveal(here); break;
            case "quickPicker": ShowPicker(); break;
            case "toggleWallpapers": ToggleWallpapers(); break;

            case "toggleSound":
                if (here is null) break;
                var vol = _video.ToggleSound(here.Id);
                if (vol is null) Balloon("Звук", "На этом мониторе нет видео");
                else { _cfg.Save(); Balloon("Звук", vol > 0 ? "включён" : "выключен"); }
                break;

            case "togglePause":
                if (here is not null) TogglePause(here);
                break;

            case "quit": Quit(); break;

            default:
                if (here is not null
                    && action.StartsWith("playlist", StringComparison.Ordinal)
                    && int.TryParse(action.AsSpan(8), out int n)
                    && n >= 1 && n <= _cfg.Playlists.Count)
                {
                    var mc = MonCfg(here.Id);
                    mc.Mode = Mode.Playlist;
                    mc.PlaylistId = _cfg.Playlists[n - 1].Id;
                    Apply();
                    Balloon("Wallpaper", $"Плейлист: {_cfg.Playlists[n - 1].Name}");
                }
                break;
        }
    }

    static int _reloadTries;
    static DispatcherTimer? _reloadRetry;

    /// <summary>Правки config.json руками — в v0.1 это основной способ настройки, поэтому подхватываем на лету.</summary>
    static void ReloadConfig()
    {
        if (!Config.TryLoad(out var cfg, out var error))
        {
            // Редактор ещё держит файл или дописывает его вторым проходом. Ждём и пробуем снова:
            // без этого правка config.json молча не применяется — а в v0.1 это основной способ настройки.
            if (++_reloadTries <= 6)
            {
                _reloadRetry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
                    (_, _) => { _reloadRetry!.Stop(); ReloadConfig(); }, _ui);
                _reloadRetry.Stop();
                _reloadRetry.Start();
                return;
            }
            _reloadTries = 0;
            Paths.Write($"config.json не читается: {error}");
            Balloon("config.json не читается", error ?? "");
            return;
        }
        _reloadTries = 0;

        var hotkeysChanged = !cfg.Hotkeys.OrderBy(k => k.Key).SequenceEqual(_cfg.Hotkeys.OrderBy(k => k.Key));
        _cfg = cfg;
        Config.AutostartEnabled = _cfg.StartWithWindows;

        if (hotkeysChanged)
        {
            _hotkeys.Dispose();
            _hotkeys = new Hotkeys(_cfg.Hotkeys, OnHotkey, _cfg.HotkeyEnabled);
            if (_hotkeys.Failed.Count > 0) Balloon("Часть горячих клавиш занята", string.Join("\n", _hotkeys.Failed));
        }

        _engine.Reload(_cfg);
        _video.Apply(_cfg);
        ConfigReloaded?.Invoke();
    }

    /// <summary>Сохранить конфиг и применить его к рабочему столу.</summary>
    internal static void ApplyConfig()
    {
        _cfg.Save();
        _engine.Reload(_cfg);
        _video.Apply(_cfg);
    }

    static void Apply() => ApplyConfig();

    /// <summary>Перерегистрировать горячие клавиши после правки в настройках.</summary>
    internal static IReadOnlyList<string> RebindHotkeys()
    {
        _hotkeys.Dispose();
        _hotkeys = new Hotkeys(_cfg.Hotkeys, OnHotkey, _cfg.HotkeyEnabled);
        _cfg.Save();
        return _hotkeys.Failed;
    }

    internal static void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else if (_settings.WindowState == System.Windows.WindowState.Minimized)
        {
            _settings.WindowState = System.Windows.WindowState.Normal;
        }
        _settings.Activate();
    }

    static void ShowPicker()
    {
        if (Here() is not { } here) return;
        if (_cfg.Playlists.Count == 0) { Balloon("Быстрый выбор", "Сначала добавь папку с обоями"); return; }

        _picker?.Close();
        _picker = new QuickPicker(here);
        _picker.Closed += (_, _) => _picker = null;
        _picker.Show();
        _picker.Activate();
    }

    // ---------- мелочи ----------

    static MonitorCfg MonCfg(string monitorId) =>
        _cfg.Monitors.TryGetValue(monitorId, out var mc) ? mc : _cfg.Monitors[monitorId] = new MonitorCfg();

    static Desktop.Display? Here(List<Desktop.Display>? mons = null)
    {
        mons ??= Desktop.Monitors();
        var p = Control.MousePosition;
        return mons.FirstOrDefault(m => m.Contains(p.X, p.Y))
            ?? mons.FirstOrDefault(m => m.Primary)
            ?? mons.FirstOrDefault();
    }

    static string Label(Desktop.Display m, int index)
    {
        var custom = _cfg.Monitors.GetValueOrDefault(m.Id)?.Label;
        if (!string.IsNullOrWhiteSpace(custom)) return custom!;
        return $"Монитор {index + 1} — {m.W}×{m.H}{(m.Primary ? " (основной)" : "")}";
    }

    static string FitName(FitMode f) => f switch
    {
        FitMode.Center => "По центру",
        FitMode.Tile => "Замостить",
        FitMode.Stretch => "Растянуть",
        FitMode.Fit => "Вписать",
        FitMode.Fill => "Заполнить",
        FitMode.Span => "Одна на все мониторы",
        _ => f.ToString(),
    };

    static string UniqueId(string name)
    {
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');
        if (slug.Length == 0) slug = "playlist";
        var id = slug;
        for (int i = 2; _cfg.Playlists.Any(p => p.Id == id); i++) id = $"{slug}-{i}";
        return id;
    }

    static ToolStripMenuItem Item(string text, Action onClick, bool check = false, bool enabled = true)
    {
        var it = new ToolStripMenuItem(text) { Checked = check, Enabled = enabled };
        it.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception e) { Paths.Write($"меню «{text}»: {e}"); }
        };
        return it;
    }

    static ToolStripMenuItem Dead(string text) => new(text) { Enabled = false };

    static void Balloon(string title, string text) =>
        _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    /// <summary>ponytail: рисуем иконку кодом вместо .ico в репозитории. Один HICON на процесс, DestroyIcon не зовём.</summary>
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var frame = new Rectangle(2, 4, 28, 24);
            var inner = new Rectangle(5, 7, 22, 18);

            using var border = new SolidBrush(Color.FromArgb(78, 92, 232));
            using var sky = new SolidBrush(Color.FromArgb(226, 229, 253));
            using var hill = new SolidBrush(Color.FromArgb(46, 58, 170));
            using var sun = new SolidBrush(Color.FromArgb(224, 166, 76));

            g.FillRectangle(border, frame);
            g.FillRectangle(sky, inner);

            g.SetClip(inner);
            g.FillEllipse(sun, 19, 9, 6, 6);
            g.FillPolygon(hill, [new Point(3, 25), new Point(12, 12), new Point(21, 25)]);
            g.FillPolygon(hill, [new Point(15, 25), new Point(22, 16), new Point(29, 25)]);
            g.ResetClip();
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
