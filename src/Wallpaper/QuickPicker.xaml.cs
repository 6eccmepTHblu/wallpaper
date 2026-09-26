using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wallpaper;

/// <summary>Оверлей по Ctrl+Alt+W: сетка обоев текущего плейлиста поверх монитора под курсором.</summary>
public partial class QuickPicker : Window
{
    const double TileW = 208, TileH = 117, Gap = 8;

    /// <summary>ponytail: тот же потолок, что и в настройках. Дальше — сузить набор фильтром.</summary>
    const int Cap = 600;

    readonly Desktop.Display _mon;
    readonly List<Border> _tiles = [];
    string[] _paths = [];
    string _filter = "";
    string? _plId;
    int _index;
    bool _closing;
    bool _menuOpen;
    CancellationTokenSource? _load;

    /// <summary>Отсортированный список и ключ, для которого он посчитан: плейлист + порядок.</summary>
    string[] _sorted = [];
    string _sortedKey = "";

    CancellationTokenSource? _previewLoad;
    string? _hoverPath;

    public QuickPicker(Desktop.Display monitor)
    {
        InitializeComponent();
        _mon = monitor;
        Ui.ApplyPalette(this);

        _plId = Program.Cfg.Monitors.GetValueOrDefault(monitor.Id)?.PlaylistId
             ?? Program.Cfg.Playlists.FirstOrDefault()?.Id;

        SourceInitialized += (_, _) => Ui.PlaceOn(this, _mon, 0.82);
        Loaded += (_, _) => { Ui.PlaceOn(this, _mon, 0.82); Build(); Focus(); };
        // Раскрытое меню плитки — тоже потеря фокуса. Закрываться на ней нельзя,
        // иначе меню исчезает вместе с окном в тот же миг, как появилось.
        Deactivated += (_, _) => { if (!_menuOpen) Dismiss(); };
        Closed += (_, _) => _load?.Cancel();

        RefreshChip.Click += (_, _) => Program.Engine.Refresh();
        SortChip.Click += (_, _) => NextSort();

        ZoomBox.IsChecked = Program.State.PickerZoom;
        ZoomBox.Checked += (_, _) => SaveZoom(true);
        ZoomBox.Unchecked += (_, _) => { SaveZoom(false); HidePreview(); };
        Program.Engine.Rescanned += OnRescanned;
        Closed += (_, _) => Program.Engine.Rescanned -= OnRescanned;

        PreviewKeyDown += OnKey;
        TextInput += OnText;
    }

    // ================= построение =================

