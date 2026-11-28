namespace JadeCourt.Solo;

public sealed record WindwardTile(string Kind, string? Name = null, int? Specialty = null);
public sealed record WindwardGood(string Name, int Points, int Cargo, int Delivered);
public sealed record WindwardPort(int Index, int? Production, int[] Stock);
public sealed record WindwardPosition(
    string Status, int Moves, int Width, int Height, int Season, int SeasonLimit, int ShipIndex,
    int SailRemaining, int OrdersRemaining, int CargoCapacity, int PortLimit, int StockLimit,
    int CharterPoints, int TargetPoints, WindwardTile?[] Chart, WindwardGood[] Goods, WindwardPort[] Ports,
    SoloAction[] LegalActions)
{
    public bool Finished => Status != "active";
}

public sealed class WindwardGame
{
    public const string Id = "windward-ledger";
    public const string Variant = "soundings";
    public const int CellCount = Width * Height;
    public const int GoodCount = 3;
    private const int Width = 7, Height = 6, Home = 21;
    private const int SeasonLimit = 22, SailAllowance = 2, OrderAllowance = 2;
    private const int CargoCapacity = 4, PortLimit = 2, StockLimit = 4, TargetPoints = 18;
    private const int MaxMoves = SeasonLimit * (SailAllowance + OrderAllowance + 1);
    // The order of these tokens is part of the v1 replay format.
    private const string Tokens = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private static readonly string[] GoodNames = ["Kelp", "Dye", "Cedar"];
    private static readonly int[] Values = [2, 3, 4];
    private static readonly SoloAction[] Actions = [
        .. Enumerable.Range(0, CellCount).Select(index => new SoloAction("sail", Index: index)),
        new("found"),
        .. Enumerable.Range(0, GoodCount).Select(value => new SoloAction("assign", Value: value)),
        .. Enumerable.Range(0, GoodCount).Select(value => new SoloAction("load", Value: value)),
        new("deliver"), new("end-season")
    ];
    private static readonly WindwardTile[] Coast = CreateChart();

    public string Start(string variant)
    {
        if (variant != Variant) throw new ArgumentException("Unsupported voyage variant.", nameof(variant));
        return SoloCheckpoint.Write(Id, new Save(""));
    }

    public string Apply(string checkpoint, SoloAction action)
    {
        var (save, voyage) = Read(checkpoint);
        if (!voyage.Legal().Contains(action)) throw new InvalidOperationException("That order is not available.");
        return SoloCheckpoint.Write(Id, new Save(save.Trail + Tokens[Array.IndexOf(Actions, action)]));
    }

    public WindwardPosition View(string checkpoint)
    {
        var (save, voyage) = Read(checkpoint);
        return new(voyage.Status, save.Trail.Length, Width, Height, voyage.Season, SeasonLimit,
            voyage.Ship, voyage.Sails, voyage.Orders, CargoCapacity, PortLimit, StockLimit, voyage.Points, TargetPoints,
            Coast.Select((tile, index) => voyage.Explored[index] ? tile : null).ToArray(),
            GoodNames.Select((name, value) => new WindwardGood(name, Values[value], voyage.Cargo[value], voyage.Delivered[value])).ToArray(),
            voyage.Ports.ToArray(),
            voyage.Legal().ToArray());
    }

    private sealed record Save(string Trail);

    private static (Save, Voyage) Read(string checkpoint)
    {
        var save = SoloCheckpoint.Read<Save>(Id, checkpoint);
        if (save.Trail is null || save.Trail.Length > MaxMoves) throw SoloCheckpoint.Invalid(Id);
        var voyage = new Voyage();
        foreach (var token in save.Trail)
        {
            var index = Tokens.IndexOf(token);
            if (index < 0 || index >= Actions.Length || !voyage.Legal().Contains(Actions[index]))
                throw SoloCheckpoint.Invalid(Id);
            voyage.Take(Actions[index]);
        }
        return (save, voyage);
    }

