using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Wallpaper;

/// <summary>Общее для окон: палитра под тему системы, тёмный заголовок, точная посадка на монитор.</summary>
public static class Ui
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, in int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static bool Dark
    {
        get
        {
            try
            {
                return Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1) is int v && v == 0;
            }
            catch { return false; }
        }
    }

    static SolidColorBrush B(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Кладёт кисти в Resources окна. XAML тянет их через DynamicResource.</summary>
    public static void ApplyPalette(FrameworkElement target, bool? dark = null)
    {
        bool d = dark ?? Dark;
        var r = target.Resources;
        r["Bg"] = B(d ? "#16181F" : "#F2F3F6");
        r["Surface"] = B(d ? "#1E2129" : "#FFFFFF");
        r["Surface2"] = B(d ? "#262A34" : "#E7E9EF");
        r["Ink"] = B(d ? "#E7E9EF" : "#171A21");
        r["Muted"] = B(d ? "#98A0B0" : "#5F6577");
        r["Line"] = B(d ? "#2E3340" : "#D3D8E1");
        r["Accent"] = B(d ? "#8E97FF" : "#4E5CE8");
        r["AccentSoft"] = B(d ? "#262A45" : "#E6E8FD");
        r["Danger"] = B(d ? "#F0705A" : "#BC3B26");
        r["Scrim"] = B(d ? "#E8101219" : "#F0F2F3F6");   // подложка оверлея быстрого выбора
    }

    /// <summary>Заголовок окна тоже должен быть тёмным, иначе светлая полоса поверх тёмного окна.</summary>
    public static void SyncTitleBar(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int on = Dark ? 1 : 0;
        try { DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, in on, sizeof(int)); } catch { }
    }

    /// <summary>
    /// Ставит окно точно на монитор. Через Left/Top не выйдет: WPF считает в DIP,
    /// а GetMonitorRECT отдаёт физические пиксели, и на разных DPI это разные числа.
    /// </summary>
    public static void PlaceOn(Window w, Desktop.Display m, double fill = 1.0)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;

        int cx = (int)(m.W * fill), cy = (int)(m.H * fill);
        int x = m.X + (m.W - cx) / 2, y = m.Y + (m.H - cy) / 2;
        SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, x, y, cx, cy, 0x0040 /* SWP_SHOWWINDOW */);
    }

    /// <summary>
    /// Посадить окно в заданное место экрана. Как и PlaceOn, мимо Left/Top: WPF считает
    /// в DIP, а координаты мониторов приходят в физических пикселях.
    /// </summary>
    public static void PlaceAt(Window w, int x, int y, int cx, int cy)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, x, y, cx, cy, 0x0010 /* SWP_NOACTIVATE */);
    }

    /// <summary>Открыть файл или показать его в проводнике.</summary>
    public static void Open(string path, bool select = false)
    {
        try
        {
            if (select) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { Paths.Write($"открыть {path}: {e.Message}"); }
    }

    /// <summary>
    /// Меню плитки — одно и то же в настройках и в быстром выборе. Что делают «показать»
    /// и «выключить», решает вызывающая сторона: монитор у них разный.
    /// </summary>
    public static ContextMenu TileMenu(string path, bool excluded, Action show, Action toggle)
    {
        var menu = new ContextMenu();

        var showItem = new MenuItem { Header = "Показать сейчас" };
        showItem.Click += (_, _) => show();

        var toggleItem = new MenuItem { Header = excluded ? "Включить в плейлисте" : "Выключить в плейлисте" };
        toggleItem.Click += (_, _) => toggle();

        var openItem = new MenuItem { Header = "Открыть папку" };
        openItem.Click += (_, _) => Open(path, select: true);

        menu.Items.Add(showItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openItem);
        return menu;
    }
}
