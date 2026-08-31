using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Wallpaper;

/// <summary>
/// Тонкая обёртка над libmpv. Библиотека не поставляется с приложением: она весит
/// десятки мегабайт и живёт под GPL. Если её нет — видеорежим просто недоступен,
/// приложение продолжает работать с картинками.
/// </summary>
public static class Mpv
{
    const string Lib = "libmpv-2.dll";

    static readonly string[] Names = ["libmpv-2.dll", "mpv-2.dll", "libmpv-1.dll", "mpv-1.dll"];

    static IntPtr _lib;

    public static bool Available => _lib != IntPtr.Zero;

    /// <summary>Откуда взялась библиотека — показывается в настройках.</summary>
    public static string? LoadedFrom { get; private set; }

    static Mpv()
    {
        // DllImport ниже указывает на постоянное имя; резолвер подставляет то, что реально нашли.
        NativeLibrary.SetDllImportResolver(typeof(Mpv).Assembly,
            (name, _, _) => name == Lib ? _lib : IntPtr.Zero);
    }

    /// <summary>Путь из конфига. libmpv весит больше сотни мегабайт — копировать её незачем.</summary>
    public static string? ExtraPath { get; set; }

    public static IEnumerable<string> SearchPaths()
    {
        if (!string.IsNullOrWhiteSpace(ExtraPath))
        {
            if (Directory.Exists(ExtraPath))
                foreach (var n in Names) yield return Path.Combine(ExtraPath, n);
            else
                yield return ExtraPath;
        }
        foreach (var n in Names) yield return Path.Combine(AppContext.BaseDirectory, n);
        foreach (var n in Names) yield return Path.Combine(Paths.Dir, n);
    }

    public static void Probe()
    {
        if (_lib != IntPtr.Zero) return;

        var problems = new List<string>();
        foreach (var p in SearchPaths())
        {
            if (!File.Exists(p)) continue;
            if (NativeLibrary.TryLoad(p, out _lib)) { LoadedFrom = p; return; }

            // Файл есть, а не грузится: обычно 32-битная сборка или не хватает зависимостей.
            // TryLoad причину проглатывает, поэтому спрашиваем ещё раз — уже с исключением.
            try { NativeLibrary.Load(p); }
            catch (Exception e) { problems.Add($"{p} — {e.Message}"); }
        }
        foreach (var n in Names)
        {
            if (NativeLibrary.TryLoad(n, out _lib)) { LoadedFrom = $"{n} (из PATH)"; return; }
        }

        Paths.Write(problems.Count > 0
            ? "libmpv не загрузилась: " + string.Join(" | ", problems)
            : $"libmpv не найдена. Искали: {string.Join(", ", SearchPaths().Take(2))}"
              + $" (mpvPath = {ExtraPath ?? "не задан"})");
    }

    [DllImport(Lib)] static extern IntPtr mpv_create();
    [DllImport(Lib)] static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib)] static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib)]
    static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib)]
    static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib)] static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    /// <summary>Создать проигрыватель, врисованный в готовое окно.</summary>
    public static IntPtr Create(IntPtr hwnd, int volume, FitMode fit)
    {
        if (!Available) return IntPtr.Zero;

        var ctx = mpv_create();
        if (ctx == IntPtr.Zero) return IntPtr.Zero;

        Opt(ctx, "wid", hwnd.ToInt64().ToString());
        Opt(ctx, "loop-file", "inf");            // бесшовный цикл, ради него и выбран mpv
        Opt(ctx, "image-display-duration", "inf");   // картинку держим, а не пролистываем
        Opt(ctx, "hwdec", "auto-safe");
        Opt(ctx, "vo", "gpu");
        Opt(ctx, "gpu-api", "d3d11");
        // Окно слоя обязано быть layered, а такое окно рисуется через redirection surface.
        // Flip-модель в неё не пишет: кадр застывает на первом. Bitblt-модель пишет.
        Opt(ctx, "d3d11-flip", "no");
        Opt(ctx, "volume", Math.Clamp(volume, 0, 100).ToString());
        Opt(ctx, "mute", volume > 0 ? "no" : "yes");

        Opt(ctx, "keepaspect", fit == FitMode.Stretch ? "no" : "yes");
        Opt(ctx, "panscan", fit == FitMode.Fit ? "0.0" : "1.0");   // 1.0 — обрезать по краям, как Fill

        // Обои не должны реагировать на клавиши, показывать плашки и читать чужой mpv.conf.
        Opt(ctx, "config", "no");
        Opt(ctx, "input-default-bindings", "no");
        Opt(ctx, "input-vo-keyboard", "no");
        Opt(ctx, "osc", "no");
        Opt(ctx, "osd-level", "0");
        Opt(ctx, "terminal", "no");
        Opt(ctx, "ytdl", "no");

        if (mpv_initialize(ctx) < 0)
        {
            mpv_terminate_destroy(ctx);
            Paths.Write("mpv_initialize не удался");
            return IntPtr.Zero;
        }
        return ctx;
    }

    /// <summary>
    /// Проигрыватель с произвольным набором опций — для перебора бэкендов вывода.
    /// Опции строками "ключ=значение".
    /// </summary>
    public static IntPtr CreateRaw(IntPtr hwnd, IEnumerable<string> options)
    {
        if (!Available) return IntPtr.Zero;

        var ctx = mpv_create();
        if (ctx == IntPtr.Zero) return IntPtr.Zero;

        Opt(ctx, "wid", hwnd.ToInt64().ToString());
        Opt(ctx, "loop-file", "inf");
        Opt(ctx, "mute", "yes");
        Opt(ctx, "hwdec", "auto-safe");
        Opt(ctx, "config", "no");
        Opt(ctx, "input-default-bindings", "no");
        Opt(ctx, "input-vo-keyboard", "no");
        Opt(ctx, "osc", "no");
        Opt(ctx, "osd-level", "0");
        Opt(ctx, "terminal", "no");
        Opt(ctx, "ytdl", "no");

        foreach (var o in options)
        {
            int eq = o.IndexOf('=');
            if (eq > 0) Opt(ctx, o[..eq], o[(eq + 1)..]);
        }

        if (mpv_initialize(ctx) < 0) { mpv_terminate_destroy(ctx); return IntPtr.Zero; }
        return ctx;
    }

    static void Opt(IntPtr ctx, string name, string value)
    {
        int rc = mpv_set_option_string(ctx, name, value);
        if (rc < 0) Paths.Write($"mpv: опция {name}={value} отклонена ({rc})");
    }

    public static void Load(IntPtr ctx, string path) => Command(ctx, "loadfile", path);

    /// <summary>Громкость меняется на лету — пересоздавать проигрыватель незачем.</summary>
    public static void SetVolume(IntPtr ctx, int volume)
    {
        if (ctx == IntPtr.Zero) return;
        int v = Math.Clamp(volume, 0, 100);
        mpv_set_property_string(ctx, "volume", v.ToString());
        mpv_set_property_string(ctx, "mute", v > 0 ? "no" : "yes");
    }

    public static void SetPaused(IntPtr ctx, bool paused)
    {
        if (ctx != IntPtr.Zero) mpv_set_property_string(ctx, "pause", paused ? "yes" : "no");
    }

    public static void Destroy(IntPtr ctx)
    {
        if (ctx != IntPtr.Zero) mpv_terminate_destroy(ctx);
    }

    /// <summary>mpv ждёт NULL-терминированный массив UTF-8 строк.</summary>
    static void Command(IntPtr ctx, params string[] args)
    {
        // libmpv на нулевой контекст не ругается, а падает в access violation и уносит
        // процесс целиком — без исключения и без записи в журнал.
        if (ctx == IntPtr.Zero) { Paths.Write($"mpv: команда {args[0]} без проигрывателя"); return; }

        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            int rc = mpv_command(ctx, ptrs);
            if (rc < 0) Paths.Write($"mpv: команда {string.Join(' ', args)} не прошла ({rc})");
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(p);
        }
    }
}

