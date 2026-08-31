using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;

namespace Wallpaper;

/// <summary>
/// Служебные ключи командной строки. Проверка нетривиальной логики: арифметика курсора, детерминированный шаффл,
/// разбор горячих клавиш, формат конфига. Запуск: Wallpaper.exe --selftest, Wallpaper.exe --monitors
/// </summary>
internal static class Cli
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();

    static int _failed;

    static void OpenConsole()
    {
        if (!AttachConsole(-1)) AllocConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
    }

    /// <summary>Печатает ID мониторов — их нужно вставлять в config.json, пока нет окна настроек.</summary>
    public static int Monitors()
    {
        OpenConsole();
        var mons = Desktop.Monitors();
        if (mons.Count == 0) { Console.WriteLine("  мониторов не видно\n"); return 1; }

        for (int i = 0; i < mons.Count; i++)
        {
            var m = mons[i];
            Console.WriteLine($"  Монитор {i + 1} — {m.W}×{m.H} @ {m.X},{m.Y}{(m.Primary ? "  (основной)" : "")}");
            Console.WriteLine($"    id:    {m.Id}");
            Console.WriteLine($"    обои:  {Desktop.CurrentWallpaper(m.Id) ?? "—"}");
            Console.WriteLine();
        }
        Console.WriteLine($"  config.json: {Paths.Config}\n");
        return 0;
    }

    /// <summary>
    /// Проверка слоя за иконками рабочего стола — самого хрупкого места приложения.
    /// На несколько секунд кладёт на каждый монитор зелёный прямоугольник: если он виден
    /// позади иконок, видео-обои на этой сборке Windows заработают.
    /// </summary>
    public static int WorkerW()
    {
        OpenConsole();
        Report("  слой за иконками рабочего стола\n");
        Report(VideoLayer.Hierarchy());
        Report(VideoLayer.SelfCheck(seconds: 0));

        // Замер цвета врёт, если поверх рабочего стола лежит прозрачное окно во весь экран.
        // Тогда решает человек: --workerw --visual проходит все сочетания по очереди.
        if (Environment.GetCommandLineArgs().Contains("--visual"))
            VideoLayer.VisualWalk(secondsEach: 3, Report);

        // То же самое, что приложение решит само при включении видео.
        var pick = VideoLayer.DetectAttach();
        Report(pick is null
            ? "\n  приложение выберет: ничего, видеорежим недоступен"
            : $"\n  приложение выберет: {pick.Value.How} + {pick.Value.Tech}"
              + (pick.Value.BehindIcons ? " — видео под иконками" : " — ВИДЕО ПОВЕРХ ИКОНОК"));

        Config.TryLoad(out var cfg, out _);
        Mpv.ExtraPath = cfg.MpvPath;
        Mpv.Probe();
        Console.WriteLine();
        Console.WriteLine(Mpv.Available
            ? $"  libmpv:      {Mpv.LoadedFrom}"
            : "  libmpv:      не найден, видеорежим недоступен. Искали здесь:");
        if (!Mpv.Available)
            foreach (var p in Mpv.SearchPaths().Take(2)) Console.WriteLine($"                 {p}");
        Console.WriteLine();
        return 0;
    }

    /// <summary>Диагностику пишем и в консоль, и в журнал — её удобно приложить к жалобе.</summary>
    static void Report(string text)
    {
        Console.WriteLine(text);
        foreach (var line in text.Split('\n')) Paths.Write(line.TrimEnd());
    }

    /// <summary>
    /// Перебор бэкендов вывода mpv на настоящем файле: какой из них умеет рисовать
    /// внутри layered-окна слоя рабочего стола. Запуск: --videosweep &lt;файл&gt;
    /// </summary>
    public static int VideoSweep(string[] args)
    {
        OpenConsole();

        var path = args.SkipWhile(a => a != "--videosweep").Skip(1).FirstOrDefault();
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine("  укажи путь к видео: Wallpaper.exe --videosweep <файл>\n");
            return 1;
        }

        Config.TryLoad(out var cfg, out _);
        Mpv.ExtraPath = cfg.MpvPath;

        Report("  перебор способов вывода mpv\n");
        VideoLayer.BackendSweep(path, Report);
        Console.WriteLine();
        return 0;
    }

    public static int SelfTest()
    {
        OpenConsole();

        Cursor();
        Shuffle();
        StableHash();
        HotkeyParsing();
        ConfigRoundTrip();
        ColorParsing();
        Schedule();
        PlaylistScan();
        PausePerMonitor();

        Console.WriteLine(_failed == 0 ? "\nвсё сошлось\n" : $"\nпровалено проверок: {_failed}\n");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- курсор плейлиста ----------

    static void Cursor()
    {
        // с пустого места вперёд — первый файл
        Check(PlaylistEngine.Step(5, -1, 1) == 0, "старт с нуля");
        Check(PlaylistEngine.Step(5, 4, 1) == 0, "перенос через конец");
        Check(PlaylistEngine.Step(5, 0, -1) == 4, "назад с первого — на последний");
        Check(PlaylistEngine.Step(5, 2, -1) == 1, "шаг назад");
        Check(PlaylistEngine.Step(1, 0, 1) == 0, "один файл в плейлисте");
        Check(PlaylistEngine.Step(0, 0, 1) == -1, "пустой плейлист не делит на ноль");
        Check(PlaylistEngine.Mod(-7, 5) == 3, "отрицательный остаток положителен");
    }

    // ---------- обход папок и выключенные файлы ----------

    static void PlaylistScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "wallpaper-selftest-" + Environment.ProcessId);
        var sub = Path.Combine(root, "вложенная");
        var deep = Path.Combine(sub, "c.png");
        Directory.CreateDirectory(sub);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.png"), []);
            File.WriteAllBytes(Path.Combine(root, "b.png"), []);
            File.WriteAllBytes(deep, []);
            File.WriteAllText(Path.Combine(root, "readme.txt"), "не картинка");

            var pl = new Playlist
            {
                Id = "t",
                Order = OrderMode.Sequential,
                Folders = { new FolderRef { Path = root, Recursive = true } },
            };
            // Enabled = false: движок не должен трогать настоящие обои во время проверки.
            var cfg = new Config { Enabled = false, Playlists = { pl } };
            using var e = new PlaylistEngine(cfg, new State());

            Check(e.All("t").Length == 3, "рекурсивный обход берёт файлы из подпапки");
            Check(!e.All("t").Contains(Path.Combine(root, "readme.txt")), "чужое расширение не берём");

            pl.Folders[0].Recursive = false;
            e.Reload(cfg);
            Check(e.All("t").Length == 2, "без галки «с подпапками» вложенное не считается");

            pl.Folders[0].Recursive = true;
            pl.Excluded.Add(deep);
            e.Reload(cfg);
            Check(e.All("t").Length == 3, "выключенный файл остаётся в полном списке — иначе его не включить");
            Check(e.Order("t").Length == 2, "выключенный файл выпадает из ротации");
            Check(!e.Order("t").Contains(deep), "выпал именно выключенный");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ---------- пауза по мониторам ----------

    static void PausePerMonitor()
    {
        // TogglePaused сохраняет состояние на диск — откладываем настоящий state.json.
        var backup = File.Exists(Paths.State) ? File.ReadAllText(Paths.State) : null;
        try
        {
            var cfg = new Config { Enabled = false, Playlists = { new Playlist { Id = "t", IntervalSeconds = 600 } } };
            var st = new State();
            st.Cursors["A"] = new PlayPos { PlaylistId = "t", Path = "x.png", DueUtc = DateTime.UtcNow.AddYears(-1) };
            using var e = new PlaylistEngine(cfg, st);

            Check(!e.IsPaused("A"), "по умолчанию пауз нет");
            Check(e.TogglePaused("A"), "первое нажатие ставит на паузу");
            Check(e.IsPaused("A") && !e.IsPaused("B"), "пауза не расползается на соседний монитор");
            Check(!e.TogglePaused("A"), "второе нажатие снимает паузу");
            Check(st.Cursors["A"].DueUtc > DateTime.UtcNow.AddSeconds(500),
                "снятая пауза отсчитывает интервал заново, а не меняет картинку сразу");
        }
        finally
        {
            if (backup is not null) File.WriteAllText(Paths.State, backup);
            else File.Delete(Paths.State);
        }
    }

    // ---------- шаффл ----------

    static void Shuffle()
    {
        string[] Src() => ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];

        var x = Src(); PlaylistEngine.Shuffle(x, 42);
        var y = Src(); PlaylistEngine.Shuffle(y, 42);
        Check(x.SequenceEqual(y), "один seed — один порядок");

        var z = Src(); PlaylistEngine.Shuffle(z, 43);
        Check(!x.SequenceEqual(z), "другой seed — другой порядок");

        Check(x.OrderBy(s => s).SequenceEqual(Src()), "шаффл ничего не теряет и не дублирует");

        // курсор ходит по перемешанному списку без пропусков
        var seen = new HashSet<string>();
        int i = -1;
        for (int k = 0; k < x.Length; k++) { i = PlaylistEngine.Step(x.Length, i, 1); seen.Add(x[i]); }
        Check(seen.Count == x.Length, "полный круг показывает каждый файл ровно раз");
    }

    static void StableHash()
    {
        // Пин на конкретное значение: если кто-то заменит FNV-1a на string.GetHashCode(),
        // порядок шаффла начнёт плыть после каждого перезапуска, а тест этого не заметит.
        // Значение посчитано независимо (FNV-1a 32 бита), а не списано с реализации.
        Check(PlaylistEngine.StableHash("nature") == unchecked((int)0xA6563E8A), "StableHash не изменился");
        Check(PlaylistEngine.StableHash("a") != PlaylistEngine.StableHash("b"), "разные строки — разный хеш");
    }

    // ---------- горячие клавиши ----------

    static void HotkeyParsing()
    {
        Check(Hotkeys.TryParse("Ctrl+Alt+Right", out var m1, out var k1) && m1 == (0x2 | 0x1) && k1 == 0x27,
            "Ctrl+Alt+Right");
        Check(Hotkeys.TryParse("Ctrl+Alt+Shift+Right", out var m2, out _) && m2 == (0x2 | 0x1 | 0x4),
            "три модификатора");
        Check(Hotkeys.TryParse("Ctrl+Alt+1", out _, out var k3) && k3 == 0x31, "цифра превращается в Key.D1");
        Check(Hotkeys.TryParse("ctrl+alt+p", out _, out var k4) && k4 == 0x50, "регистр не важен");

        Check(!Hotkeys.TryParse("Right", out _, out _), "без модификатора не регистрируем");
        Check(!Hotkeys.TryParse("Ctrl+Пробел", out _, out _), "неизвестная клавиша отвергается");
        Check(!Hotkeys.TryParse("Meta+X", out _, out _), "неизвестный модификатор отвергается");
        Check(!Hotkeys.TryParse("", out _, out _), "пустая строка");

        // Format — обратная сторона: редактор в настройках пишет им, TryParse читает
        const ModifierKeys ca = ModifierKeys.Control | ModifierKeys.Alt;
        Check(Hotkeys.Format(ca, Key.Right) == "Ctrl+Alt+Right", "Format собирает комбинацию");
        Check(Hotkeys.Format(ca, Key.D1) == "Ctrl+Alt+1", "Format разворачивает D1 обратно в 1");
        Check(Hotkeys.Format(ca | ModifierKeys.Shift, Key.Right) == "Ctrl+Alt+Shift+Right", "порядок модификаторов постоянный");
        Check(Hotkeys.Format(ModifierKeys.None, Key.A) == "", "клавиша без модификаторов — не комбинация");
        Check(Hotkeys.Format(ca, Key.LeftShift) == "", "модификатор сам по себе — не комбинация");

        foreach (var combo in Config.DefaultHotkeys().Values)
        {
            Hotkeys.TryParse(combo, out var m, out var vk);
            var again = Hotkeys.Format(ToMods(m), KeyInterop.KeyFromVirtualKey((int)vk));
            Check(again == combo, $"round-trip {combo}");
        }
    }

    static ModifierKeys ToMods(uint m) =>
        (((m & 0x2) != 0) ? ModifierKeys.Control : 0) |
        (((m & 0x1) != 0) ? ModifierKeys.Alt : 0) |
        (((m & 0x4) != 0) ? ModifierKeys.Shift : 0) |
        (((m & 0x8) != 0) ? ModifierKeys.Windows : 0);

    // ---------- конфиг ----------

    static void ConfigRoundTrip()
    {
        const string monId = @"\\?\DISPLAY#SAM0F94#5&1a2b3c&0&UID4353";

        var cfg = new Config
        {
            Fit = FitMode.Fit,
            StartWithWindows = true,
            Monitors = { [monId] = new MonitorCfg { Mode = Mode.Playlist, PlaylistId = "nature" } },
            Playlists = { new Playlist { Id = "nature", Name = "Природа", IntervalSeconds = 60, Order = OrderMode.Sequential } },
        };

        var json = JsonSerializer.Serialize(cfg, Config.Json);
        Check(json.Contains("\"fit\": \"fit\""), "enum пишется строкой в camelCase");
        Check(json.Contains("\"sequential\""), "порядок пишется строкой");

        var back = JsonSerializer.Deserialize<Config>(json, Config.Json)!;
        Check(back.Fit == FitMode.Fit, "fit пережил round-trip");
        Check(back.Monitors.ContainsKey(monId), "ключ монитора с обратными слешами не покорёжен");
        Check(back.Monitors[monId].Mode == Mode.Playlist, "режим монитора пережил round-trip");
        Check(back.Playlist("nature")?.Name == "Природа", "кириллица пережила round-trip");
        Check(back.Playlist("nope") is null, "несуществующий плейлист — null, а не исключение");

        // конфиг с комментариями и висящей запятой должен читаться: люди правят его руками
        const string hand = """
        {
          // мой конфиг
          "fit": "fill",
          "playlists": [],
        }
        """;
        Check(JsonSerializer.Deserialize<Config>(hand, Config.Json)?.Fit == FitMode.Fill,
            "комментарии и висящая запятая не ломают разбор");

        var defaults = new Config();
        Check(defaults.Hotkeys.Values.All(v => Hotkeys.TryParse(v, out _, out _)),
            "все горячие клавиши по умолчанию разбираются");

        // Урезанный вручную hotkeys не должен оставлять остальные действия без клавиш.
        var trimmed = JsonSerializer.Deserialize<Config>(
            """{ "hotkeys": { "next": "Ctrl+Alt+F1" } }""", Config.Json)!;
        trimmed.FillHotkeyDefaults();
        Check(trimmed.Hotkeys["next"] == "Ctrl+Alt+F1", "своя комбинация из файла сохраняется");
        Check(trimmed.Hotkeys.Count == Config.DefaultHotkeys().Count, "остальные добираются из умолчаний");
        Check(trimmed.Hotkeys.ContainsKey("quit"), "новое действие получает клавишу и в старом конфиге");
    }

    static void ColorParsing()
    {
        Check(Desktop.TryParseColor("#FF8000", out var c) && c == 0x000080FF, "#RRGGBB превращается в COLORREF");
        Check(!Desktop.TryParseColor("#FFF", out _), "короткая запись цвета отвергается");
        Check(!Desktop.TryParseColor("зелёный", out _), "мусор в цвете отвергается");
    }

    // ---------- расписание ----------

    static void Schedule()
    {
        static ScheduleRule R(string from, string id) => new() { From = from, PlaylistId = id };
        static string? Pick(List<ScheduleRule> rules, string time) =>
            PlaylistEngine.ActiveRule(rules, TimeOnly.Parse(time, CultureInfo.InvariantCulture))?.PlaylistId;

        Check(PlaylistEngine.ActiveRule([], new TimeOnly(12, 0)) is null, "пустое расписание — правил нет");

        var day = new List<ScheduleRule> { R("08:00", "утро"), R("14:00", "день"), R("22:00", "ночь") };
        Check(Pick(day, "08:00") == "утро", "ровно на границе действует новое правило");
        Check(Pick(day, "12:30") == "утро", "до следующей границы держится прежнее");
        Check(Pick(day, "15:00") == "день", "середина дня");
        Check(Pick(day, "23:59") == "ночь", "поздний вечер");
        Check(Pick(day, "03:00") == "ночь", "до первого правила суток действует вчерашнее последнее");
        Check(Pick(day, "07:59") == "ночь", "за минуту до утреннего правила ещё ночь");

        // порядок в конфиге не должен ничего значить
        var shuffled = new List<ScheduleRule> { R("22:00", "ночь"), R("08:00", "утро"), R("14:00", "день") };
        Check(Pick(shuffled, "15:00") == "день", "правила сортируются сами");

        var messy = new List<ScheduleRule> { R("не время", "мусор"), R("09:00", "нормальное") };
        Check(Pick(messy, "10:00") == "нормальное", "неразобранное время пропускается");
        Check(PlaylistEngine.ActiveRule([R("25:99", "мусор")], new TimeOnly(12, 0)) is null,
            "если разобрать нечего — правил нет");
    }

    // ---------- ----------

    static void Check(bool ok, string what)
    {
        if (!ok) _failed++;
        Console.WriteLine($"  {(ok ? "ок  " : "ПЛОХО")}  {what}");
    }
}
