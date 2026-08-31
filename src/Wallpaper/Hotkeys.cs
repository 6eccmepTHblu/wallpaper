using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Wallpaper;

/// <summary>
/// Глобальные горячие клавиши на message-only окне. HwndSource даёт HWND и цикл сообщений
/// даром — своё окно через CreateWindowEx понадобится только в v0.3 под видео.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int HWND_MESSAGE = -3;

    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource _src;
    readonly Dictionary<int, string> _actions = [];
    readonly Action<string> _fire;

    /// <summary>Комбинации, которые не удалось занять — заняты другой программой или записаны с ошибкой.</summary>
    public List<string> Failed { get; } = [];

    public Hotkeys(IReadOnlyDictionary<string, string> map, Action<string> fire,
                   Func<string, bool>? isEnabled = null)
    {
        _fire = fire;
        _src = new HwndSource(new HwndSourceParameters("WallpaperHotkeys")
        {
            ParentWindow = new IntPtr(HWND_MESSAGE),
        });
        _src.AddHook(Hook);

        int id = 1;
        foreach (var (action, combo) in map)
        {
            if (string.IsNullOrWhiteSpace(combo)) continue;
            if (isEnabled is not null && !isEnabled(action)) continue;   // выключена галкой в настройках

            if (!TryParse(combo, out uint mods, out uint vk))
            {
                Failed.Add($"{combo} — не разобрана");
                continue;
            }
            if (!RegisterHotKey(_src.Handle, id, mods | MOD_NOREPEAT, vk))
            {
                Failed.Add($"{combo} — занята другой программой");
                continue;
            }
            _actions[id++] = action;
        }

        if (Failed.Count > 0) Paths.Write("горячие клавиши: " + string.Join("; ", Failed));
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !_actions.TryGetValue((int)wParam, out var action)) return IntPtr.Zero;
        handled = true;
        try { _fire(action); }
        catch (Exception e) { Paths.Write($"хоткей {action}: {e}"); }
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+Shift+Right" -> модификаторы + virtual key. Без модификаторов не регистрируем.</summary>
    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default: return false;
            }
        }

        var name = parts[^1];
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) name = "D" + name;   // "1" -> Key.D1

        if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key) || key == Key.None) return false;

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return mods != 0 && vk != 0;
    }

    /// <summary>
    /// Обратное к <see cref="TryParse"/>: то, что нажал пользователь, — в строку для конфига.
    /// Пустая строка означает «пока не комбинация»: одни модификаторы или клавиша без них.
    /// </summary>
    public static string Format(ModifierKeys mods, Key key)
    {
        if (key is Key.None or Key.System
                or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return "";

        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return "";

        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) name = name[1..];  // D1 -> 1
        parts.Add(name);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_src.Handle, id);
        _actions.Clear();
        _src.RemoveHook(Hook);
        _src.Dispose();
    }
}
