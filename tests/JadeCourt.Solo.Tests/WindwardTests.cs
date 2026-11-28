using System.Text.Json;
using JadeCourt.Solo;

namespace JadeCourt.Solo.Tests;

public sealed class WindwardTests
{
    private readonly WindwardGame _game = new();
    private static readonly SoloAction Found = new("found"), Deliver = new("deliver"), End = new("end-season");

    [Fact]
    public void SailingChartsOnlyNearbyCellsAndDoesNotAdvertiseFogOrLand()
    {
        var start = Fresh();
        var dock = _game.View(start);
        Assert.Equal(21, dock.ShipIndex);
        Assert.Equal(new[] { 14, 21, 22, 28 }, Enumerable.Range(0, dock.Chart.Length).Where(i => dock.Chart[i] is not null));
        Assert.Equal(new[] { Sail(14), Sail(22), Sail(28), End }, dock.LegalActions);
        Reject(start, Sail(15), Sail(20), Found, Deliver, new("end-season", Index: 0));

        var east = Play(start, Sail(22));
        var besideReeds = _game.View(east);
        Assert.Equal("Reed Haven", besideReeds.Chart[15]?.Name);
        Assert.Equal("land", besideReeds.Chart[29]?.Kind);
        Assert.Null(besideReeds.Chart[24]);
        Assert.Equal(new[] { Sail(15), Sail(23), Sail(21), End }, besideReeds.LegalActions);
        Reject(east, Sail(29), Sail(24), Sail(15) with { Value = 0 });

        var reeds = Play(east, Sail(15));
        var moored = _game.View(reeds);
        Assert.Equal(1, moored.Season);
        Assert.Equal(0, moored.SailRemaining);
        Assert.Equal(new[] { Found, End }, moored.LegalActions);
        Reject(reeds, Sail(22), Assign(0), Load(0), Deliver);
        Assert.Equal(reeds, Play(Fresh(), Sail(22), Sail(15)));
    }

    [Fact]
    public void FoundingProductionStockLoadingAndHomeDeliveryWinAcrossThreeGoods()
    {
        var reeds = Play(Fresh(), Sail(22), Sail(15), Found, Assign(0));
        Assert.Equal(new[] { 0, 0, 0 }, Assert.Single(_game.View(reeds).Ports).Stock);
        var forecast = Play(reeds, End);
        var seasonTwo = _game.View(forecast);
        Assert.Equal(2, seasonTwo.Season);
        Assert.Equal(2, seasonTwo.OrdersRemaining);
        Assert.Equal(new[] { 2, 0, 0 }, Assert.Single(seasonTwo.Ports).Stock);
        Reject(forecast, Assign(0), Load(1), Found, Deliver);

        var loaded = Play(forecast, Load(0), Load(0), Sail(22), Sail(21));
        var waitingAtHome = _game.View(loaded);
        Assert.Equal(2, waitingAtHome.Goods[0].Cargo);
        Assert.Equal(0, waitingAtHome.Ports[0].Stock[0]);
        Assert.Equal(0, waitingAtHome.CharterPoints);
        Reject(loaded, Deliver);

        var firstDelivery = Play(loaded, End, Deliver);
        Assert.Equal(new WindwardGood("Kelp", 2, 0, 2), _game.View(firstDelivery).Goods[0]);
        Assert.Equal(4, _game.View(firstDelivery).CharterPoints);
        var dye = Play(firstDelivery, Sail(22), Sail(23), End, Sail(24), Found, Assign(1), End);
        var dyeReady = _game.View(dye);
        Assert.Equal(new[] { 15, 24 }, dyeReady.Ports.Select(port => port.Index));
        Assert.Equal(new[] { 0, 2, 0 }, dyeReady.Ports[1].Stock);
        Assert.Equal(new[] { 4, 0, 0 }, dyeReady.Ports[0].Stock);
        var secondDelivery = Play(dye, Load(1), Load(1), Sail(23), Sail(22), End, Sail(21), Deliver);
        Assert.Equal(10, _game.View(secondDelivery).CharterPoints);

        var cedar = Play(secondDelivery, Sail(22), End, Sail(15), Assign(2), End);
        Assert.Equal(new[] { 4, 0, 1 }, _game.View(cedar).Ports[0].Stock);
        var won = Play(cedar, Load(2), End, Load(2), Sail(22), Sail(21), Deliver);
        var result = _game.View(won);
        Assert.Equal(9, result.Season);
        Assert.Equal("won", result.Status);
        Assert.True(result.Finished);
        Assert.Equal(18, result.CharterPoints);
        Assert.Equal(new[] { 2, 2, 2 }, result.Goods.Select(good => good.Delivered));
        Assert.All(result.Goods, good => Assert.Equal(0, good.Cargo));
        Assert.Empty(result.LegalActions);
        Reject(won, Sail(22), End);
    }

