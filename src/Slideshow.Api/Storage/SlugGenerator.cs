using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Slideshow.Api.Storage;

public static partial class SlugGenerator
{
    private const int MaxLength = 64;

    public static string Create(string? title, DateTimeOffset now)
    {
        var slug = Slugify(title);

        // En titel med bara emoji eller skiljetecken ger tom slug.
        return slug.Length == 0 ? $"bildspel-{now:yyyyMMdd-HHmmss}" : slug;
    }

    public static bool IsValid(string? slug) =>
        slug is { Length: > 0 and <= MaxLength } && SlugPattern().IsMatch(slug);

    private static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // FormD delar å/ä/ö i grundbokstav + diakritiskt tecken, som sedan kastas.
        var normalized = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var pendingDash = false;

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            var lower = char.ToLowerInvariant(ch);

            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                builder.Append(lower);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = builder.ToString();
        return slug.Length > MaxLength ? slug[..MaxLength].Trim('-') : slug;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();
}
