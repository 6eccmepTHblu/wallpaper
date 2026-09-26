using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Wallpaper;

/// <summary>Как вписывать картинку. Глобально: IDesktopWallpaper.SetPosition не принимает monitorId.</summary>
public enum FitMode { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

public enum Mode { Off, Static, Playlist, Video }

public enum OrderMode { Sequential, Shuffle }

/// <summary>Как разложены плитки в быстром выборе. К самому плейлисту отношения не имеет.</summary>
public enum SortMode { Playlist, Name, NewestFirst, OldestFirst }

public sealed class FolderRef
{
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public sealed class Playlist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<FolderRef> Folders { get; set; } = [];
    public List<string> Files { get; set; } = [];
    public int IntervalSeconds { get; set; } = 900;
    public OrderMode Order { get; set; } = OrderMode.Shuffle;

    /// <summary>
    /// Выключенные вручную файлы. Из папки они никуда не деваются и в сетке настроек
    /// видны — просто не участвуют в ротации. Хранится путь целиком: файл может лежать
    /// в любой из папок плейлиста, а имена в разных папках повторяются.
    /// </summary>
    public List<string> Excluded { get; set; } = [];
}

public sealed class VideoCfg
{
    public string? Path { get; set; }
    /// <summary>0 — без звука. Обои со звуком — редкость, поэтому это и есть значение по умолчанию.</summary>
    public int Volume { get; set; }
}

/// <summary>Когда глушить видео. Всё — «пауза»: mpv на паузе отпускает и декодер, и GPU.</summary>
public sealed class PauseRules
{
    public bool FullscreenApp { get; set; } = true;
    public bool ForegroundCoversMonitor { get; set; } = true;
    public bool OnBattery { get; set; } = true;
    public bool LockScreen { get; set; } = true;
    public bool RemoteSession { get; set; } = true;
}

/// <summary>«С 22:00 — тёмный плейлист». Правило действует, пока не наступит следующее.</summary>
public sealed class ScheduleRule
{
    /// <summary>Время в формате ЧЧ:ММ.</summary>
    public string From { get; set; } = "";
    public string? PlaylistId { get; set; }
}

public sealed class MonitorCfg
{
    /// <summary>Своё имя монитора. Пусто — подставится «Монитор N — WxH».</summary>
    public string? Label { get; set; }
    public Mode Mode { get; set; } = Mode.Off;
    public string? PlaylistId { get; set; }
    /// <summary>Путь к картинке для Mode.Static.</summary>
    public string? Path { get; set; }
    /// <summary>Для Mode.Video.</summary>
    public VideoCfg? Video { get; set; }
}

public sealed class Config
{
    public int Version { get; set; } = 1;
    public bool StartWithWindows { get; set; }

    /// <summary>Выключено — приложение ничего не показывает и возвращает обои Windows.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Общее для всех мониторов — ограничение IDesktopWallpaper, см. SPEC §4.1.</summary>
    public FitMode Fit { get; set; } = FitMode.Fill;

    /// <summary>Чем заполнять поля вокруг картинки: цветом, средним цветом или размытием.</summary>
    public BackgroundMode Background { get; set; }

    /// <summary>Цвет полей для режима <see cref="BackgroundMode.Color"/>.</summary>
    public string? BackgroundColor { get; set; }

    public PauseRules PauseRules { get; set; } = new();

    /// <summary>Переключение плейлиста по времени суток. Пусто — расписания нет.</summary>
    public List<ScheduleRule> Schedule { get; set; } = [];

    /// <summary>Где лежит libmpv-2.dll: папка или сам файл. Пусто — искать рядом с exe.</summary>
    public string? MpvPath { get; set; }

    public Dictionary<string, MonitorCfg> Monitors { get; set; } = [];
    public List<Playlist> Playlists { get; set; } = [];
    /// <summary>Размер превью по наведению в быстром выборе, % от размера экрана.</summary>
    public int PickerZoomPercent { get; set; } = 60;

    /// <summary>Прозрачность того же превью, %. 0 — непрозрачное.</summary>
    public int PickerZoomTransparency { get; set; }

    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

