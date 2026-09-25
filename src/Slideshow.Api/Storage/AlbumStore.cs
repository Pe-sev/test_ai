using System.Text.Json;
using Slideshow.Api.Models;

namespace Slideshow.Api.Storage;

public sealed class AlbumStore
{
    public const int MaxSlidesPerAlbum = 75;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _dataRoot;

    public string MediaRoot { get; }

    public AlbumStore(string appDataPath)
    {
        _dataRoot = Path.Combine(appDataPath, "data");
        MediaRoot = Path.Combine(appDataPath, "media");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(MediaRoot);
    }

    public async Task<Album?> GetAsync(string slug, CancellationToken ct = default)
    {
        if (!SlugGenerator.IsValid(slug)) return null;

        var path = DataPath(slug);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Album>(stream, JsonOptions, ct);
    }

    public async Task<IReadOnlyList<Album>> ListAsync(bool includeDrafts, CancellationToken ct = default)
    {
        var albums = new List<Album>();

        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*.json"))
        {
            await using var stream = File.OpenRead(file);
            var album = await JsonSerializer.DeserializeAsync<Album>(stream, JsonOptions, ct);

            if (album is null) continue;
            if (!includeDrafts && album.PublishedUtc is null) continue;

            albums.Add(album);
        }

        return albums
            .OrderBy(a => a.SortOrder)
            .ThenByDescending(a => a.CreatedUtc)
            .ToList();
    }

    // Klienten skickar hela den önskade ordningen, servern numrerar om. Idempotent,
    // och oberoende av hur ordningen ändrades i gränssnittet.
    public async Task SetOrderAsync(IReadOnlyList<string> slugs, CancellationToken ct = default)
    {
        for (var position = 0; position < slugs.Count; position++)
        {
            var album = await GetAsync(slugs[position], ct);
            if (album is null || album.SortOrder == position) continue;

            album.SortOrder = position;
            await SaveAsync(album, ct);
        }
    }

    public async Task<Album> CreateAsync(string? title, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var baseSlug = SlugGenerator.Create(title, now);

        var slug = baseSlug;
        for (var suffix = 2; File.Exists(DataPath(slug)); suffix++)
        {
            slug = $"{baseSlug}-{suffix}";
        }

        var existing = await ListAsync(includeDrafts: true, ct);

        var album = new Album
        {
            Slug = slug,
            Title = string.IsNullOrWhiteSpace(title) ? slug : title.Trim(),
            CreatedUtc = now,
            // Under det lägsta befintliga värdet: nya bildspel hamnar högst upp.
            SortOrder = existing.Count == 0 ? 0 : existing.Min(a => a.SortOrder) - 1
        };

        Directory.CreateDirectory(FullDirectory(slug));
        Directory.CreateDirectory(ThumbDirectory(slug));
        await SaveAsync(album, ct);

        return album;
    }

    public async Task AddSlideAsync(Album album, Slide slide, CancellationToken ct = default)
    {
        album.Slides.Add(slide);

        // Sorteras om vid varje tillägg, så ordningen stämmer även om uppladdningarna
        // kommer i annan ordning än filnamnen.
        album.Slides.Sort((a, b) => NaturalComparer.Instance.Compare(a.OriginalName, b.OriginalName));

        await SaveAsync(album, ct);
    }

    public async Task SaveAsync(Album album, CancellationToken ct = default)
    {
        var path = DataPath(album.Slug);
        var temp = path + ".tmp";

        await _writeGate.WaitAsync(ct);
        try
        {
            // Skriv först, byt sedan namn: en app pool-recycle mitt i en skrivning
            // får inte lämna en halv JSON-fil efter sig.
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, album, JsonOptions, ct);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Delete(string slug)
    {
        if (!SlugGenerator.IsValid(slug)) return;

        var json = DataPath(slug);
        if (File.Exists(json)) File.Delete(json);

        var media = Path.Combine(MediaRoot, slug);
        if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
    }

    public string FullDirectory(string slug) => Path.Combine(MediaRoot, slug, "full");

    public string ThumbDirectory(string slug) => Path.Combine(MediaRoot, slug, "thumb");

    private string DataPath(string slug) => Path.Combine(_dataRoot, slug + ".json");
}
