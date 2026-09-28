using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace Wallpaper;

public sealed class PlaylistEngine : IDisposable
{
    /// <summary>Картинки: их ставит Windows как обычные обои.</summary>
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp", ".avif" };

    /// <summary>Фильтр для диалогов выбора картинок — из того же списка, чтобы новый формат не забывался в диалогах.</summary>
    public static string ImageFilter =>
        "Изображения|" + string.Join(";", ImageExt.Select(e => "*" + e)) + "|Все файлы|*.*";

    /// <summary>Движущееся: играет слой рабочего стола. Анимированный gif — только явным режимом «Видео».</summary>
    static readonly HashSet<string> MovingExt = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv", ".html", ".htm" };

    static bool Known(string path) => ImageExt.Contains(Path.GetExtension(path))
                                   || MovingExt.Contains(Path.GetExtension(path));

    public static bool IsMoving(string path) => MovingExt.Contains(Path.GetExtension(path));

    /// <summary>
    /// Метка «не показывать» в начале имени файла. Живёт в самом имени, а не в конфиге:
    /// переживает переезд папки, видна в проводнике и понятна без приложения.
    /// </summary>
    public const string HiddenMark = "(-)";

    public static bool IsHidden(string path) =>
        Path.GetFileName(path).StartsWith(HiddenMark, StringComparison.Ordinal);

    /// <summary>
    /// Спрятать файл от слайд-шоу — дописать метку в начало имени. Возвращает новое имя,
    /// уже спрятанный файл отдаёт как есть, а при неудаче — null.
    /// </summary>
    public static string? Hide(string path)
    {
        if (IsHidden(path)) return path;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null) return null;

            var target = Path.Combine(dir, HiddenMark + Path.GetFileName(path));
            if (File.Exists(target)) return null;   // тёзка уже лежит рядом, чужое не трогаем

