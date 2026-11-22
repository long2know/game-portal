namespace JadeCourt.Games.Farkle;

internal static class FarkleScore
{
    public const int MaxDice = 6;
    public const int MinPoints = 50;
    public const int MaxPoints = 3000;

    // Zero means at least one selected die could not be scored.
    public static int Selection(ReadOnlySpan<int> dice, int mask)
    {
        if (dice.Length is < 1 or > MaxDice || mask <= 0 || mask >= (1 << dice.Length)) return 0;

        Span<int> counts = stackalloc int[MaxDice + 1];
        counts.Clear();
        var selected = 0;
        for (var i = 0; i < dice.Length; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            if (dice[i] is < 1 or > 6) return 0;
            counts[dice[i]]++;
            selected++;
        }

        if (selected == MaxDice)
        {
            var pairs = 0;
            var triples = 0;
            var fours = 0;
            var singles = 0;
            for (var face = 1; face <= 6; face++)
            {
                switch (counts[face])
                {
                    case 1: singles++; break;
                    case 2: pairs++; break;
                    case 3: triples++; break;
                    case 4: fours++; break;
                }
            }
            if (triples == 2) return 2500;
            if (singles == 6 || pairs == 3 || (fours == 1 && pairs == 1)) return 1500;
        }

        var points = 0;
        for (var face = 1; face <= 6; face++)
        {
            switch (counts[face])
            {
                case 0: break;
                case < 3 when face == 1: points += 100 * counts[face]; break;
                case < 3 when face == 5: points += 50 * counts[face]; break;
                case < 3: return 0;
                case 3: points += face == 1 ? 300 : face * 100; break;
                case 4: points += 1000; break;
                case 5: points += 2000; break;
                case 6: points += 3000; break;
            }
        }
        return points;
    }

    public static bool HasChoice(ReadOnlySpan<int> dice)
    {
        for (var mask = 1; mask < (1 << dice.Length); mask++)
            if (Selection(dice, mask) > 0) return true;
        return false;
    }
}