    /// <summary>Действия с выключенными горячими клавишами. Комбинация при этом сохраняется.</summary>
    public List<string> HotkeysOff { get; set; } = [];

    public bool HotkeyEnabled(string action) =>
        !HotkeysOff.Contains(action, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Дописать комбинации, которых нет в файле. JSON подменяет словарь целиком, а не
    /// дополняет: без этого действие, добавленное в новой версии, молча остаётся без
    /// клавиши, а урезанный руками hotkeys обесклавишивает вообще всё, кроме своих строк.
    /// Выключают клавиши не удалением, а списком hotkeysOff — его это не трогает.
    /// </summary>
    public void FillHotkeyDefaults()
    {
        Hotkeys ??= [];
        foreach (var (action, combo) in DefaultHotkeys()) Hotkeys.TryAdd(action, combo);
    }

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["next"] = "Ctrl+Alt+Right",
        ["prev"] = "Ctrl+Alt+Left",
        ["nextAll"] = "Ctrl+Alt+Shift+Right",
        ["togglePause"] = "Ctrl+Alt+P",
        ["quit"] = "Ctrl+Alt+Q",
        ["deleteCurrent"] = "Ctrl+Alt+D",
        ["hideCurrent"] = "Ctrl+Alt+C",
        ["quickPicker"] = "Ctrl+Alt+W",
        ["toggleSound"] = "Ctrl+Alt+M",
        ["toggleWallpapers"] = "Ctrl+Alt+0",
        ["revealCurrent"] = "Ctrl+Alt+E",
        ["playlist1"] = "Ctrl+Alt+1",
        ["playlist2"] = "Ctrl+Alt+2",
        ["playlist3"] = "Ctrl+Alt+3",
    };

    public Playlist? Playlist(string? id) =>
        id is null ? null : Playlists.FirstOrDefault(p => p.Id == id);

    // ---------- загрузка / сохранение ----------

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Последняя запись нашими руками — чтобы watcher не реагировал на самого себя.</summary>
    public static DateTime LastSelfWrite { get; private set; }

    /// <summary>
    /// Читает конфиг. Ничего не пишет: битый файл — не повод затирать чужие правки,
    /// а при горячей перезагрузке мы вполне можем поймать наполовину сохранённый файл.
    /// </summary>
    public static bool TryLoad(out Config cfg, out string? error)
    {
        cfg = new Config();
        error = null;
        if (!File.Exists(Paths.Config)) return true;
        try
        {
            cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.Config), Json) ?? new Config();
            cfg.FillHotkeyDefaults();
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Старт: нет файла — создать; битый — отложить копию в .bad и работать на дефолтах.</summary>
    public static Config LoadOrDefault(out string? backupPath)
    {
        backupPath = null;
        if (!File.Exists(Paths.Config)) { var fresh = new Config(); fresh.Save(); return fresh; }
        if (TryLoad(out var cfg, out var error)) return cfg;

        backupPath = Paths.Config + ".bad";
        try { File.Copy(Paths.Config, backupPath, overwrite: true); } catch { backupPath = null; }
        Paths.Write($"config.json битый ({error}), работаем на дефолтах");
        return new Config();   // намеренно не сохраняем: пусть пользователь чинит свой файл
    }

    public void Save() => WriteAtomic(Paths.Config, JsonSerializer.Serialize(this, Json));

    internal static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
        LastSelfWrite = DateTime.UtcNow;
    }

    /// <summary>Следит за правками config.json руками. В v0.1 это основной способ настройки.</summary>
    public static FileSystemWatcher Watch(Action onChanged)
    {
        var w = new FileSystemWatcher(Paths.Dir, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        void Fire(object? _, FileSystemEventArgs __)
        {
            // ponytail: отсечка по времени вместо честного сравнения содержимого.
            if (DateTime.UtcNow - LastSelfWrite < TimeSpan.FromSeconds(2)) return;
            onChanged();
        }
        w.Changed += Fire;
        w.Created += Fire;
        w.Renamed += (_, _) => Fire(null, null!);
        return w;
    }

    // ---------- реестр ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Wallpaper";

    public static bool AutostartEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is not null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }

