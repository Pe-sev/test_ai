namespace Slideshow.Api.Models;

public sealed class Slide
{
    public required string Id { get; init; }
    public required string StoredName { get; init; }
    public required string OriginalName { get; init; }
    public string Caption { get; set; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
}
