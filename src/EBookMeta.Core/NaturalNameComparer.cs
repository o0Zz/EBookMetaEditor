namespace EBookMeta;

/// <summary>
/// Orders names the way a person reads them, so <c>2.jpg</c> comes before
/// <c>10.jpg</c>.
/// </summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    /// <summary>The shared instance; the comparer holds no state.</summary>
    internal static NaturalNameComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        int i = 0;
        int j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int startX = i;
                int startY = j;

                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                // Compared as text with leading zeros stripped rather than parsed
                // as an integer: a scanner that names pages with a 20-digit
                // timestamp would overflow every numeric type there is.
                string numberX = x.Substring(startX, i - startX).TrimStart('0');
                string numberY = y.Substring(startY, j - startY).TrimStart('0');

                if (numberX.Length != numberY.Length)
                {
                    return numberX.Length - numberY.Length;
                }

                int digits = string.CompareOrdinal(numberX, numberY);
                if (digits != 0)
                {
                    return digits;
                }

                continue;
            }

            int character = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (character != 0)
            {
                return character;
            }

            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
