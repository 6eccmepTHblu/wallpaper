using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Wic = System.Windows.Media.Imaging;

namespace Wallpaper;

/// <summary>Чем заполнять поля вокруг картинки, когда она не занимает монитор целиком.</summary>
public enum BackgroundMode
{
    /// <summary>Сплошной цвет, заданный руками.</summary>
    Color,

    /// <summary>Сплошной цвет, посчитанный как средний по самой картинке.</summary>
    Average,

    /// <summary>Размытая копия картинки во весь монитор, поверх — она же целиком.</summary>
    Blur,
}

/// <summary>
/// Windows умеет только один сплошной цвет полей на всю систему — ни размытия, ни разных
/// цветов на разных мониторах в API нет. Поэтому «размытие» здесь не настройка, а собранная
/// нами картинка ровно под размер монитора: её и отдаём в обои вместо исходной.
///
/// ponytail: сборка идёт синхронно, в потоке интерфейса. Картинка 4K собирается за доли
/// секунды и потом берётся из кэша, так что подёргивание видно разве что при первом показе.
/// Станет заметно — уносить в фоновый поток и перевыставлять обои по готовности.
/// </summary>
public static class Backdrop
{
    static string Dir => Path.Combine(Paths.Cache, "backdrop");

    /// <summary>
    /// Бикубика у самого края тянет значения из-за границы картинки и рисует там ореол:
    /// сплошной красный после уменьшения перестаёт быть красным. TileFlipXY заставляет
    /// GDI+ отражать края внутрь, и кромка остаётся такой же, как соседние пиксели.
    /// </summary>
    static readonly ImageAttributes EdgeClamp = MakeEdgeClamp();

    static ImageAttributes MakeEdgeClamp()
    {
        var a = new ImageAttributes();
        a.SetWrapMode(WrapMode.TileFlipXY);
        return a;
    }

    // ponytail: чистим кэш, когда файлов стало больше потолка, без учёта давности обращения.
    // Собранная картинка весит 0,5–3 МБ, полсотни таких погоды не делают.
    const int MaxCached = 60;

    /// <summary>
    /// Средний цвет картинки в виде #RRGGBB. Считаем по уменьшенной до 32×32 копии:
    /// усреднение и так усреднение, гонять ради него миллионы пикселей незачем.
    /// </summary>
    public static string? Average(string path)
    {
        try
        {
            using var src = Open(path);
            const int N = 32;

            using var small = new Bitmap(N, N, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, N, N),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, EdgeClamp);
            }

            long r = 0, gr = 0, b = 0;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var c = small.GetPixel(x, y);
                    r += c.R; gr += c.G; b += c.B;
                }

            const int total = N * N;
            return $"#{r / total:X2}{gr / total:X2}{b / total:X2}";
        }
        catch (Exception e)
        {
            Paths.Write($"средний цвет {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Собрать картинку во весь монитор: сильно размытая копия на всё поле, поверх — оригинал
    /// целиком и без искажения пропорций. Возвращает путь к готовому файлу или null, если
    /// собрать не вышло — тогда вызывающий просто ставит исходную картинку.
    /// </summary>
    public static string? Compose(string path, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        try
        {
            var ready = CachePath(path, w, h);
            if (File.Exists(ready)) return ready;

            using var src = Open(path);
            using (var canvas = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    // Подложка: крошечная копия, растянутая на весь монитор. Билинейная
                    // интерполяция при таком увеличении и даёт то самое сильное размытие.
                    using (var tiny = Shrink(src, w, h))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(tiny, new Rectangle(0, 0, w, h));
                    }

                    double k = Math.Min((double)w / src.Width, (double)h / src.Height);
                    int cw = (int)Math.Round(src.Width * k), ch = (int)Math.Round(src.Height * k);

                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, (w - cw) / 2, (h - ch) / 2, cw, ch);
                }

                Directory.CreateDirectory(Dir);
                Prune();
                // q95, а не умолчание GDI+ (≈92): +1,5–2,5 дБ на самой картинке за те же ~10 мс.
                // Быстрее не бывает, а без потерь — только PNG (в 20 раз дольше) или BMP (до 24 МБ).
                using var q = new EncoderParameters(1);
                q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                canvas.Save(ready, JpegCodec, q);
            }
            return ready;
        }
        catch (Exception e)
        {
            Paths.Write($"подложка для {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Открыть картинку. GDI+ не знает WebP и AVIF — их читает WIC, теми же кодеками,
    /// что и проводник. Через WIC идут только они: обычные форматы GDI+ читает быстрее.
    /// </summary>
    static Bitmap Open(string path)
    {
        try { return new Bitmap(path); }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException) { return OpenWic(path); }
    }

    static Bitmap OpenWic(string path)
    {
        using var s = File.OpenRead(path);
        var frame = Wic.BitmapDecoder.Create(s, Wic.BitmapCreateOptions.IgnoreColorProfile, Wic.BitmapCacheOption.OnLoad).Frames[0];
        var src = new Wic.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr24, null, 0);

        var bmp = new Bitmap(src.PixelWidth, src.PixelHeight, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try { src.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0, data.Stride * data.Height, data.Stride); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>
    /// Ужать картинку до нескольких десятков пикселей, обрезав по пропорциям монитора.
    /// Настоящий гауссов фильтр на 4K считался бы секундами, а на таком радиусе размытия
    /// разницы с растянутой крошкой не видно.
    /// </summary>
    static Bitmap Shrink(Bitmap src, int w, int h)
    {
        const int Wide = 24;
        int tw = Wide, th = Math.Max(1, (int)Math.Round(Wide * (double)h / w));

        var box = Cover(src.Width, src.Height, w, h);

        var tiny = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(tiny);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, new Rectangle(0, 0, tw, th),
            box.X, box.Y, box.Width, box.Height, GraphicsUnit.Pixel, EdgeClamp);
        return tiny;
    }

    /// <summary>Кусок исходной картинки с пропорциями монитора, по центру — чтобы подложка не косила.</summary>
    static Rectangle Cover(int sw, int sh, int tw, int th)
    {
        double target = (double)tw / th;
        if ((double)sw / sh > target)
        {
            int cw = Math.Max(1, (int)Math.Round(sh * target));
            return new Rectangle((sw - cw) / 2, 0, cw, sh);
        }

        int ch = Math.Max(1, (int)Math.Round(sw / target));
        return new Rectangle(0, (sh - ch) / 2, sw, ch);
    }

    /// <summary>
    /// Имя в кэше завязано на файл, его время правки и размер монитора. И на версию сборки:
    /// поменялось, как собираем, — старые подложки не должны браться из кэша.
    /// </summary>
    static string CachePath(string path, int w, int h)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { stamp = 0; }

        var key = $"{path.ToLowerInvariant()}|{stamp}|{w}x{h}|q95";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Dir, hash + ".jpg");
    }

    static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.jpg");
            if (files.Length < MaxCached) return;

            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length / 2))
                try { f.Delete(); } catch { }
        }
        catch { }
    }
}
