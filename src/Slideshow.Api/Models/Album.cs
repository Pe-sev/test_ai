namespace Slideshow.Api.Models;

// Hierarkisk: ett bildspel syns för den som har minst albumets egen nivå.
public enum AlbumAccess
{
    Public = 0,
    Extended = 1,
    Family = 2
}

// Heter Album och inte Slideshow: en typ med samma namn som rotnamnrymden gör
// "Slideshow.Api.*" tvetydigt för kompilatorn.
public sealed class Album
{
    public required string Slug { get; init; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }

    // Äldre bildspel saknar fältet och får då Public, vilket är hur de betedde sig förut.
    public AlbumAccess Access { get; set; }

    // Lägre värde hamnar högre upp på indexsidan. Äldre bildspel saknar fältet och
    // får då 0, vilket behåller datumordningen tills man flyttat något.
    public int SortOrder { get; set; }

    // Null tills uppladdningen är klar. Utkast visas bara för inloggad.
    public DateTimeOffset? PublishedUtc { get; set; }

    public List<Slide> Slides { get; init; } = [];
}
