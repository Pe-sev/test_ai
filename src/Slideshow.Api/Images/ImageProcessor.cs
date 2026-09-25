using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Slideshow.Api.Images;

public sealed class ImageRejectedException(string message) : Exception(message);

public static class ImageProcessor
{
    // En liten fil kan deklarera enorma mått. Avkodas den i en 32-bitarsprocess tar
    // minnet slut, app poolen stoppas och siten svarar 503.
    private const long MaxPixels = 50_000_000;

    private const int FullMaxEdge = 2560;
    private const int ThumbMaxEdge = 400;

    public static async Task<(int Width, int Height)> SaveAsync(
        Stream source, string fullPath, string thumbPath, CancellationToken ct)
    {
        source.Position = 0;

        ImageInfo info;
        try
        {
            info = await Image.IdentifyAsync(source, ct);
        }
        catch (UnknownImageFormatException)
        {
            throw new ImageRejectedException("Filen är inte en bild som kan läsas.");
        }

        if ((long)info.Width * info.Height > MaxPixels)
        {
            throw new ImageRejectedException(
                $"Bilden är {info.Width}×{info.Height} pixlar, vilket överskrider gränsen.");
        }

        source.Position = 0;

        // TargetSize skalar även UPP, så den sätts bara när bilden faktiskt är för stor.
        // Syftet är att slippa hålla hela originalet i minnet, inte att ändra måtten.
        var options = info.Width > FullMaxEdge || info.Height > FullMaxEdge
            ? new DecoderOptions { TargetSize = new Size(FullMaxEdge, FullMaxEdge) }
            : new DecoderOptions();

        using var image = await Image.LoadAsync(options, source, ct);

        // Mobilfoton bär rotationen i EXIF, och omkodningen nedan kastar all metadata.
        image.Mutate(x => x.AutoOrient());
        Downscale(image, FullMaxEdge);

        await image.SaveAsJpegAsync(fullPath, new JpegEncoder { Quality = 85, SkipMetadata = true }, ct);

        using var thumb = image.Clone(x => { });
        Downscale(thumb, ThumbMaxEdge);
        await thumb.SaveAsJpegAsync(thumbPath, new JpegEncoder { Quality = 75, SkipMetadata = true }, ct);

        return (image.Width, image.Height);
    }

    private static void Downscale(Image image, int maxEdge)
    {
        if (image.Width <= maxEdge && image.Height <= maxEdge) return;

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(maxEdge, maxEdge),
            Mode = ResizeMode.Max
        }));
    }
}
