using System.Globalization;
using System.Runtime.InteropServices;

namespace Wallpaper;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

/// <summary>
/// shobjidl_core.h. Методы объявлены строго в порядке vtable и обрезаны на GetPosition —
/// всё, что ниже (SetSlideshow, AdvanceSlideshow, Enable), нам не нужно: свой таймер
/// слайд-шоу умеет то, чего встроенный не умеет — разные плейлисты на разных мониторах.
/// </summary>
[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
                      [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    uint GetMonitorDevicePathCount();

    RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

    void SetBackgroundColor(uint color);
    uint GetBackgroundColor();
    void SetPosition(FitMode position);
    FitMode GetPosition();
}

public static class Desktop
{
    static readonly Guid CLSID_DesktopWallpaper = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    [ThreadStatic] static IDesktopWallpaper? _dw;

    static IDesktopWallpaper Dw => _dw ??=
        (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_DesktopWallpaper)!)!;

    // ================= почему весь COM на своём потоке =================
    //
    // IDesktopWallpaper живёт в Explorer. Наши окна с видео — дети его WorkerW, и после
    // SetParent потоки оказываются связаны. Блокирующий вызов этого COM из UI-потока даёт
    // круговое ожидание: Explorer шлёт сообщение нашему окну и ждёт, наш поток ждёт возврата
    // из COM. Виснет не только приложение — весь рабочий стол системы, у любого процесса.
    //
    // Разрывается это одним правилом: UI-поток никогда не ждёт этот COM. Вся работа с ним
    // уходит на отдельный STA-поток, который не владеет ничем внутри Explorer.

    static readonly ComThread Com = new("WallpaperCom");

    /// <summary>Отправить работу COM-потоку и не ждать её. Единственный способ не словить блокировку.</summary>
    static void Post(Action job) => Com.Post(job);

    // ================= снимок мониторов =================

    static volatile List<Display> _monitors = [];
    static bool _haveSnapshot;

    /// <summary>
    /// Список мониторов из последнего снимка. Никогда не блокирует: обновляется на COM-потоке.
    /// Первый вызов делается синхронно — на старте окон в слое ещё нет, зависнуть не на чем.
    /// </summary>
    public static List<Display> Monitors()
    {
        if (!_haveSnapshot)
        {
            _haveSnapshot = true;
            _monitors = ReadMonitors();
        }
        return _monitors;
    }

    /// <summary>
    /// Перечитать мониторы в фоне. Пустой ответ не принимаем: COM оболочки временами
    /// отвечает пустотой прямо во время перерисовки рабочего стола, а по пустому списку
    /// видеослой сносит все свои окна.
    /// </summary>
    public static void RefreshMonitors() => Post(() =>
    {
        var fresh = ReadMonitors();
        if (fresh.Count == 0 && _monitors.Count > 0)
        {
            Paths.Write("снимок мониторов пришёл пустым — оставили прежний");
            return;
        }
        _monitors = fresh;
    });

    public sealed record Display(string Id, int X, int Y, int W, int H)
    {
        public bool Primary => X == 0 && Y == 0;
        public bool Contains(int px, int py) => px >= X && px < X + W && py >= Y && py < Y + H;
    }

    /// <summary>Только подключённые мониторы. Отключённые остаются в перечислении, но без RECT.</summary>
    static List<Display> ReadMonitors()
    {
        var list = new List<Display>();
        uint n;
        try { n = Dw.GetMonitorDevicePathCount(); } catch (Exception e) { Paths.Write($"GetMonitorDevicePathCount: {e.Message}"); return list; }

        for (uint i = 0; i < n; i++)
        {
            string id;
            try { id = Dw.GetMonitorDevicePathAt(i); } catch { continue; }
            if (string.IsNullOrEmpty(id)) continue;

            RECT r;
            try { r = Dw.GetMonitorRECT(id); } catch { continue; }   // монитор отключён
            // GetMonitorRECT может вернуть S_FALSE с нулевым прямоугольником — это не исключение.
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) continue;

            list.Add(new Display(id, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }
        return list;
    }

    /// <summary>
    /// Ставит обои. Существование файла проверяется здесь и сразу, сам COM-вызов уходит
    /// на свой поток и результата не возвращает — ждать его нельзя.
    /// </summary>
    public static bool SetWallpaper(string monitorId, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
            if (!File.Exists(full)) return false;
        }
        catch { return false; }

        Post(() =>
        {
            // Оболочка отвечает «файл не найден» и на неизвестный ей монитор — без пути
            // и монитора в сообщении такую жалобу не разобрать.
            try { Dw.SetWallpaper(monitorId, full); }
            catch (Exception e) { Paths.Write($"обои не поставились: {full} на {monitorId[..24]}… — {e.Message}"); }
        });
        WallpaperApplied?.Invoke(monitorId, full);
        return true;
    }

    /// <summary>Мы поставили обои. Слушатель запоминает это, чтобы потом отличить свои от чужих.</summary>
    public static event Action<string, string>? WallpaperApplied;

    public static string? CurrentWallpaper(string monitorId)
    {
        try { var p = Dw.GetWallpaper(monitorId); return string.IsNullOrEmpty(p) ? null : p; }
        catch { return null; }
    }

    public static void SetPosition(FitMode fit) => Post(() => Dw.SetPosition(fit));

    /// <summary>Текущее вписывание. Блокирует — звать только там, где окон в слое ещё нет.</summary>
    public static FitMode? CurrentPosition()
    {
        try { return Dw.GetPosition(); } catch { return null; }
    }

    /// <summary>
    /// Дождаться, пока COM-поток разберёт очередь. Безопасно только когда наших окон
    /// в слое уже нет — то есть при выходе, после сноса видеослоя.
    /// </summary>
    public static void Flush(int timeoutMs = 3000) => Com.Flush(timeoutMs);

    /// <summary>"#RRGGBB" -> COLORREF (0x00BBGGRR).</summary>
    public static void SetBackgroundColor(string hex)
    {
        if (!TryParseColor(hex, out uint colorref)) return;
        Post(() => Dw.SetBackgroundColor(colorref));
    }

    // ================= WorkerW: слой за иконками рабочего стола =================
    //
    // Единственное недокументированное место во всём приложении. Ломается на обновлениях
    // Windows, поэтому живёт в одном месте и имеет два запасных пути.

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                             uint flags, uint timeoutMs, out IntPtr result);

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hwnd, char[] buffer, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    internal static string ClassOf(IntPtr hwnd)
    {
        var buf = new char[256];
        int n = GetClassNameW(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    internal static string ProcessOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return $"pid {pid}"; }
    }

    /// <summary>Все окна верхнего уровня класса Progman или WorkerW — для диагностики.</summary>
    internal static List<(IntPtr Hwnd, string Class, RECT Rect, bool Visible, bool HasDefView)> DesktopWindows()
    {
        var found = new List<(IntPtr, string, RECT, bool, bool)>();
        EnumWindows((h, _) =>
        {
            var cls = ClassOf(h);
            if (cls is not ("Progman" or "WorkerW")) return true;
            GetWindowRect(h, out var r);
            bool defView = FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
            found.Add((h, cls, r, IsWindowVisible(h), defView));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Всё, что живёт внутри Progman. Здесь же видно чужие менеджеры обоев: если в слое
    /// есть окна не от explorer — кто-то уже занял место.
    /// </summary>
    internal static List<(IntPtr Hwnd, string Class, RECT Rect, bool Visible, string Process)> DesktopChildren()
    {
        var found = new List<(IntPtr, string, RECT, bool, string)>();
        var progman = Progman();
        if (progman == IntPtr.Zero) return found;

        EnumChildWindows(progman, (h, _) =>
        {
            GetWindowRect(h, out var r);
            found.Add((h, ClassOf(h), r, IsWindowVisible(h), ProcessOf(h)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Отправить Progman сообщение, которое разводит обои и иконки по разным окнам.</summary>
    internal static void PokeProgman(IntPtr progman, bool win11Variant)
    {
        if (win11Variant) SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);
        else SendMessageTimeoutW(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
    }

    internal static IntPtr Progman() => FindWindowW("Progman", null);

    /// <summary>WorkerW внутри Progman — раскладка Windows 11 24H2+.</summary>
    internal static IntPtr WorkerWInsideProgman(IntPtr progman) =>
        progman == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);

    /// <summary>
    /// Классическая раскладка: WorkerW верхнего уровня сразу за окном с иконками.
    /// Обязательно проверяем размер — в системе десятки служебных WorkerW по 136×39,
    /// и без проверки находится первый попавшийся мусор.
    /// </summary>
    internal static IntPtr WorkerWSibling()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            var next = FindWindowExW(IntPtr.Zero, h, "WorkerW", null);
            if (next == IntPtr.Zero || !IsDesktopSized(next)) return true;
            found = next;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Видимые окна верхнего уровня во весь монитор и шире, кроме самого рабочего стола.
    /// Прозрачный оверлей на весь экран мешает и увидеть слой, и измерить цвет пикселя.
    /// </summary>
    internal static List<(string Class, string Process)> FullScreenTopLevel()
    {
        var found = new List<(string, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
            if (r.Right - r.Left < 2000 || r.Bottom - r.Top < 1000) return true;
            var cls = ClassOf(h);
            if (cls is "Progman" or "WorkerW") return true;
            found.Add((cls, ProcessOf(h)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Список иконок рабочего стола. Наше окно должно оказаться ровно под ним.</summary>
    internal static IntPtr IconList(IntPtr defView) =>
        defView == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);

    /// <summary>Окно размером с монитор и больше — то есть кандидат в слой обоев.</summary>
    internal static bool IsDesktopSized(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) && r.Right - r.Left >= 800 && r.Bottom - r.Top >= 600;

    /// <summary>Контейнер иконок рабочего стола. Обычно ребёнок Progman, иногда — WorkerW.</summary>
    internal static IntPtr DefView(IntPtr progman)
    {
        var dv = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (dv != IntPtr.Zero) return dv;

        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var d = FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (d == IntPtr.Zero) return true;
            found = d;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Куда именно удалось прицепиться — видно в `--workerw`, если что-то пойдёт не так.</summary>
    public static string LastWorkerWRoute { get; private set; } = "не искали";

    /// <summary>
    /// Окно, которое Explorer рисует ПОД иконками рабочего стола. Наши окна с видео
    /// становятся его детьми — тогда иконки и подписи остаются поверх видео.
    /// </summary>
    public static IntPtr FindWorkerW()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            LastWorkerWRoute = "Progman не найден";
            return IntPtr.Zero;
        }

        // 0x052C просит Explorer развести обои и иконки по разным окнам.
        // Первый вариант работает на Win10 и большинстве Win11, второй нужен с 22H2.
        SendMessageTimeoutW(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
        SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);

        // Классика (Win10 и ранние Win11): top-level окно с SHELLDLL_DefView внутри,
        // а нужный WorkerW — следующий за ним сосед в z-порядке.
        var sibling = WorkerWSibling();
        if (sibling != IntPtr.Zero)
        {
            LastWorkerWRoute = "WorkerW рядом с SHELLDLL_DefView";
            return sibling;
        }

        // Windows 11 24H2+: WorkerW никуда не выносится, он лежит ВНУТРИ Progman,
        // сразу за SHELLDLL_DefView. EnumWindows его не видит — он не верхнего уровня.
        var inside = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        if (inside != IntPtr.Zero)
        {
            LastWorkerWRoute = "WorkerW внутри Progman";
            return inside;
        }

        // Запасной путь: иконки живут прямо в Progman — вешаемся на него.
        // Видно хуже (в некоторых конфигурациях перекрывает иконки), но лучше, чем ничего.
        LastWorkerWRoute = "WorkerW не найден, запасной путь через Progman";
        return progman;
    }

    public static bool TryParseColor(string hex, out uint colorref)
    {
        colorref = 0;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        colorref = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        return true;
    }
}

/// <summary>
/// Отдельный STA-поток под COM оболочки Windows. Нужен по одной причине: наши окна живут
/// внутри окон Explorer, и блокирующий вызов его же COM из UI-потока даёт круговое ожидание —
/// виснет и приложение, и рабочий стол системы. Работа сюда только отправляется, ждать её нельзя.
/// </summary>
internal sealed class ComThread
{
    readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    readonly string _name;
    Thread? _thread;
    readonly Lock _lock = new();

    public ComThread(string name) => _name = name;

    void Ensure()
    {
        lock (_lock)
        {
            if (_thread is not null) return;
            _thread = new Thread(() =>
            {
                foreach (var job in _queue.GetConsumingEnumerable())
                {
                    try { job(); }
                    catch (Exception e) { Paths.Write($"{_name}: {e.Message}"); }
                }
            })
            { IsBackground = true, Name = _name };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public void Post(Action job)
    {
        Ensure();
        try { _queue.Add(job); } catch (InvalidOperationException) { }
    }

    /// <summary>Дождаться разбора очереди. Безопасно только когда наших окон в слое Explorer уже нет.</summary>
    public void Flush(int timeoutMs)
    {
        if (_thread is null) return;
        using var done = new ManualResetEventSlim(false);
        Post(done.Set);
        done.Wait(timeoutMs);
    }
}