            File.Move(path, target);
            return target;
        }
        catch (Exception e)
        {
            Paths.Write($"спрятать {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    // ---------- удаление в корзину ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    const uint FO_DELETE = 0x0003;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;      // то самое «в корзину, а не насовсем»
    const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// Убрать файл в корзину. Именно в корзину: горячая клавиша срабатывает мгновенно и без
    /// вопросов, так что промах обязан быть обратимым. File.Delete стирал бы насовсем.
    /// </summary>
    public static bool Recycle(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            // Список файлов для шелла — с двойным нулём в конце.
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };

            int rc = SHFileOperationW(ref op);
            if (rc != 0 || op.fAnyOperationsAborted != 0)
            {
                Paths.Write($"в корзину {Path.GetFileName(path)}: код {rc}");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Paths.Write($"в корзину {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    // ponytail: тик раз в 5 с вместо таймера на точное время следующей смены.
    // Погрешность ±5 с на интервале в 15 минут никого не волнует; станет важно —
    // считать min(DueUtc) и заводить таймер ровно на него.
    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // ponytail: 32 наблюдаемых папки. Больше — переходить на один watcher на корень диска.
    const int MaxWatchers = 32;

    readonly DispatcherTimer _tick;
    readonly DispatcherTimer _rescan;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly Dictionary<string, string[]> _all = [];      // всё, что нашлось в папках
    readonly Dictionary<string, string[]> _order = [];    // то же минус выключенные — в этом и крутится

    Config _cfg;
    readonly State _st;

    /// <summary>Обои сменились или список файлов пересобран — трею пора обновить подпись.</summary>
    public event Action? Changed;

    /// <summary>Список файлов пересобран — сеткам миниатюр пора перерисоваться.</summary>
    public event Action? Rescanned;

    /// <summary>Наступило время сменить плейлист по расписанию. Применяет тот, кто владеет конфигом.</summary>
    public event Action<string>? ScheduleWantsPlaylist;

    /// <summary>Пора проверить, живы ли окна видеослоя.</summary>
    public event Action? VerifyLayer;

    /// <summary>
    /// Показать файл на мониторе. Чем именно — обоями Windows или слоем рабочего стола —
    /// решает владелец слоя: движку это знать незачем. Ложь — файл не подошёл, берём следующий.
    /// </summary>
    public Func<string, string, bool> Present { get; set; } = (_, _) => false;

    string? _appliedRule;

    /// <summary>Первый проход после запуска: на мониторах ещё чужие обои, их надо перекрыть.</summary>
    bool _firstSync = true;

    public PlaylistEngine(Config cfg, State st)
    {
        _cfg = cfg;
        _st = st;

        var d = Dispatcher.CurrentDispatcher;
        _tick = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick(), d);
        _rescan = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Rescan(), d);
        _rescan.Stop();
    }

    /// <summary>Стоит ли слайд-шоу на этом мониторе.</summary>
    public bool IsPaused(string monId) =>
        _st.PausedMonitors.Contains(monId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Переключить паузу на мониторе. Возвращает новое состояние.</summary>
    public bool TogglePaused(string monId)
    {
        bool on = !IsPaused(monId);
        if (on) _st.PausedMonitors.Add(monId);
        else _st.PausedMonitors.RemoveAll(x => string.Equals(x, monId, StringComparison.OrdinalIgnoreCase));

        // Снимая паузу, отсчитываем интервал заново: иначе картинка сменится сразу же,
        // хотя пауза как раз и была нужна, чтобы её задержать.
        if (!on && _st.Cursors.TryGetValue(monId, out var pos))
            pos.DueUtc = NextDue(pos.PlaylistId);

        _st.Save();
        Changed?.Invoke();
        return on;
    }

    /// <summary>Из чего строится список файлов, в виде строки — чтобы заметить, что это изменилось.</summary>
    string _sources = "";

    public void Reload(Config cfg)
    {
        _cfg = cfg;

        // Настройки сохраняются на каждую галку, а полный обход папок на большой или сетевой
        // библиотеке подвешивает окно. Поэтому список пересобираем, только если изменилось
        // то, из чего он строится. Появление файлов в самих папках ловит watcher, а если
        // он промолчал — есть Refresh.
        var sources = JsonSerializer.Serialize(
            cfg.Playlists.Select(p => new { p.Id, p.Folders, p.Files, p.Excluded, p.Order }), Config.Json);
        if (sources != _sources)
        {
            _sources = sources;
            Forget();
            BuildWatchers();
        }
        Sync();
    }

    // ---------- применение конфига ----------

    /// <summary>Разложить обои по мониторам согласно конфигу и сохранённому состоянию.</summary>
    public void Sync()
    {
        if (!_cfg.Enabled) return;

        Desktop.SetPosition(_cfg.Fit);
        // В остальных режимах цвет полей зависит от самой картинки и выставляется на каждую
        // отдельно, а в режиме размытия полей не остаётся вовсе.
        if (_cfg.Background == BackgroundMode.Color && !string.IsNullOrWhiteSpace(_cfg.BackgroundColor))
            Desktop.SetBackgroundColor(_cfg.BackgroundColor!);

        foreach (var mon in Desktop.Monitors())
        {
            if (!_cfg.Monitors.TryGetValue(mon.Id, out var mc)) continue;
            switch (mc.Mode)
            {
                case Mode.Static when !string.IsNullOrWhiteSpace(mc.Path):
                    Desktop.SetWallpaper(mon.Id, mc.Path!);
                    break;
                case Mode.Playlist when mc.PlaylistId is not null:
                    SyncPlaylist(mon.Id, mc.PlaylistId);
                    break;
            }
        }
        // Монитор ушёл из режима плейлиста — курсор больше не нужен, иначе он вечно "просрочен"
        // и Tick дёргает Advance вхолостую каждые 5 секунд.
        foreach (var id in _st.Cursors.Keys.ToArray())
            if (_cfg.Monitors.GetValueOrDefault(id)?.Mode != Mode.Playlist) _st.Cursors.Remove(id);

        _st.Save();
        _firstSync = false;
        Changed?.Invoke();
    }

    void SyncPlaylist(string monId, string plId)
    {
        var order = Order(plId);
        if (order.Length == 0) return;

        var pos = _st.Cursors.GetValueOrDefault(monId);
        bool stale = pos is null
                  || pos.PlaylistId != plId
                  || pos.Path is null
                  || Array.IndexOf(order, pos.Path) < 0;

        if (stale)
        {
            // Вперемешку — со случайного места. По порядку разные мониторы стартуют с разных
            // мест, иначе на всех одна и та же картинка.
            Show(monId, plId, order, IsRandom(plId)
                ? Random.Shared.Next(order.Length)
                : Mod(StableHash(monId), order.Length));
        }
        else if (!IsPaused(monId) && DateTime.UtcNow >= pos!.DueUtc)
        {
            Advance(monId, 1);
        }
        else if (_firstSync)
        {
            // При выходе мы вернули на монитор обои пользователя, поэтому наших там сейчас нет:
            // на первом проходе после запуска показываем то, что было открыто до закрытия.
            // Курсор и таймер при этом не трогаем — картинка досидит свой интервал.
            Present(monId, pos!.Path!);
        }
        // дальше ничего не трогаем: перевыставлять на каждое изменение конфига — лишнее мигание
    }

    // ---------- переключение ----------

    public void Advance(string monId, int delta)
    {
        if (!_cfg.Monitors.TryGetValue(monId, out var mc)
            || mc.Mode != Mode.Playlist
            || mc.PlaylistId is null) return;

        var order = Order(mc.PlaylistId);
        if (order.Length == 0) return;

        var pos = _st.Cursors.GetValueOrDefault(monId);
        int i = pos?.Path is string p ? Array.IndexOf(order, p) : -1;

        if (!IsRandom(mc.PlaylistId))
        {
            Show(monId, mc.PlaylistId, order, Step(order.Length, i, delta));
            return;
        }

        // Вперемешку: каждый следующий — честно случайный, назад — по истории показов.
        if (delta < 0) { Back(monId, mc.PlaylistId, order); return; }
        Remember(monId, pos?.Path);
        Show(monId, mc.PlaylistId, order, RandomIndex(order.Length, i, Random.Shared));
    }

    bool IsRandom(string? plId) => _cfg.Playlist(plId)?.Order == OrderMode.Shuffle;

    /// <summary>Сколько показов помнит «Предыдущая».</summary>
    const int HistoryMax = 20;

    void Remember(string monId, string? path)
    {
        if (path is null) return;
        var h = _st.History.TryGetValue(monId, out var list) ? list : _st.History[monId] = [];
        h.Add(path);
        if (h.Count > HistoryMax) h.RemoveRange(0, h.Count - HistoryMax);
    }

    /// <summary>Шаг назад по истории. Удалённое и выключенное пропускаем; история пуста — стоим.</summary>
    void Back(string monId, string plId, string[] order)
    {
        if (!_st.History.TryGetValue(monId, out var h)) return;
        while (h.Count > 0)
        {
            var path = h[^1];
            h.RemoveAt(h.Count - 1);
            int i = Array.IndexOf(order, path);
            if (i >= 0) { Show(monId, plId, order, i); return; }
        }
        _st.Save();   // история могла сократиться на одни пропуски
    }

    public void AdvanceAll(int delta)
    {
        foreach (var mon in Desktop.Monitors()) Advance(mon.Id, delta);
    }

    /// <summary>Показать order[start]; если файл не открылся — пробовать следующие по Attempts.</summary>
    void Show(string monId, string plId, string[] order, int start)
    {
        foreach (int i in Attempts(order.Length, start, IsRandom(plId), Random.Shared))
        {
            var path = order[i];
            if (!Present(monId, path)) continue;

            _st.Cursors[monId] = new PlayPos
            {
                PlaylistId = plId,
                Path = path,
                DueUtc = NextDue(plId),
            };
            _st.Save();
            Changed?.Invoke();
            return;
        }
        // Не открылся ни один. Тик иначе пробовал бы заново каждые 5 с и писал это в лог
        // столько же раз, а файлы за 5 с не починятся — ждём обычный интервал.
        if (_st.Cursors.TryGetValue(monId, out var pos)) pos.DueUtc = NextDue(plId);
        Paths.Write($"плейлист {plId}: ни один файл не открылся ({order.Length} шт.)");
    }

    /// <summary>Когда менять картинку в следующий раз. Меньше 5 с не бывает: тик реже не умеет.</summary>
    DateTime NextDue(string? plId) =>
        DateTime.UtcNow.AddSeconds(Math.Max(5, _cfg.Playlist(plId)?.IntervalSeconds ?? 900));

    int _ticksToMonitorRefresh;

    void Tick()
    {
        // Снимок мониторов обновляем раз в минуту: горячее подключение экрана
        // WM_DISPLAYCHANGE ловит не всегда.
        if (--_ticksToMonitorRefresh <= 0) { _ticksToMonitorRefresh = 12; Desktop.RefreshMonitors(); }

        VerifyLayer?.Invoke();
        CheckSchedule();
        if (!_cfg.Enabled) return;

        var now = DateTime.UtcNow;
        var due = _st.Cursors.Where(kv => now >= kv.Value.DueUtc).Select(kv => kv.Key).ToArray();
        if (due.Length == 0) return;

        var live = Desktop.Monitors().Select(m => m.Id).ToHashSet();
        foreach (var id in due)
        {
            if (IsPaused(id)) continue;                       // на паузе — просто ждём
            if (live.Contains(id)) Advance(id, 1);
            else _st.Cursors[id].DueUtc = now.AddMinutes(5);   // монитор отключён, не крутить вхолостую
        }
    }

    void CheckSchedule()
    {
        var rule = ActiveRule(_cfg.Schedule, TimeOnly.FromDateTime(DateTime.Now));
        if (rule is null || rule.From == _appliedRule) return;

        _appliedRule = rule.From;
        if (rule.PlaylistId is not null && _cfg.Playlist(rule.PlaylistId) is not null)
        {
            Paths.Write($"расписание {rule.From}: плейлист {rule.PlaylistId}");
            ScheduleWantsPlaylist?.Invoke(rule.PlaylistId);
        }
    }

    /// <summary>
    /// Правило, действующее в этот момент: последнее, чьё время уже наступило.
    /// До первого правила суток действует последнее правило предыдущих — расписание кольцевое.
    /// </summary>
    public static ScheduleRule? ActiveRule(IReadOnlyList<ScheduleRule> rules, TimeOnly now)
    {
        var valid = new List<(TimeOnly At, ScheduleRule Rule)>();
        foreach (var r in rules)
            if (TimeOnly.TryParse(r.From, CultureInfo.InvariantCulture, out var t)) valid.Add((t, r));

        if (valid.Count == 0) return null;
        valid.Sort((a, b) => a.At.CompareTo(b.At));

        (TimeOnly At, ScheduleRule Rule)? best = null;
        foreach (var v in valid) if (v.At <= now) best = v;
        return (best ?? valid[^1]).Rule;
    }

    public string? CurrentPath(string monId) => _st.Cursors.GetValueOrDefault(monId)?.Path;

    /// <summary>
    /// Поставить конкретную картинку сейчас. Если она из плейлиста этого монитора —
    /// слайд-шоу продолжится с неё, а не прыгнет обратно на прежнее место.
    /// Показ идёт тем же путём, что и в слайд-шоу: прямая установка обоев Windows
    /// снесла бы видеослой на соседнем мониторе.
    /// </summary>
    public void ShowNow(string monId, string path)
    {
        var before = CurrentPath(monId);
        if (!Present(monId, path)) return;

        var plId = _cfg.Monitors.GetValueOrDefault(monId)?.PlaylistId;
        if (plId is not null && Array.IndexOf(Order(plId), path) >= 0)
        {
            // Выбранное руками — тоже показ: «Предыдущая» должна вернуть то, что было до него.
            if (IsRandom(plId)) Remember(monId, before);
            _st.Cursors[monId] = new PlayPos
            {
                PlaylistId = plId,
                Path = path,
                DueUtc = NextDue(plId),
            };
            _st.Save();
        }
        Changed?.Invoke();
    }

    // ---------- список файлов ----------

    void Forget() { _all.Clear(); _order.Clear(); }

    /// <summary>То, что реально крутится: всё найденное за вычетом выключенных вручную.</summary>
    public string[] Order(string plId)
    {
        if (_order.TryGetValue(plId, out var cached)) return cached;

        var all = All(plId);
        var ex = _cfg.Playlist(plId)?.Excluded;
        if (ex is null || ex.Count == 0) return _order[plId] = all;

        var off = new HashSet<string>(ex, StringComparer.OrdinalIgnoreCase);
        return _order[plId] = all.Where(f => !off.Contains(f)).ToArray();
    }

    /// <summary>
    /// Показать заново то, что уже показано. Нужно, когда сменился не файл, а способ показа:
    /// заполнение полей, цвет. Курсор и таймер при этом не двигаются.
    /// </summary>
    public void Repaint()
    {
        foreach (var mon in Desktop.Monitors())
        {
            if (_cfg.Monitors.GetValueOrDefault(mon.Id)?.Mode != Mode.Playlist) continue;
            if (_st.Cursors.GetValueOrDefault(mon.Id)?.Path is string p) Present(mon.Id, p);
        }
    }

    /// <summary>
    /// Всё, что нашлось, включая выключенное — сетке в настройках нужно показать и его,
    /// иначе выключенный файл негде включить обратно.
    /// </summary>
    public string[] All(string plId)
    {
        if (_all.TryGetValue(plId, out var cached)) return cached;

        var pl = _cfg.Playlist(plId);
        if (pl is null) return _all[plId] = [];

        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in pl.Files)
        {
            try { if (Known(f) && !IsHidden(f) && File.Exists(f)) files.Add(Path.GetFullPath(f)); }
            catch { }
        }

        foreach (var d in pl.Folders)
        {
            if (string.IsNullOrWhiteSpace(d.Path) || !Directory.Exists(d.Path)) continue;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = d.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            try
            {
                foreach (var f in Directory.EnumerateFiles(d.Path, "*", opts))
                    if (Known(f) && !IsHidden(f)) files.Add(f);
            }
            catch (Exception e) { Paths.Write($"скан {d.Path}: {e.Message}"); }
        }

        return _all[plId] = files.ToArray();
    }

    // ---------- чистые функции, их и проверяет SelfTest ----------

    /// <summary>
    /// Переложить файлы в выбранном порядке. Дата создания для скопированного в папку файла —
    /// это момент его появления там, то самое «недавно добавленные».
    /// </summary>
    public static string[] SortFiles(string[] files, SortMode mode) => mode switch
    {
        SortMode.Name => [.. files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)],
        SortMode.NewestFirst => [.. files.OrderByDescending(Created)],
        SortMode.OldestFirst => [.. files.OrderBy(Created)],
        _ => files,
    };

    static DateTime Created(string path)
    {
        try { return File.GetCreationTimeUtc(path); }
        catch { return DateTime.MinValue; }   // файл исчез между сканом и сортировкой
    }


    public static int Mod(int value, int count) => count <= 0 ? -1 : (value % count + count) % count;

    public static int Step(int count, int current, int delta) => Mod(current + delta, count);

    /// <summary>
    /// В каком порядке пробовать файлы: сначала start, дальше по списку. Вперемешку — дальше
    /// в случайном порядке: иначе каждый нерабочий файл отдавал свой шанс соседу по алфавиту.
    /// </summary>
    public static int[] Attempts(int count, int start, bool random, Random rnd)
    {
        var a = new int[count];
        for (int k = 0; k < count; k++) a[k] = Step(count, start, k);
        if (random && count > 1) rnd.Shuffle(a.AsSpan(1));
        return a;
    }

    /// <summary>String.GetHashCode рандомизирован между запусками — порядок бы плыл после перезапуска.</summary>
    public static int StableHash(string s)
    {
        unchecked
        {
            int h = (int)2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return h;
        }
    }

    /// <summary>
    /// Случайный номер из count, кроме текущего: иначе «следующая» иногда ничего не меняла бы.
    /// Остальные номера равновероятны.
    /// </summary>
    public static int RandomIndex(int count, int current, Random rnd)
    {
        if (count <= 1) return 0;
        if (current < 0) return rnd.Next(count);   // текущего нет — выбираем из всех
        int i = rnd.Next(count - 1);
        return i >= current ? i + 1 : i;
    }

    // ---------- слежение за папками ----------

    void BuildWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();

        foreach (var pl in _cfg.Playlists)
            foreach (var d in pl.Folders)
            {
                if (_watchers.Count >= MaxWatchers) return;
                if (string.IsNullOrWhiteSpace(d.Path) || !Directory.Exists(d.Path)) continue;
                try
                {
                    var w = new FileSystemWatcher(d.Path)
                    {
                        IncludeSubdirectories = d.Recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true,
                    };
                    w.Created += (_, _) => QueueRescan();
                    w.Deleted += (_, _) => QueueRescan();
                    w.Renamed += (_, _) => QueueRescan();
                    w.Error += (_, _) => QueueRescan();      // переполнение буфера — пересканировать целиком
                    _watchers.Add(w);
                }
                catch (Exception e) { Paths.Write($"watcher {d.Path}: {e.Message}"); }
            }
    }

    void QueueRescan() => _rescan.Dispatcher.BeginInvoke(() => { _rescan.Stop(); _rescan.Start(); });

    void Rescan()
    {
        _rescan.Stop();
        Forget();
        Rescanned?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>
    /// Перечитать папки прямо сейчас. Слежение за файловой системой пропускает изменения
    /// чаще, чем хотелось бы — по сети, на съёмных дисках, при массовом копировании, —
    /// поэтому у человека должна быть кнопка. Заодно пересобираем наблюдателей: папку
    /// могли создать уже после запуска, и следить тогда было не за чем.
    /// </summary>
    public void Refresh()
    {
        BuildWatchers();
        Rescan();
    }

    public void Dispose()
    {
        _tick.Stop();
        _rescan.Stop();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