/// <summary>
/// Слой рабочего стола: окна с видео и веб-страницами, живущие под иконками.
/// Как к нему цепляться — на разных сборках Windows по-разному, поэтому способ
/// подбирается пробником один раз за запуск (см. <see cref="DetectAttach"/>).
/// </summary>
public sealed class VideoLayer : IDisposable
{
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>Одно окно слоя на одном мониторе. Внутри либо mpv, либо WebView2 — никогда оба.</summary>
    sealed class Surface
    {
        public IntPtr Hwnd, Mpv;
        public CoreWebView2Controller? Web;
        public string Path = "";
        public int Volume;
        public FitMode Fit;
        public bool Paused;
    }

    /// <summary>К какому окну рабочего стола цепляемся.</summary>
    public enum Attach { WorkerWInProgman, WorkerWSibling, Progman, DefView }

    /// <summary>Как именно окно становится частью слоя.</summary>
    public enum Technique
    {
        CreateAsChild,
        CreateAsChildRedraw,
        ReparentTopLevel,
        ReparentToBottom,
        ReparentBelowIcons,
        ReparentThenRaiseIcons,
    }

    const uint WS_CHILD = 0x40000000;
    const uint WS_VISIBLE = 0x10000000;
    const uint WS_POPUP = 0x80000000;

    const uint WS_EX_NOACTIVATE = 0x08000000;
    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_LAYERED = 0x00080000;
    const uint LWA_ALPHA = 2;

    /// <summary>
    /// Без WS_EX_LAYERED окно внутри WorkerW не композитится вовсе — DWM его просто не рисует.
    /// Это и есть весь секрет: тот же стиль стоит у окон Wallpaper Engine.
    /// </summary>
    const uint SurfaceExStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    const int WM_DISPLAYCHANGE = 0x007E;
    const int GWL_STYLE = -16;
    const int SW_SHOW = 5;
    const uint RDW_ALL = 0x0585;

    static readonly IntPtr HWND_TOP = new(0);
    static readonly IntPtr HWND_BOTTOM = new(1);

    const string ClassName = "WallpaperVideoHost";

    static WndProc? _wndProc;
    static bool _classRegistered;

    readonly Dictionary<string, Surface> _surfaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Что просит показать слайд-шоу, пока слой занят: монитор → файл.</summary>
    readonly Dictionary<string, string> _fromPlaylist = new(StringComparer.OrdinalIgnoreCase);

    readonly HwndSource _watcher;
    readonly uint _taskbarCreated;
    readonly DispatcherTimer _repark;
    readonly PauseWatcher _pause;

    Config _cfg;
    IntPtr _workerW;
    Technique _tech;
    string _lastShape = "";
    bool _toldAboutMissingMpv;

    /// <summary>Ложь — выбранный способ кладёт видео поверх иконок рабочего стола.</summary>
    public bool BehindIcons { get; private set; } = true;

    /// <summary>
    /// Слой занят: есть живые окна или хотя бы один монитор просит видео. Пока это так,
    /// картинки слайд-шоу тоже идут через слой — иначе смена обоев Windows заставит
    /// Explorer перестроить рабочий стол и убить наши окна на соседних мониторах.
    /// </summary>
    public bool LayerBusy =>
        _surfaces.Count > 0
        || _cfg.Monitors.Values.Any(m => m.Mode == Mode.Video && !string.IsNullOrWhiteSpace(m.Video?.Path));