    void Build()
    {
        HidePreview();
        _load?.Cancel();
        Tiles.Children.Clear();
        _tiles.Clear();

        var pl = Program.Cfg.Playlist(_plId);
        PlTitle.Text = pl?.Name ?? "Плейлистов нет";

        SortChip.Content = SortName(Program.State.PickerSort);

        var all = pl is null ? [] : Sorted(pl);
        _paths = (_filter.Length == 0
            ? all
            : all.Where(p => Path.GetFileName(p).Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToArray())
            .Take(Cap).ToArray();

        Counter.Text = all.Length > _paths.Length
            ? $"{_paths.Length} из {all.Length}"
            : $"{_paths.Length}";

        FilterChip.Visibility = _filter.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        FilterText.Text = _filter;

        var current = Program.Engine.CurrentPath(_mon.Id);
        var cts = _load = new CancellationTokenSource();

        for (int i = 0; i < _paths.Length; i++)
        {
            var path = _paths[i];
            var img = new Image { Stretch = Stretch.UniformToFill, Source = Thumbs.Cached(path) };
            var tile = new Border
            {
                Width = TileW,
                Height = TileH,
                Margin = new Thickness(Gap / 2),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = (Brush)Resources["Surface2"],
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Child = img,
                ToolTip = Path.GetFileName(path),
            };
            var p = path;
            tile.ContextMenu = TileMenu(p);
            tile.MouseLeftButtonUp += (_, _) => Apply(p);
            tile.MouseEnter += (_, _) => HoverStart(p);
            tile.MouseLeave += (_, _) => HidePreview();
            _tiles.Add(tile);
            Tiles.Children.Add(tile);

            if (img.Source is null) _ = FillAsync(img, p, cts.Token);
        }

        _index = Math.Max(0, Array.IndexOf(_paths, current));
        Highlight();
    }

    // ================= превью по наведению =================

    void SaveZoom(bool on)
    {
        Program.State.PickerZoom = on;
        Program.State.Save();
    }

    /// <summary>
    /// Показать превью сразу, без выдержки. Миниатюра из сетки уже в памяти — её и рисуем
    /// в первый же кадр, а чтение файла в полном разрешении подменяет картинку, когда
    /// доедет. Так наведение отзывается мгновенно и всё равно заканчивается резким кадром.
    /// </summary>
    void HoverStart(string path)
    {
        if (ZoomBox.IsChecked != true) return;
        _hoverPath = path;

        // Монитор приходит в физических пикселях, WPF раскладывает в независимых от устройства.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;
        double part = Math.Clamp(Program.Cfg.PickerZoomPercent, 10, 100) / 100.0;

        PreviewImage.MaxWidth = _mon.W * part / scale;
        PreviewImage.MaxHeight = _mon.H * part / scale;
        PreviewHost.Opacity = 1 - Math.Clamp(Program.Cfg.PickerZoomTransparency, 0, 90) / 100.0;
        PreviewName.Text = Path.GetFileName(path);

        var quick = Thumbs.Cached(path);
        if (quick is not null)
        {
            PreviewImage.Source = quick;
            PreviewHost.Visibility = Visibility.Visible;
        }

        _ = ShowPreviewAsync(path, (int)(_mon.W * part));
    }

    void HidePreview()
    {
        _previewLoad?.Cancel();
        _hoverPath = null;
        PreviewHost.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewName.Text = "";
    }

    async Task ShowPreviewAsync(string path, int px)
    {
        _previewLoad?.Cancel();
        var cts = _previewLoad = new CancellationTokenSource();
        var img = await Thumbs.PreviewAsync(path, px, cts.Token);

        // Пока читали с диска, мышь могла уйти на другую плитку или вообще из окна.
        if (img is null || cts.IsCancellationRequested || _hoverPath != path) return;

        PreviewImage.Source = img;
        PreviewHost.Visibility = Visibility.Visible;
    }

    // ================= порядок плиток =================

    /// <summary>
    /// Файлы в выбранном порядке. Считаем один раз на плейлист и режим: Build вызывается
    /// на каждую букву фильтра, а дата создания — это обращение к диску на каждый файл.
    /// </summary>
    string[] Sorted(Playlist pl)
    {
        var mode = Program.State.PickerSort;
        var key = $"{pl.Id}|{mode}";
        if (key == _sortedKey) return _sorted;

        _sortedKey = key;
        return _sorted = PlaylistEngine.SortFiles(Program.Engine.Order(pl.Id), mode);
    }

    static string SortName(SortMode m) => m switch
    {
        SortMode.Name => "↕ По имени",
        SortMode.NewestFirst => "↕ Сначала новые",
        SortMode.OldestFirst => "↕ Сначала старые",
        _ => "↕ Как в плейлисте",
    };

    void NextSort()
    {
        var next = Program.State.PickerSort switch
        {
            SortMode.Playlist => SortMode.NewestFirst,
            SortMode.NewestFirst => SortMode.OldestFirst,
            SortMode.OldestFirst => SortMode.Name,
            _ => SortMode.Playlist,
        };
        Program.State.PickerSort = next;
        Program.State.Save();
        Build();
    }

    /// <summary>Список файлов пересобран — посчитанный порядок больше не годится.</summary>
    void OnRescanned()
    {
        _sortedKey = "";
        Build();
    }

    async Task FillAsync(Image target, string path, CancellationToken ct)
    {
        var bmp = await Thumbs.GetAsync(path, ct);
        if (bmp is not null && !ct.IsCancellationRequested) target.Source = bmp;
    }

    void Highlight()
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            bool sel = i == _index;
            _tiles[i].BorderBrush = sel ? (Brush)Resources["Accent"] : Brushes.Transparent;
            _tiles[i].Opacity = sel ? 1.0 : 0.72;
        }
        if (_index >= 0 && _index < _tiles.Count) _tiles[_index].BringIntoView();
    }

    int Columns()
    {
        double w = Tiles.ActualWidth > 0 ? Tiles.ActualWidth : ActualWidth - 44;
        return Math.Max(1, (int)(w / (TileW + Gap)));
    }

    // ================= клавиатура =================

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_filter.Length > 0) { _filter = ""; Build(); }
                else Dismiss();
                break;

            case Key.Enter:
                if (_index >= 0 && _index < _paths.Length) Apply(_paths[_index]);
                break;

            case Key.Left: Move(-1); break;
            case Key.Right: Move(+1); break;
            case Key.Up: Move(-Columns()); break;
            case Key.Down: Move(+Columns()); break;
            case Key.Home: _index = 0; Highlight(); break;
            case Key.End: _index = Math.Max(0, _paths.Length - 1); Highlight(); break;

            case Key.Back:
                if (_filter.Length > 0) { _filter = _filter[..^1]; Build(); }
                break;

            case Key.Tab: NextPlaylist(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1); break;

            case Key.F5: Program.Engine.Refresh(); break;

            case Key.F6: NextSort(); break;

            default: return;   // остальное пусть дойдёт до TextInput
        }
        e.Handled = true;
    }

    void OnText(object sender, TextCompositionEventArgs e)
    {
        var t = e.Text;
        if (t.Length == 0 || char.IsControl(t[0])) return;
        _filter += t;
        Build();
        e.Handled = true;
    }

    void Move(int delta)
    {
        if (_paths.Length == 0) return;
        _index = Math.Clamp(_index + delta, 0, _paths.Length - 1);
        Highlight();
    }

    void NextPlaylist(int delta)
    {
        var list = Program.Cfg.Playlists;
        if (list.Count < 2) return;
        int i = list.FindIndex(p => p.Id == _plId);
        _plId = list[PlaylistEngine.Mod(i + delta, list.Count)].Id;
        _filter = "";
        _sortedKey = "";
        Build();
    }

    /// <summary>
    /// То же меню, что и в настройках. Выключенных файлов здесь не показываем — сетка
    /// строится по тому же списку, что крутит слайд-шоу, — поэтому пункт всегда «выключить».
    /// </summary>
    ContextMenu TileMenu(string path)
    {
        var menu = Ui.TileMenu(path, excluded: false, () => Apply(path), () => Exclude(path));

        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _menuOpen = false;
            // Меню закрыли кликом мимо окна — фокус к нам не вернулся, значит оверлей пора убрать.
            if (!IsActive) Dismiss();
        });
        return menu;
    }

    /// <summary>Выключить файл из плейлиста, из которого сейчас показана сетка.</summary>
    void Exclude(string path)
    {
        var pl = Program.Cfg.Playlist(_plId);
        if (pl is null) return;

        if (!pl.Excluded.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
            pl.Excluded.Add(path);

        Program.ApplyConfig();
        _sortedKey = "";   // список изменился, посчитанный порядок больше не годится
        Build();
    }

    /// <summary>
    /// Закрыть ровно один раз. Клик по плитке отнимает фокус, из-за чего Deactivated
    /// закрывает окно раньше нас — а второй Close по закрытому окну бросает исключение
    /// прямо в оконном колбэке и убивает процесс без следа в журнале.
    /// </summary>
    void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch (Exception e) { Paths.Write($"закрытие быстрого выбора: {e.Message}"); }
    }

    void Apply(string path)
    {
        if (_closing) return;

        // Плейлист переключили внутри оверлея — закрепляем выбор за монитором, иначе
        // следующий тик вернёт картинку из прежнего плейлиста.
        var mc = Program.Cfg.Monitors.GetValueOrDefault(_mon.Id);
        if (_plId is not null && mc is not null && mc.Mode == Mode.Playlist && mc.PlaylistId != _plId)
        {
            mc.PlaylistId = _plId;
            Program.ApplyConfig();
        }

        // Закрываемся раньше показа: пока окно на экране, оно перехватывает фокус,
        // а показ картинки может дёрнуть слой рабочего стола.
        Dismiss();
        Program.Engine.ShowNow(_mon.Id, path);
    }
}
