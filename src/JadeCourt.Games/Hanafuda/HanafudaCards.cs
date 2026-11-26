namespace JadeCourt.Games.Hanafuda;

public readonly record struct KoiKoiCard(int Id, int Month, string Kind, string Name);
public readonly record struct KoiKoiYaku(string Name, int Points);

internal static class HanafudaCards
{
    public const string Bright = "bright", Animal = "animal", Ribbon = "ribbon", Plain = "plain";
    public const int CherryCurtain = 8, Butterfly = 20, Boar = 24, Moon = 28, Cup = 32, Deer = 36, Rainman = 40;

    private static readonly string[] Plants =
    [
        "Pine", "Plum", "Cherry", "Wisteria", "Iris", "Peony", "Bush Clover", "Pampas",
        "Chrysanthemum", "Maple", "Willow", "Paulownia"
    ];

    private static readonly KoiKoiCard[] Deck = CreateDeck(
    [
        (Bright, "Crane"), (Ribbon, "Red Poetry Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Bush Warbler"), (Ribbon, "Red Poetry Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Bright, "Curtain"), (Ribbon, "Red Poetry Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Cuckoo"), (Ribbon, "Red Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Eight-Plank Bridge"), (Ribbon, "Red Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Butterfly"), (Ribbon, "Blue Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Boar"), (Ribbon, "Red Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Bright, "Moon"), (Animal, "Geese"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Sake Cup"), (Ribbon, "Blue Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Animal, "Deer"), (Ribbon, "Blue Ribbon"), (Plain, "Plain 1"), (Plain, "Plain 2"),
        (Bright, "Rainman"), (Animal, "Swallow"), (Ribbon, "Red Ribbon"), (Plain, "Lightning"),
        (Bright, "Phoenix"), (Plain, "Plain 1"), (Plain, "Plain 2"), (Plain, "Plain 3")
    ]);

    public static int Month(int id) => id / 4 + 1;
    public static KoiKoiCard Card(int id) => Deck[id];

    public static KoiKoiYaku[] Yaku(IEnumerable<int> captured)
    {
        var cards = captured.Select(Card).ToArray();
        var brights = cards.Count(card => card.Kind == Bright);
        var animals = cards.Count(card => card.Kind == Animal);
        var ribbons = cards.Count(card => card.Kind == Ribbon);
        var plains = cards.Count(card => card.Kind == Plain || card.Id == Cup);
        bool Has(int id) => cards.Any(card => card.Id == id);

        var yaku = new List<KoiKoiYaku>();
        if (brights == 5) yaku.Add(new("Five Brights", 10));
        else if (brights == 4) yaku.Add(Has(Rainman) ? new("Rainy Four Brights", 7) : new("Four Brights", 8));
        else if (brights == 3 && !Has(Rainman)) yaku.Add(new("Three Brights", 5));

        if (Has(Boar) && Has(Deer) && Has(Butterfly)) yaku.Add(new("Boar, Deer, Butterfly", 5 + animals - 3));
        else if (animals >= 5) yaku.Add(new("Animals", animals - 4));

        var red = Has(1) && Has(5) && Has(9);
        var blue = Has(21) && Has(33) && Has(37);
        if (red && blue) yaku.Add(new("Red and Blue Ribbons", 10 + ribbons - 6));
        else if (red) yaku.Add(new("Red Poetry Ribbons", 5 + ribbons - 3));
        else if (blue) yaku.Add(new("Blue Ribbons", 5 + ribbons - 3));
        else if (ribbons >= 5) yaku.Add(new("Ribbons", ribbons - 4));

        if (plains >= 10) yaku.Add(new("Plain Cards", plains - 9));
        if (Has(Cup) && Has(CherryCurtain)) yaku.Add(new("Flower Viewing", 5));
        if (Has(Cup) && Has(Moon)) yaku.Add(new("Moon Viewing", 5));
        return [.. yaku];
    }

    private static KoiKoiCard[] CreateDeck((string Kind, string Name)[] motifs) =>
        motifs.Select((motif, id) => new KoiKoiCard(id, Month(id), motif.Kind, $"{Plants[Month(id) - 1]} — {motif.Name}")).ToArray();
}