    /// <summary>Windows пережимает обои в JPEG. 100 — без потерь. SPEC §4.1.</summary>
    public static void EnsureJpegQuality()
    {
        const string key = @"HKEY_CURRENT_USER\Control Panel\Desktop";
        if (Registry.GetValue(key, "JPEGImportQuality", null) is int q && q == 100) return;
        try { Registry.SetValue(key, "JPEGImportQuality", 100, RegistryValueKind.DWord); } catch { }
    }
}

// ---------- состояние проигрывания ----------

public sealed class PlayPos
{
    public string? PlaylistId { get; set; }
    public string? Path { get; set; }
    public DateTime DueUtc { get; set; }
}

/// <summary>Отдельно от конфига, чтобы правки config.json руками не затирались.</summary>
public sealed class State
{
    /// <summary>
    /// Последние показанные на мониторе файлы, новые в конце. Нужны «Предыдущей» в режиме
    /// «Вперемешку»: следующий файл там случайный, и шаг назад по списку ничего не значит.
    /// </summary>
    public Dictionary<string, List<string>> History { get; set; } = [];

    /// <summary>
    /// Мониторы, на которых слайд-шоу остановлено. Пауза именно помониторная:
    /// на одном экране картинка держится, на другом продолжает меняться.
    /// </summary>
    public List<string> PausedMonitors { get; set; } = [];

    public Dictionary<string, PlayPos> Cursors { get; set; } = [];

    /// <summary>Что стояло на мониторах до нас. Возвращаем это при выходе.</summary>
    public Dictionary<string, string> OriginalWallpapers { get; set; } = [];

    /// <summary>
    /// Что мы сами последний раз поставили на каждый монитор. Нужно ровно для одного:
    /// на старте отличить «пользователь поставил свои обои» от «это осталось от нас».
    /// </summary>
    public Dictionary<string, string> LastApplied { get; set; } = [];

    /// <summary>И как оно было вписано.</summary>
    public FitMode? OriginalFit { get; set; }

    /// <summary>Где и какого размера было окно настроек — чтобы открылось там же, где закрыли.</summary>
    public WindowPlace? SettingsPlace { get; set; }

    /// <summary>Выбранный порядок плиток в быстром выборе — держится между запусками.</summary>
    public SortMode PickerSort { get; set; }

    /// <summary>Показывать ли в быстром выборе крупное превью при наведении.</summary>
    public bool PickerZoom { get; set; }

    public sealed class WindowPlace
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public bool Maximized { get; set; }
    }

    public static State Load()
    {
        try
        {
            return File.Exists(Paths.State)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(Paths.State), Config.Json) ?? new State()
                : new State();
        }
        catch { return new State(); }
    }

    public void Save()
    {
        try { Config.WriteAtomic(Paths.State, JsonSerializer.Serialize(this, Config.Json)); } catch { }
    }
}

public static class Paths
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wallpaper");

    /// <summary>
    /// Куда складывать производное — то, что всегда можно собрать заново. Локальный профиль,
    /// а не роуминговый: синхронизировать между машинами десятки мегабайт подложек незачем.
    /// </summary>
    public static string Cache { get; } = Path.Combine(Path.GetTempPath(), "Wallpaper");

    public static string Config => Path.Combine(Dir, "config.json");
    public static string State => Path.Combine(Dir, "state.json");
    public static string Log => Path.Combine(Dir, "wallpaper.log");

    public static void Ensure() => Directory.CreateDirectory(Dir);

    /// <summary>ponytail: 20 строк вместо ILogger. Хватит, пока лог читает один человек.</summary>
    public static void Write(string line)
    {
        try
        {
            var f = new FileInfo(Log);
            if (f.Exists && f.Length > 512 * 1024) f.Delete();
            // BOM: иначе Get-Content в PowerShell 5.1 читает лог как ANSI и кириллица превращается в кашу.
            File.AppendAllText(Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch { }
    }
}