    [Fact]
    public void CargoIsLimitedAndPointsAloneCannotPreventSeasonExpiry()
    {
        var ready = Play(Fresh(), Sail(22), Sail(15), Found, Assign(2), End, End, End, End);
        Assert.Equal(4, _game.View(ready).Ports[0].Stock[2]);
        var full = Play(ready, Load(2), Load(2), End, Load(2), Load(2), End);
        Assert.Equal(4, _game.View(full).Goods[2].Cargo);
        Assert.Equal(2, _game.View(full).Ports[0].Stock[2]);
        Reject(full, Load(2));
        var oneGood = Play(full, Sail(22), Sail(21), Deliver, End, Sail(22), Sail(15), Load(2), End, Sail(22), Sail(21), Deliver);
        var active = _game.View(oneGood);
        Assert.Equal(20, active.CharterPoints);
        Assert.Equal(new[] { 0, 0, 5 }, active.Goods.Select(good => good.Delivered));
        Assert.False(active.Finished);
        var last = Play(oneGood, Enumerable.Repeat(End, 13).ToArray());
        Assert.Equal(22, _game.View(last).Season);
        Assert.False(_game.View(last).Finished);
        var expired = Play(last, End);
        var result = _game.View(expired);
        Assert.Equal(22, result.Season);
        Assert.Equal("lost", result.Status);
        Assert.True(result.Finished);
        Assert.Equal(20, result.CharterPoints);
        Assert.Equal(_game.View(last).Ports[0].Stock, result.Ports[0].Stock);
        Assert.Empty(result.LegalActions);
        Reject(expired, End);
    }

    [Fact]
    public void AThirdSiteCanBeChartedButNotFounded()
    {
        var visited = Play(Fresh(), Sail(22), Sail(15), Found, End, Sail(8), Sail(9), Found, End, Sail(10), Sail(11), End, Sail(12));
        var position = _game.View(visited);
        Assert.Equal("Kite Bay", position.Chart[position.ShipIndex]?.Name);
        Assert.Equal(new[] { 15, 9 }, position.Ports.Select(port => port.Index));
        Reject(visited, Found);
    }

    [Fact]
    public void CheckpointsRejectUnknownVersionsMalformedTrailsAndIllegalHistories()
    {
        Assert.Throws<ArgumentException>(() => _game.Start("other"));
        var valid = Play(Fresh(), Sail(22), Sail(15), Found, Assign(0), End);
        var prefix = "windward-ledger-v1:";
        Assert.StartsWith(prefix, valid, StringComparison.Ordinal);
        var trail = JsonDocument.Parse(valid[prefix.Length..]).RootElement.GetProperty("Trail").GetString()!;
        foreach (var corrupt in new[]
        {
            valid.Replace("-v1:", "-v2:", StringComparison.Ordinal),
            prefix + """{"Trail":null}""",
            prefix + """{"Trail":"","Trail":""}""",
            prefix + """{"Trail":"","Chart":[]}""",
            prefix + """{"Trail":"?"}""",
            prefix + JsonSerializer.Serialize(new { Trail = trail[1..] }),
            prefix + JsonSerializer.Serialize(new { Trail = new string(trail[^1], 111) }),
            Play(Fresh(), Enumerable.Repeat(End, 22).ToArray()).Replace("\"}", $"{trail[^1]}\"}}", StringComparison.Ordinal),
        })
        {
            Assert.Throws<InvalidDataException>(() => _game.View(corrupt));
            Assert.Throws<InvalidDataException>(() => _game.Apply(corrupt, End));
        }
    }

    private string Fresh() => _game.Start("soundings");
    private string Play(string checkpoint, params SoloAction[] actions) => actions.Aggregate(checkpoint, _game.Apply);
    private static SoloAction Sail(int index) => new("sail", Index: index);
    private static SoloAction Assign(int value) => new("assign", Value: value);
    private static SoloAction Load(int value) => new("load", Value: value);

    private void Reject(string checkpoint, params SoloAction[] actions)
    {
        var before = JsonSerializer.Serialize(_game.View(checkpoint));
        foreach (var action in actions)
        {
            Assert.DoesNotContain(action, _game.View(checkpoint).LegalActions);
            Assert.Throws<InvalidOperationException>(() => _game.Apply(checkpoint, action));
            Assert.Equal(before, JsonSerializer.Serialize(_game.View(checkpoint)));
        }
    }
}
