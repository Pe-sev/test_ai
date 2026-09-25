namespace Slideshow.Api.Models;

// Heter Album och inte Slideshow: en typ med samma namn som rotnamnrymden gör
// "Slideshow.Api.*" tvetydigt för kompilatorn.
public sealed class Album
{
    public required string Slug { get; init; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }

    // Null tills uppladdningen är klar. Utkast visas bara för inloggad.
    public DateTimeOffset? PublishedUtc { get; set; }

    public List<Slide> Slides { get; init; } = [];
}
