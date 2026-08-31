using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Wallpaper;

[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    void GetImage(SIZE size, uint flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE { public int cx, cy; }

/// <summary>
/// Миниатюры через shell thumbnail provider: один API отдаёт превью и для картинок,
/// и для видео (пригодится в v0.3). Своего кэша на диске нет — Windows уже держит
/// thumbcache_*.db, дублировать его незачем.
/// </summary>
public static class Thumbs
{
    /// <summary>Просим 200 px: тайл в сетке ~150 px, запас на DPI.</summary>
    const int Px = 200;

    // ponytail: при переполнении чистим кэш целиком вместо LRU. 600 миниатюр ≈ 50 МБ,
    // до этого потолка сессия обычно и не доходит.
    const int MaxCached = 600;

    static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Свой STA-поток, а не пул: IShellItemImageFactory — тот же COM оболочки, что и обои,
    // и из потока пула он умеет намертво сцепиться с Explorer, чьи окна мы держим.
    // Отдельный от Desktop, чтобы шесть сотен миниатюр не стояли в очереди перед сменой обоев.
    static readonly ComThread Com = new("WallpaperThumbs");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(IntPtr hObject);

    static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    public static BitmapSource? Cached(string path) => Cache.GetValueOrDefault(path);

    /// <summary>Готовая к показу и замороженная миниатюра, либо null если её не отдали.</summary>
    public static async Task<BitmapSource?> GetAsync(string path, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(path, out var hit)) return hit;
        if (ct.IsCancellationRequested) return null;

        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Com.Post(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetResult(null); return; }
            tcs.TrySetResult(Load(path));
        });

        var img = await tcs.Task.ConfigureAwait(true);
        if (img is null || ct.IsCancellationRequested) return null;

        if (Cache.Count > MaxCached) Cache.Clear();
        Cache[path] = img;
        return img;
    }

    static BitmapSource? Load(string path)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var factory);
            factory.GetImage(new SIZE { cx = Px, cy = Px }, 0, out hbm);
            if (hbm == IntPtr.Zero) return null;

            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbm, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();   // иначе не отдать в UI-поток
            return src;
        }
        catch (Exception e)
        {
            Paths.Write($"миниатюра {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }
}