    /// <summary>Что показать человеку в настройках, если что-то пошло не так.</summary>
    public string? Note { get; private set; }

    public string PauseReason => _pause.Reason;

    public int ActiveCount => _surfaces.Count;

    // ================= окна =================

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassExW(in WNDCLASSEXW c);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyWindow(IntPtr h);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessageW(string name);

    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint colorref);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindow(IntPtr h, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateWindow(IntPtr h);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    /// <summary>Непрозрачное layered-окно: альфа 255. Без этого вызова окно остаётся невидимым.</summary>
    static void MakeOpaqueLayered(IntPtr h) => SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);

    static void EnsureClass()
    {
        if (_classRegistered) return;

        _wndProc = DefWindowProcW;
        RegisterClassExW(new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = CreateSolidBrush(0),
            lpszClassName = ClassName,
        });
        _classRegistered = true;
    }

    // ================= жизнь слоя =================

    public VideoLayer(Config cfg)
    {
        _cfg = cfg;
        _pause = new PauseWatcher(cfg, ApplyPauseRules);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // Окно нарочно не message-only: широковещательные сообщения до таких не доходят,
        // а именно ими Explorer сообщает о своём перезапуске.
        _watcher = new HwndSource(new HwndSourceParameters("WallpaperVideoWatcher")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            Width = 1,
            Height = 1,
        });
        _watcher.AddHook(OnMessage);

        _repark = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => { _repark!.Stop(); Repark(); }, Dispatcher.CurrentDispatcher);
        _repark.Stop();
    }

    IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // Исключение, выпущенное из оконного колбэка, .NET не ловит: процесс убивается
        // мгновенно и молча (0xc000041d). Поэтому здесь глухая ловушка.
        try { return OnMessageCore(msg); }
        catch (Exception e) { Paths.Write($"сообщение окна {msg:X}: {e}"); return IntPtr.Zero; }
    }

    IntPtr OnMessageCore(int msg)
    {
        if ((uint)msg == _taskbarCreated)
        {
            Paths.Write("Explorer перезапустился, перевешиваем видеослой");
            _repark.Stop();
            _repark.Start();
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            Reposition();
        }
        return IntPtr.Zero;
    }

    /// <summary>Привести слой в соответствие конфигу: создать нужные окна, снять лишние.</summary>
    public void Apply(Config cfg)
    {
        _cfg = cfg;
        _pause.Reload(cfg);
        Note = null;

        var wanted = new Dictionary<string, Surface>(StringComparer.OrdinalIgnoreCase);
        var mons = cfg.Enabled ? Desktop.Monitors() : [];

        var shape = $"{mons.Count}/{_surfaces.Count}/{cfg.Enabled}";
        if (shape != _lastShape) { _lastShape = shape; Paths.Write($"слой: мониторов {mons.Count}, поверхностей {_surfaces.Count}, вкл={cfg.Enabled}"); }

        foreach (var mon in mons)
        {
            var mc = cfg.Monitors.GetValueOrDefault(mon.Id);
            string? path;
            int volume;

            if (mc is not null && mc.Mode == Mode.Video)
            {
                path = mc.Video?.Path;
                volume = mc.Video?.Volume ?? 0;
            }
            else if (mc is not null && mc.Mode == Mode.Playlist && _fromPlaylist.TryGetValue(mon.Id, out var fromPl))
            {
                path = fromPl;
                volume = 0;
            }
            else
            {
                _fromPlaylist.Remove(mon.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!WebLayer.Exists(path))
            {
                Note = "не найдено: " + Path.GetFileName(path);
                Paths.Write("обои не найдены: " + path);
                continue;
            }

            wanted[mon.Id] = new Surface { Path = path, Volume = volume, Fit = cfg.Fit };
        }

        // Снимок мониторов иногда приходит пустым на ровном месте (переключение входа, сон).
        // Сносить из-за этого работающее видео — хуже, чем ничего не делать.
        if (wanted.Count == 0 && _surfaces.Count > 0 && cfg.Enabled && Desktop.Monitors().Count == 0)
        {
            Paths.Write("мониторов не видно — видеослой оставлен как есть");
            return;
        }

        foreach (var id in _surfaces.Keys.ToArray())
        {
            if (wanted.ContainsKey(id)) continue;
            Paths.Write("снимаем слой: монитор больше не просит видео (" + Path.GetFileName(_surfaces[id].Path) + ")");
            Drop(id);
        }

        if (wanted.Count == 0) { _pause.Stop(); return; }

        if (wanted.Values.Any(w => !WebLayer.IsWeb(w.Path)))
        {
            Mpv.ExtraPath = cfg.MpvPath;
            Mpv.Probe();
            if (!Mpv.Available)
            {
                Note = "libmpv не найден";
                if (!_toldAboutMissingMpv)
                {
                    _toldAboutMissingMpv = true;
                    Paths.Write("видеорежим включён, но libmpv-2.dll нет — положи её рядом с Wallpaper.exe");
                }
                return;
            }
        }

        var attach = DetectAttach();
        if (attach is null) { Note = "слой рабочего стола недоступен на этой сборке Windows"; return; }

        _tech = attach.Value.Tech;
        BehindIcons = attach.Value.BehindIcons;
        _workerW = Target(attach.Value.How).Parent;
        if (_workerW == IntPtr.Zero) { Note = "слой рабочего стола пропал"; return; }

        foreach (var (monitorId, want) in wanted) Ensure(monitorId, want);

        _pause.Start();
        ApplyPauseRules();
    }

    /// <summary>
    /// Слайд-шоу просит показать файл через слой. null — монитору слой больше не нужен.
    /// </summary>
    public void SetPlaylistItem(string monitorId, string? path)
    {
        if (path is null)
        {
            if (_fromPlaylist.Remove(monitorId)) Apply(_cfg);
            return;
        }
        if (_fromPlaylist.TryGetValue(monitorId, out var had) && had == path) return;

        _fromPlaylist[monitorId] = path;
        Apply(_cfg);
    }

    void Ensure(string monitorId, Surface want)
    {
        var mon = Desktop.Monitors().FirstOrDefault(m => m.Id == monitorId);
        if (mon is null) { Paths.Write($"слой: монитор пропал из снимка ({monitorId[..24]}…)"); return; }

        if (_surfaces.TryGetValue(monitorId, out var s) && Desktop.IsWindow(s.Hwnd))
        {
            // Подменить файл на лету умеет только mpv. У веб-поверхности контекст mpv нулевой,
            // и вызов libmpv с ним — access violation: процесс умирает мгновенно и без записи
            // в журнал. Так падало на плейлисте, где за страницей шла обычная картинка.
            bool sameKind = WebLayer.IsWeb(s.Path) == WebLayer.IsWeb(want.Path);
            // Громкость и вписывание задаются при создании — меняем пересозданием, это редкий случай.
            bool sameLook = s.Volume == want.Volume && s.Fit == want.Fit;

            if (sameKind && sameLook && (s.Path == want.Path || s.Mpv != IntPtr.Zero))
            {
                Place(s.Hwnd, mon);
                if (s.Path != want.Path) { s.Path = want.Path; Mpv.Load(s.Mpv, want.Path); }
                return;
            }
            Drop(monitorId);
        }

        EnsureClass();
        if (!Desktop.GetWindowRect(_workerW, out var pr)) { Paths.Write("слой: у WorkerW нет области"); return; }

        var hwnd = MakeWindow(ClassName, _workerW, _tech, mon, pr);
        if (hwnd == IntPtr.Zero)
        {
            Paths.Write($"не удалось создать окно видео: {Marshal.GetLastWin32Error()}");
            return;
        }

        var surface = new Surface
        {
            Hwnd = hwnd, Path = want.Path, Volume = want.Volume, Fit = want.Fit,
        };

        if (WebLayer.IsWeb(want.Path))
        {
            _surfaces[monitorId] = surface;
            Paths.Write($"страница пущена: {Path.GetFileName(want.Path)}");
            // WebView2 поднимается асинхронно; окно уже на месте, содержимое доедет следом.
            _ = FillWebAsync(surface, mon);
            return;
        }

        var ctx = Mpv.Create(hwnd, want.Volume, want.Fit);
        if (ctx == IntPtr.Zero)
        {
            DestroyWindow(hwnd);
            Note = "mpv не запустился";
            return;
        }
        Mpv.Load(ctx, want.Path);
        surface.Mpv = ctx;
        _surfaces[monitorId] = surface;
        Paths.Write($"в слой пущено: {Path.GetFileName(want.Path)} на {mon.W}×{mon.H} @ {mon.X},{mon.Y}");
    }

    async Task FillWebAsync(Surface s, Desktop.Display mon)
    {
        var web = await WebLayer.CreateAsync(s.Hwnd, s.Path, mon.W, mon.H);
        if (web is null) { Note = "веб-страница не открылась"; return; }

        // Пока страница поднималась, поверхность могли снять — тогда выбрасываем и её.
        if (!_surfaces.ContainsValue(s)) { WebLayer.Destroy(web); return; }

        s.Web = web;
        if (s.Paused) WebLayer.SetPaused(web, paused: true);
    }

    /// <summary>Поставить окно ровно на монитор. Координаты — относительно родителя, не экрана.</summary>
    void Place(IntPtr hwnd, Desktop.Display m)
    {
        if (!Desktop.GetWindowRect(_workerW, out var pr)) return;

        SetWindowPos(hwnd, IntPtr.Zero, m.X - pr.Left, m.Y - pr.Top, m.W, m.H, SWP_NOZORDER | SWP_NOACTIVATE);

        foreach (var s in _surfaces.Values)
            if (s.Hwnd == hwnd && s.Web is not null) WebLayer.Resize(s.Web, m.W, m.H);
    }

    void Drop(string monitorId)
    {
        if (!_surfaces.Remove(monitorId, out var s)) return;
        Paths.Write($"слой снят с монитора ({Path.GetFileName(s.Path)})");
        Mpv.Destroy(s.Mpv);
        WebLayer.Destroy(s.Web);
        if (s.Hwnd != IntPtr.Zero) DestroyWindow(s.Hwnd);
    }

    /// <summary>
    /// Живы ли наши окна и там ли они. Explorer убивает чужих детей своего WorkerW
    /// при каждой смене обоев — заметить это можно только проверкой.
    /// </summary>
    public void Verify()
    {
        if (_surfaces.Count == 0) return;

        foreach (var (_, s) in _surfaces)
        {
            bool alive = Desktop.IsWindow(s.Hwnd);
            bool parked = alive && GetParent(s.Hwnd) == _workerW;
            if (alive && parked) continue;

            Paths.Write(alive
                ? "слой уехал из WorkerW (" + Path.GetFileName(s.Path) + ") — пересобираем"
                : "окно слоя убито Explorer'ом (" + Path.GetFileName(s.Path) + ") — пересобираем");
            Repark();
            break;
        }
    }

    /// <summary>Снести все окна и собрать слой заново, заодно перепроверив способ привязки.</summary>
    public void Repark()
    {
        foreach (var id in _surfaces.Keys.ToArray())
        {
            if (!_surfaces.Remove(id, out var s)) continue;
            Mpv.Destroy(s.Mpv);
            WebLayer.Destroy(s.Web);
            if (Desktop.IsWindow(s.Hwnd)) DestroyWindow(s.Hwnd);
        }

        _workerW = IntPtr.Zero;
        _attach = null;
        _attachProbed = false;
        Apply(_cfg);
    }

    public void Reposition()
    {
        Desktop.RefreshMonitors();
        if (_surfaces.Count > 0) Apply(_cfg);
    }

    void ApplyPauseRules()
    {
        if (_surfaces.Count == 0) return;

        bool global = _pause.GlobalPause();
        foreach (var mon in Desktop.Monitors())
        {
            if (!_surfaces.TryGetValue(mon.Id, out var s)) continue;

            bool covered = !global && _pause.ForegroundCovers(mon);
            bool paused = global || covered;
            if (paused == s.Paused) continue;

            s.Paused = paused;
            if (s.Web is not null) WebLayer.SetPaused(s.Web, paused);
            else Mpv.SetPaused(s.Mpv, paused);

            Paths.Write(paused
                ? "пауза видео: " + (global ? _pause.Reason : "окно закрыло монитор")
                : "видео продолжено");
        }
    }

    /// <summary>Звук вкл/выкл. null — на мониторе нет видео или там страница.</summary>
    public int? ToggleSound(string monitorId)
    {
        if (!_surfaces.TryGetValue(monitorId, out var s)) return null;
        if (s.Web is not null) return null;

        int v = s.Volume = s.Volume > 0 ? 0 : 100;
        Mpv.SetVolume(s.Mpv, v);

        var mc = _cfg.Monitors.GetValueOrDefault(monitorId);
        if (mc?.Video is not null) mc.Video.Volume = v;
        return v;
    }

    public bool HasVideo(string monitorId) => _surfaces.ContainsKey(monitorId);

    // ================= диагностика дерева окон =================

    /// <summary>Дерево окон рабочего стола до и после «пинка» Progman — для ключа --workerw.</summary>
    public static string Hierarchy()
    {
        var log = new List<string>();
        var progman = Desktop.Progman();

        log.Add($"  Progman:     0x{progman.ToInt64():X8}");
        log.Add("");

        Dump("верхний уровень, до сообщения");
        DumpChildren("до сообщения");

        Desktop.PokeProgman(progman, win11Variant: false);
        Thread.Sleep(300);
        Desktop.PokeProgman(progman, win11Variant: true);
        Thread.Sleep(300);

        Dump("верхний уровень, после 0x052C");
        DumpChildren("после 0x052C");

        var chosen = Desktop.FindWorkerW();
        log.Add($"  выбран слой: 0x{chosen.ToInt64():X8} — {Desktop.LastWorkerWRoute}");

        var over = Desktop.FullScreenTopLevel();
        log.Add(over.Count == 0
            ? "  окон во весь стол поверх рабочего стола нет"
            : "  поверх рабочего стола растянуты: " + string.Join(", ", over.Select(c => $"{c.Class} [{c.Process}]")));

        var strangers = Desktop.DesktopChildren()
            .Where(c => !c.Process.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        log.Add(strangers.Count == 0
            ? "  чужих окон в слое нет"
            : "  ВНИМАНИЕ, в слое чужие окна: " + string.Join(", ", strangers.Select(f => $"{f.Class} ({f.Process})")));

        return string.Join('\n', log);

        void Dump(string when)
        {
            log.Add($"  {when}:");
            var list = Desktop.DesktopWindows();
            if (list.Count == 0) log.Add("    (пусто)");
            foreach (var (h, cls, r, vis, defView) in list)
                log.Add($"    {cls,-8} 0x{h.ToInt64():X8}  {r.Right - r.Left,5}×{r.Bottom - r.Top,-5} @ {r.Left,6},{r.Top,-5}  {(vis ? "видимо " : "скрыто ")}{(defView ? " + SHELLDLL_DefView" : "")}");
            log.Add("");
        }

        void DumpChildren(string when)
        {
            log.Add($"  {when} — внутри Progman:");
            foreach (var (h, cls, r, vis, proc) in Desktop.DesktopChildren())
                log.Add($"    {cls,-26} 0x{h.ToInt64():X8}  {r.Right - r.Left,5}×{r.Bottom - r.Top,-5} @ {r.Left,6},{r.Top,-5}  {(vis ? "видимо" : "скрыто")}  {proc}");
            log.Add("");
        }
    }

    // ================= пробник =================

    const string ProbeClass = "WallpaperVideoProbe";
    const uint ProbeColor = 0x00FF00;

    static WndProc? _probeProc;
    static bool _probeClassRegistered;

    /// <summary>
    /// Порядок перебора. Первые два кладут видео под иконки, остальные — поверх;
    /// поверх иконок лучше, чем ничего, но только как запасной вариант.
    /// </summary>
    static readonly (Attach How, Technique Tech, bool BehindIcons)[] Candidates =
    [
        (Attach.WorkerWSibling,   Technique.ReparentTopLevel, true),
        (Attach.WorkerWInProgman, Technique.ReparentTopLevel, true),
        (Attach.DefView,          Technique.ReparentTopLevel, false),
        (Attach.Progman,          Technique.ReparentTopLevel, false),
    ];

    static (Attach How, Technique Tech, bool BehindIcons)? _attach;
    static bool _attachProbed;

    static readonly (string Name, string[] Opts)[] Backends =
    [
        ("gpu / d3d11, flip=no",      ["vo=gpu", "gpu-api=d3d11", "d3d11-flip=no"]),
        ("gpu / d3d11, как обычно",   ["vo=gpu", "gpu-api=d3d11"]),
        ("gpu / opengl, win",         ["vo=gpu", "gpu-api=opengl", "gpu-context=win"]),
        ("gpu / opengl, dxinterop",   ["vo=gpu", "gpu-api=opengl", "gpu-context=dxinterop"]),
        ("gpu / opengl, angle",       ["vo=gpu", "gpu-api=opengl", "gpu-context=angle"]),
        ("gpu / vulkan",              ["vo=gpu", "gpu-api=vulkan"]),
        ("gpu-next / d3d11",          ["vo=gpu-next", "gpu-api=d3d11"]),
        ("gpu-next / opengl, win",    ["vo=gpu-next", "gpu-api=opengl", "gpu-context=win"]),
        ("direct3d, старый вывод",    ["vo=direct3d"]),
        ("sdl",                       ["vo=sdl"]),
    ];

    static void EnsureProbeClass()
    {
        if (_probeClassRegistered) return;

        _probeProc = DefWindowProcW;
        RegisterClassExW(new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_probeProc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = CreateSolidBrush(ProbeColor),
            lpszClassName = ProbeClass,
        });
        _probeClassRegistered = true;
    }

    static IntPtr MakeProbe(IntPtr parent, Technique tech, Desktop.Display m, RECT pr)
        => MakeWindow(ProbeClass, parent, tech, m, pr);

    /// <summary>
    /// Создать окно слоя. Layered-стиль нельзя задать сразу дочернему окну, поэтому окно
    /// рождается верхнеуровневым, усыновляется, и только потом ему меняют стиль.
    /// </summary>
    static IntPtr MakeWindow(string cls, IntPtr parent, Technique tech, Desktop.Display m, RECT pr)
    {
        int x = m.X - pr.Left, y = m.Y - pr.Top;

        if (tech is Technique.CreateAsChild or Technique.CreateAsChildRedraw)
        {
            var child = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls, null, WS_CHILD | WS_VISIBLE,
                x, y, m.W, m.H, parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (child == IntPtr.Zero) return IntPtr.Zero;

            ShowWindow(child, SW_SHOW);
            UpdateWindow(child);
            if (tech == Technique.CreateAsChildRedraw) RedrawWindow(parent, IntPtr.Zero, IntPtr.Zero, RDW_ALL);
            return child;
        }

        var top = CreateWindowExW(SurfaceExStyle, cls, null, WS_POPUP | WS_VISIBLE,
            m.X, m.Y, m.W, m.H, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (top == IntPtr.Zero) return IntPtr.Zero;

        SetParent(top, parent);

        long style = GetWindowLongPtr(top, GWL_STYLE);
        style = (style & ~(long)WS_POPUP) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(top, GWL_STYLE, new IntPtr(style));

        // Только теперь окно становится видимым для DWM.
        MakeOpaqueLayered(top);

        var after = tech switch
        {
            Technique.ReparentToBottom => HWND_BOTTOM,
            Technique.ReparentBelowIcons => Desktop.IconList(parent),
            _ => IntPtr.Zero,
        };
        SetWindowPos(top, after, x, y, m.W, m.H, SWP_NOACTIVATE | (after == IntPtr.Zero ? SWP_NOZORDER : 0));
        ShowWindow(top, SW_SHOW);
        UpdateWindow(top);

        if (tech == Technique.ReparentThenRaiseIcons)
        {
            var icons = Desktop.IconList(parent);
            if (icons != IntPtr.Zero)
                SetWindowPos(icons, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        RedrawWindow(parent, IntPtr.Zero, IntPtr.Zero, RDW_ALL);
        return top;
    }

    static (IntPtr Parent, string Name) Target(Attach how)
    {
        var progman = Desktop.Progman();
        return how switch
        {
            Attach.WorkerWInProgman => (Desktop.WorkerWInsideProgman(progman), "WorkerW внутри Progman"),
            Attach.WorkerWSibling => (Desktop.WorkerWSibling(), "WorkerW рядом с DefView"),
            Attach.Progman => (progman, "Progman"),
            Attach.DefView => (Desktop.DefView(progman), "SHELLDLL_DefView"),
            _ => (IntPtr.Zero, "?"),
        };
    }

    static string TechName(Technique t) => t switch
    {
        Technique.CreateAsChild => "сразу дочернее",
        Technique.CreateAsChildRedraw => "дочернее + перерисовка",
        Technique.ReparentTopLevel => "усыновить верхнеуровневое",
        Technique.ReparentToBottom => "усыновить и вниз z-порядка",
        Technique.ReparentBelowIcons => "усыновить под список иконок",
        Technique.ReparentThenRaiseIcons => "усыновить, иконки поднять над нами",
        _ => "?",
    };

    /// <summary>Полный перебор всех сочетаний с отчётом — для ключа --workerw.</summary>
    public static ((Attach How, Technique Tech)? Best, string Report) ProbeAttach()
    {
        var lines = new List<string>();
        (Attach, Technique)? best = null;

        var mon = Desktop.Monitors().FirstOrDefault(m => m.Primary) ?? Desktop.Monitors().FirstOrDefault();
        if (mon is null) return (null, "  мониторов не видно");

        EnsureProbeClass();
        int px = mon.X + mon.W / 2, py = mon.Y + mon.H / 2;

        // Контроль: если обычное окно поверх всего не видно, врёт сам замер, а не способы.
        var control = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, ProbeClass, null, WS_POPUP | WS_VISIBLE,
            px - 60, py - 60, 120, 120, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        Pump(400);
        uint got = ScreenPixel(px, py);
        DestroyWindow(control);
        Pump(200);

        bool ok = got == ProbeColor;
        lines.Add($"    {"контроль: обычное окно поверх всего",-38} — {(ok ? "ВИДНО" : $"не видно, пиксель 0x{got:X6}")}");
        if (!ok) lines.Add("    (прибор врёт или экран чем-то закрыт — сверни окна и повтори)");
        lines.Add("");

        foreach (var how in Enum.GetValues<Attach>())
        {
            var (parent, name) = Target(how);
            if (parent == IntPtr.Zero) { lines.Add($"    {name,-24} — не к чему цепляться"); continue; }
            if (!Desktop.GetWindowRect(parent, out var pr)) { lines.Add($"    {name,-24} — нет области"); continue; }

            lines.Add($"    {name} (0x{parent.ToInt64():X}, {pr.Right - pr.Left}×{pr.Bottom - pr.Top}):");

            foreach (var tech in Enum.GetValues<Technique>())
            {
                uint before = ScreenPixel(px, py);
                var h = MakeProbe(parent, tech, mon, pr);
                if (h == IntPtr.Zero) { lines.Add($"      {TechName(tech),-30} — окно не создалось"); continue; }

                Pump(450);
                uint onScreen = ScreenPixel(px, py);
                uint inParent = WindowPixel(parent, pr, px, py);

                // Замер «шевелится ли картинка»: у застывшего кадра пиксель не меняется.
                bool still = true;
                for (int k = 0; k < 5 && still; k++) { Pump(90); still = ScreenPixel(px, py) == onScreen; }

                DestroyWindow(h);
                Pump(150);

                bool changed = onScreen != before;
                lines.Add($"      {TechName(tech),-30} — в родителе {(inParent == ProbeColor ? "ЗЕЛЁНОЕ" : $"0x{inParent:X6}")}, "
                        + $"экран {(!changed ? "без изменений" : onScreen == ProbeColor ? "видно" : $"0x{onScreen:X6}")}, "
                        + $"{(still ? "НЕПОДВИЖЕН" : "меняется")}");

                if (inParent == ProbeColor && best is null) best = (how, tech);
            }
        }

        return (best, string.Join('\n', lines));
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>
    /// Цвет пикселя внутри окна, а не на экране. Так проверка не зависит от того,
    /// что лежит поверх рабочего стола — а поверх него лежит почти всегда что-нибудь.
    /// </summary>
    static uint WindowPixel(IntPtr hwnd, RECT wr, int screenX, int screenY)
    {
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return uint.MaxValue;

        try
        {
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 2); }   // PW_RENDERFULLCONTENT
                finally { g.ReleaseHdc(hdc); }
            }
            var c = bmp.GetPixel(screenX - wr.Left, screenY - wr.Top);
            return (uint)(c.R | (c.G << 8) | (c.B << 16));
        }
        catch { return uint.MaxValue; }
    }

    static uint ScreenPixel(int x, int y)
    {
        try
        {
            using var bmp = new Bitmap(1, 1);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
            var c = bmp.GetPixel(0, 0);
            return (uint)(c.R | (c.G << 8) | (c.B << 16));
        }
        catch
        {
            var dc = GetDC(IntPtr.Zero);
            try { return GetPixel(dc, x, y); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
    }

    /// <summary>Покрутить очередь сообщений: пробнику надо дать себя отрисовать.</summary>
    static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Подобрать рабочий способ привязки. Результат кэшируется на весь запуск, включая
    /// неудачу: перебирать заново на каждой смене обоев — это мигание на весь экран.
    /// </summary>
    public static (Attach How, Technique Tech, bool BehindIcons)? DetectAttach()
    {
        if (_attachProbed) return _attach;

        var mon = Desktop.Monitors().FirstOrDefault(d => d.Primary) ?? Desktop.Monitors().FirstOrDefault();
        if (mon is null) return null;

        _attachProbed = true;
        EnsureProbeClass();

        int px = mon.X + mon.W / 2, py = mon.Y + mon.H / 2;

        // Пробник маленький — 16×16 вместо окна во весь монитор, из-за которого раньше
        // мигало зелёным. Но именно в центре: у края родитель отрисовывается не всегда,
        // и проверка начинала врать, выбирая слой поверх иконок вместо правильного.
        var probeArea = new Desktop.Display(mon.Id, px - 8, py - 8, 16, 16);

        foreach (var cand in Candidates)
        {
            var (parent, name) = Target(cand.How);
            if (parent == IntPtr.Zero || !Desktop.GetWindowRect(parent, out var pr)) continue;

            var h = MakeProbe(parent, cand.Tech, probeArea, pr);
            if (h == IntPtr.Zero) continue;

            Pump(250);
            bool ok = WindowPixel(parent, pr, px, py) == ProbeColor;
            DestroyWindow(h);
            Pump(80);

            if (!ok) continue;

            Paths.Write($"слой рабочего стола: {name} + {TechName(cand.Tech)}"
                      + (cand.BehindIcons ? "" : " (видео окажется поверх иконок)"));
            return _attach = cand;
        }

        Paths.Write("рабочий способ привязки к рабочему столу не найден");
        return null;
    }

    /// <summary>Идут ли кадры: сравниваем четыре точки экрана до и после паузы.</summary>
    static bool FramesMoving(Desktop.Display m)
    {
        var points = new (int X, int Y)[]
        {
            (m.X + m.W / 4, m.Y + m.H / 4),
            (m.X + m.W / 2, m.Y + m.H / 2),
            (m.X + m.W * 3 / 4, m.Y + m.H / 2),
            (m.X + m.W / 2, m.Y + m.H * 3 / 4),
        };

        var before = points.Select(p => ScreenPixel(p.X, p.Y)).ToArray();
        Pump(1300);
        var after = points.Select(p => ScreenPixel(p.X, p.Y)).ToArray();
        return before.Where((v, i) => v != after[i]).Any();
    }

    /// <summary>Перебор способов вывода mpv на настоящем файле — для ключа --videosweep.</summary>
    public static void BackendSweep(string videoPath, Action<string> say)
    {
        var mon = Desktop.Monitors().FirstOrDefault(m => m.Primary) ?? Desktop.Monitors().FirstOrDefault();
        if (mon is null) { say("  мониторов не видно"); return; }

        Mpv.Probe();
        if (!Mpv.Available) { say("  libmpv не найдена"); return; }

        var attach = DetectAttach();
        if (attach is null) { say("  слой рабочего стола недоступен"); return; }

        var (parent, name) = Target(attach.Value.How);
        if (!Desktop.GetWindowRect(parent, out var pr)) { say("  у слоя нет области"); return; }

        say($"  слой:  {name} + {TechName(attach.Value.Tech)}");
        say($"  файл:  {Path.GetFileName(videoPath)}");
        say("");

        EnsureClass();
        foreach (var (bname, opts) in Backends)
        {
            var hwnd = MakeWindow(ClassName, parent, attach.Value.Tech, mon, pr);
            if (hwnd == IntPtr.Zero) { say($"    {bname,-26} — окно не создалось"); continue; }

            var ctx = Mpv.CreateRaw(hwnd, opts);
            if (ctx == IntPtr.Zero) { DestroyWindow(hwnd); say($"    {bname,-26} — mpv не запустился"); continue; }

            Mpv.Load(ctx, videoPath);
            Pump(2500);
            bool moving = FramesMoving(mon);

            Mpv.Destroy(ctx);
            DestroyWindow(hwnd);
            Pump(400);

            say($"    {bname,-26} — {(moving ? "КАДРЫ ИДУТ" : "застыло")}");
        }
    }

    /// <summary>
    /// Прогон всех сочетаний по очереди с заливкой экрана зелёным — на случай, когда
    /// автоматический замер врёт (например, поверх стола лежит прозрачный оверлей).
    /// </summary>
    public static void VisualWalk(int secondsEach, Action<string> say)
    {
        var primary = Desktop.Monitors().FirstOrDefault(m => m.Primary) ?? Desktop.Monitors().FirstOrDefault();
        if (primary is null) { say("  мониторов не видно"); return; }

        EnsureProbeClass();
        say("");
        say("  Сейчас каждое сочетание по очереди закрасит экран зелёным.");
        say("  Смотри на рабочий стол и запомни, на каком шаге увидел зелёное.");
        say("");

        int step = 0;
        foreach (var how in Enum.GetValues<Attach>())
        {
            var (parent, name) = Target(how);
            if (parent == IntPtr.Zero || !Desktop.GetWindowRect(parent, out var pr)) continue;

            foreach (var tech in Enum.GetValues<Technique>())
            {
                step++;
                say($"  шаг {step}: {name} + {TechName(tech)}");

                var made = new List<IntPtr>();
                foreach (var m in Desktop.Monitors())
                {
                    var h = MakeProbe(parent, tech, m, pr);
                    if (h != IntPtr.Zero) made.Add(h);
                }

                Pump(secondsEach * 1000);
                foreach (var h in made) DestroyWindow(h);
                Pump(400);
            }
        }

        say("");
        say($"  всего шагов: {step}. Скажи номер шага, на котором экран позеленел.");
    }

    /// <summary>Отчёт для --workerw: перебор плюс, если попросили, показ прямоугольников.</summary>
    public static string SelfCheck(int seconds)
    {
        var (best, report) = ProbeAttach();
        var lines = new List<string> { "  способы привязки:", report, "" };

        if (best is null)
        {
            lines.Add("  ни один способ не сработал — видео-обои на этой сборке недоступны");
            return string.Join('\n', lines);
        }

        var (how, tech) = best.Value;
        lines.Add($"  рабочий способ: {Target(how).Name} + {TechName(tech)}");
        if (seconds <= 0) return string.Join('\n', lines);

        var parent = Target(how).Parent;
        Desktop.GetWindowRect(parent, out var pr);

        var made = new List<IntPtr>();
        foreach (var m in Desktop.Monitors())
        {
            var h = MakeProbe(parent, tech, m, pr);
            if (h != IntPtr.Zero) made.Add(h);
        }
        Pump(seconds * 1000);
        foreach (var h in made) DestroyWindow(h);

        lines.Add($"  показано прямоугольников: {made.Count}, {seconds} с, убраны");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Убрать окна слоя до того, как трогать COM оболочки. Иначе SetParent связывает
    /// наш поток с потоком Explorer, и синхронный вызов COM вешает рабочий стол.
    /// </summary>
    public void Teardown()
    {
        foreach (var id in _surfaces.Keys.ToArray()) Drop(id);
        _fromPlaylist.Clear();
        _pause.Stop();
    }

    public void Dispose()
    {
        _repark.Stop();
        _pause.Dispose();
        foreach (var id in _surfaces.Keys.ToArray()) Drop(id);
        _watcher.RemoveHook(OnMessage);
        _watcher.Dispose();
    }
}
