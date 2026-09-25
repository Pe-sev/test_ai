namespace Slideshow.Api.Storage;

// "bild2" ska sorteras före "bild10". CompareOptions.NumericOrdering finns först i .NET 10.
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numY = y.AsSpan(startY, j - startY).TrimStart('0');

                // Fler siffror efter strippade nollor betyder alltid ett större tal.
                if (numX.Length != numY.Length) return numX.Length - numY.Length;

                var digits = numX.SequenceCompareTo(numY);
                if (digits != 0) return digits;
            }
            else
            {
                var chars = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (chars != 0) return chars;
                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