    private sealed class Voyage
    {
        public int Ship { get; private set; } = Home;
        public int Season { get; private set; } = 1;
        public int Sails { get; private set; } = SailAllowance;
        public int Orders { get; private set; } = OrderAllowance;
        public bool[] Explored { get; } = new bool[CellCount];
        public int[] Cargo { get; } = new int[GoodCount];
        public int[] Delivered { get; } = new int[GoodCount];
        public List<WindwardPort> Ports { get; } = [];
        public int Points => Delivered.Select((count, good) => count * Values[good]).Sum();
        public string Status { get; private set; } = "active";

        public Voyage() => Survey();

        public IEnumerable<SoloAction> Legal()
        {
            if (Status != "active") yield break;
            if (Sails > 0)
                foreach (var index in Neighbors(Ship))
                    if (Explored[index] && Coast[index].Kind != "land")
                        yield return new("sail", Index: index);

            if (Orders > 0)
            {
                var port = Ports.Find(port => port.Index == Ship);
                var cargoCount = Cargo.Sum();
                if (port is not null)
                {
                    for (var good = 0; good < GoodCount; good++)
                    {
                        if (port.Production != good) yield return new("assign", Value: good);
                        if (cargoCount < CargoCapacity && port.Stock[good] > 0) yield return new("load", Value: good);
                    }
                }
                else if (Ship == Home && cargoCount > 0) yield return new("deliver");
                else if (Explored[Ship] && Coast[Ship].Kind == "coast" && Ports.Count < PortLimit) yield return new("found");
            }
            yield return new("end-season");
        }

        public void Take(SoloAction action)
        {
            switch (action.Kind)
            {
                case "sail":
                    Ship = action.Index!.Value;
                    Sails--;
                    Survey();
                    return;
                case "end-season":
                    if (Season == SeasonLimit)
                    {
                        Status = "lost";
                        Sails = Orders = 0;
                        return;
                    }
                    foreach (var port in Ports)
                        if (port.Production is int good)
                            port.Stock[good] = Math.Min(StockLimit, port.Stock[good] + (Coast[port.Index].Specialty == good ? 2 : 1));
                    Season++;
                    Sails = SailAllowance;
                    Orders = OrderAllowance;
                    return;
                case "found":
                    Ports.Add(new(Ship, null, new int[GoodCount]));
                    break;
                case "assign":
                    var slot = Ports.FindIndex(port => port.Index == Ship);
                    Ports[slot] = Ports[slot] with { Production = action.Value };
                    break;
                case "load":
                    var load = action.Value!.Value;
                    Ports.Find(port => port.Index == Ship)!.Stock[load]--;
                    Cargo[load]++;
                    break;
                case "deliver":
                    for (var good = 0; good < GoodCount; good++)
                    {
                        Delivered[good] += Cargo[good];
                        Cargo[good] = 0;
                    }
                    if (Points >= TargetPoints && Delivered.All(count => count > 0)) Status = "won";
                    break;
            }
            Orders--;
        }

        private void Survey()
        {
            Explored[Ship] = true;
            foreach (var index in Neighbors(Ship)) Explored[index] = true;
        }

        private static IEnumerable<int> Neighbors(int index)
        {
            if (index >= Width) yield return index - Width;
            if (index % Width < Width - 1) yield return index + 1;
            if (index < CellCount - Width) yield return index + Width;
            if (index % Width > 0) yield return index - 1;
        }
    }

    private static WindwardTile[] CreateChart() =>
        ("##~~#~~" +
         "#~T~~K#" +
         "~R~#~~~" +
         "H~~E~#~" +
         "~#~~~P~" +
         "~~#~~~~")
        .Select<char, WindwardTile>(symbol => symbol switch
        {
            '#' => new("land"),
            'H' => new("home", "Lantern Dock"),
            'R' => new("coast", "Reed Haven", 0),
            'T' => new("coast", "Tern Quay", 1),
            'E' => new("coast", "Ember Inlet", 1),
            'K' => new("coast", "Kite Bay", 2),
            'P' => new("coast", "Pine Hook", 2),
            _ => new("water")
        }).ToArray();
}
