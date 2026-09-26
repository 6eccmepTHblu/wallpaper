using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wallpaper;

/// <summary>
/// Полупрозрачная плашка «пауза» в левом верхнем углу монитора. Показывается, только пока
/// слайд-шоу на этом мониторе стоит; кликов не ловит и фокус не забирает — это индикатор,
/// а не кнопка.
/// </summary>
public sealed class PauseBadge : Window
{
    /// <summary>Размер в физических пикселях: плашка должна быть заметной, но не мешать.</summary>
    const int Size = 34;
    const int Gap = 16;   // не Margin: так называется свойство самого окна

    const int GWL_EXSTYLE = -20;
    const uint WS_EX_TRANSPARENT = 0x00000020;   // мышь проходит насквозь
    const uint WS_EX_NOACTIVATE = 0x08000000;
    const uint WS_EX_TOOLWINDOW = 0x00000080;    // не показывать в Alt+Tab

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    public string MonitorId { get; }

    public PauseBadge(Desktop.Display mon)
    {
        MonitorId = mon.Id;

        Title = "Пауза слайд-шоу";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Opacity = 0.55;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Ui.ApplyPalette(this);

        Content = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = (Brush)Resources["Surface"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Resources["Line"],
            Child = new TextBlock
            {
                Text = "⏸",
                FontSize = 17,
                Foreground = (Brush)Resources["Ink"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = (uint)GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(h, GWL_EXSTYLE,
                new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            Place(mon);
        };
    }

    /// <summary>Посадить плашку в угол монитора. Мониторы могли переехать — зовём и потом.</summary>
    public void Place(Desktop.Display mon) =>
        Ui.PlaceAt(this, mon.X + Gap, mon.Y + Gap, Size, Size);
}
