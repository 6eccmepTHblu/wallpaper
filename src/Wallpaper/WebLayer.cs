using Microsoft.Web.WebView2.Core;

namespace Wallpaper;

/// <summary>
/// Веб-страница как обои. Отдельного слоя не нужно: окна в слое рабочего стола уже
/// создаёт <see cref="VideoLayer"/>, здесь меняется только содержимое окна.
/// </summary>
public static class WebLayer
{
    static readonly string[] WebExtensions = [".html", ".htm"];

    /// <summary>Веб это или файл для mpv — решаем по самому пути, отдельного режима не заводим.</summary>
    public static bool IsWeb(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || WebExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Существует ли то, на что указывает путь. Для адресов проверять нечего.</summary>
    public static bool Exists(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || File.Exists(path);

    static CoreWebView2Environment? _env;

    /// <summary>
    /// Заводит страницу в готовом окне слоя. Асинхронно: WebView2 иначе не умеет.
    /// Вызывать только из UI-потока — контроллер этого требует.
    /// </summary>
    public static async Task<CoreWebView2Controller?> CreateAsync(IntPtr hwnd, string path, int width, int height)
    {
        try
        {
            _env ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Paths.Dir, "webview"));

            var controller = await _env.CreateCoreWebView2ControllerAsync(hwnd);
            controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
            controller.IsVisible = true;

            var web = controller.CoreWebView2;
            // Обои не должны вести себя как браузер: ни меню, ни горячих клавиш, ни диалогов.
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            web.Settings.IsZoomControlEnabled = false;

            web.Navigate(path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : new Uri(Path.GetFullPath(path)).AbsoluteUri);

            return controller;
        }
        catch (Exception e)
        {
            Paths.Write($"веб-обои ({Path.GetFileName(path)}): {e.Message}");
            return null;
        }
    }

    public static void Resize(CoreWebView2Controller c, int width, int height)
    {
        try { c.Bounds = new System.Drawing.Rectangle(0, 0, width, height); } catch { }
    }

    /// <summary>Пауза: снимаем видимость и усыпляем процесс — иначе анимация крутит GPU впустую.</summary>
    public static void SetPaused(CoreWebView2Controller c, bool paused)
    {
        try
        {
            c.IsVisible = !paused;
            if (paused) _ = c.CoreWebView2.TrySuspendAsync();
            else c.CoreWebView2.Resume();
        }
        catch { }
    }

    public static void Destroy(CoreWebView2Controller? c)
    {
        try { c?.Close(); } catch { }
    }

    /// <summary>Есть ли в системе рантайм WebView2. На Windows 11 он предустановлен.</summary>
    public static string? RuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
    }
}
