using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Wallpaper;

/// <summary>
/// Решает, когда глушить видео. Опрашивает систему раз в 2 секунды и только пока
/// видеослой вообще жив — в режиме картинок таймер стоит.
/// </summary>
public sealed class PauseWatcher : IDisposable
{
    /// <summary>SHQueryUserNotificationState — один вызов закрывает и полноэкранку, и локскрин.</summary>
    enum Quns
    {
        NotPresent = 1,          // локскрин, скринсейвер, чужая сессия
        Busy = 2,                // полноэкранное приложение
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,                 // полноэкранное приложение из Store
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHQueryUserNotificationState(out Quns state);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus
    {
        public byte AcLineStatus;      // 0 — от батареи, 1 — от сети, 255 — неизвестно
        public byte BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    const int SM_REMOTESESSION = 0x1000;

    readonly DispatcherTimer _timer;
    readonly Action _recheck;
    Config _cfg;

    /// <summary>Почему сейчас пауза. Пусто — играем. Показывается в настройках.</summary>
    public string Reason { get; private set; } = "";

    public PauseWatcher(Config cfg, Action recheck)
    {
        _cfg = cfg;
        _recheck = recheck;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => _recheck(), Dispatcher.CurrentDispatcher);
        _timer.Stop();
    }

    public void Reload(Config cfg) => _cfg = cfg;

    public void Start() => _timer.Start();
    public void Stop() { _timer.Stop(); Reason = ""; }

    /// <summary>Причины, общие для всех мониторов. Считается один раз за опрос.</summary>
    public bool GlobalPause()
    {
        var r = _cfg.PauseRules;

        if (r.RemoteSession && GetSystemMetrics(SM_REMOTESESSION) != 0) return Set("удалённый рабочий стол");

        if (r.OnBattery && GetSystemPowerStatus(out var p) && p.AcLineStatus == 0) return Set("работа от батареи");

        Quns state;
        try { SHQueryUserNotificationState(out state); }
        catch { return Set(""); }

        if (r.LockScreen && state == Quns.NotPresent) return Set("экран заблокирован");

        // Только настоящая полноэкранка. QUNS_BUSY выставляется и на обычных развёрнутых
        // окнах — по нему видео вставало бы на паузу почти всегда.
        // Игры в безрамочном окне ловятся отдельно, через ForegroundCovers.
        if (r.FullscreenApp && state is Quns.RunningD3dFullScreen or Quns.PresentationMode)
            return Set($"полноэкранное приложение ({state})");

        return Set("");
    }

    bool Set(string reason)
    {
        Reason = reason;
        return reason.Length > 0;
    }

    /// <summary>
    /// Игра в безрамочном окне не считается полноэкранной для Windows, но монитор закрывает
    /// целиком — декодировать под ней всё равно некуда.
    /// </summary>
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>
    /// Сам рабочий стол, панель задач и наши же окна с видео растянуты на все мониторы.
    /// Без этой отсечки достаточно нажать Win+D — и видео уходит на паузу навсегда.
    /// </summary>
    static bool IsDesktopOrShell(IntPtr hwnd)
    {
        if (hwnd == GetShellWindow()) return true;

        // Наши собственные окна — настройки, быстрый выбор — тоже закрывают монитор,
        // но глушить из-за них видео незачем.
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return true;

        return Desktop.ClassOf(hwnd) is
            "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32"
            or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "WallpaperVideoHost" or "mpv";
    }

    public bool ForegroundCovers(Desktop.Display m)
    {
        if (!_cfg.PauseRules.ForegroundCoversMonitor) return false;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || IsDesktopOrShell(fg)) return false;
        if (!Desktop.GetWindowRect(fg, out var r)) return false;

        return r.Left <= m.X && r.Top <= m.Y
            && r.Right >= m.X + m.W && r.Bottom >= m.Y + m.H;
    }

    public void Dispose() => _timer.Stop();
}
