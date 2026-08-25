using RotMGGameDataService.Data;
using Xunit;

namespace RotMGGameDataService.Tests.Data;

public sealed class GameDataDiffBuilderTests
{
    [Fact]
    public void Create_ClassifiesObjectChangesAndSectionReplacements()
    {
        var from = Manifest(
            "from",
            new Dictionary<string, GameObjectRecord>
            {
                ["1"] = Object(1, "hash-1"),
                ["2"] = Object(2, "hash-2-old"),
            },
            "stats-old",
            "fame-same");
        var to = Manifest(
            "to",
            new Dictionary<string, GameObjectRecord>
            {
                ["2"] = Object(2, "hash-2-new"),
                ["3"] = Object(3, "hash-3"),
            },
            "stats-new",
            "fame-same");

        var diff = GameDataDiffBuilder.Create(from, to);

        Assert.Equal([3], diff.AddedObjects.Keys.Select(int.Parse));
        Assert.Equal([2], diff.ModifiedObjects.Keys.Select(int.Parse));
        Assert.Equal([1], diff.RemovedObjectIds);
        Assert.NotNull(diff.PlayerStats);
        Assert.Null(diff.FameBonuses);
        Assert.Equal("to", diff.ToBuildId);
        Assert.Equal("stats-new", diff.PlayerStatsHash);
        Assert.Equal("fame-same", diff.FameBonusesHash);
        Assert.Equal(2, diff.ObjectCount);
        Assert.Equal(GameDataJson.HashObjectCatalog(to.Objects), diff.ObjectsCatalogHash);
    }

    [Fact]
    public void HashObjectCatalog_OrdersByOrdinalKeyAndIgnoresInsertionOrder()
    {
        // A consumer reproduces this hash in another language from the object
        // ids and metadata hashes alone, so the ordering has to be ordinal and
        // independent of how the map was built.
        var ascending = new Dictionary<string, GameObjectRecord>
        {
            ["10"] = Object(10, "hash-10"),
            ["9"] = Object(9, "hash-9"),
        };
        var descending = new Dictionary<string, GameObjectRecord>
        {
            ["9"] = Object(9, "hash-9"),
            ["10"] = Object(10, "hash-10"),
        };

        Assert.Equal(
            GameDataJson.HashObjectCatalog(ascending),
            GameDataJson.HashObjectCatalog(descending));

        // "10" sorts before "9" under ordinal comparison, which is what the
        // consumer's byte-wise sort produces.
        var expected = GameDataJson.HashBytes(
            System.Text.Encoding.UTF8.GetBytes("10:hash-10\n9:hash-9"));
        Assert.Equal(expected, GameDataJson.HashObjectCatalog(ascending));
    }

    private static GameDataManifest Manifest(
        string id,
        IReadOnlyDictionary<string, GameObjectRecord> objects,
        string statsHash,
        string fameHash) => new(
        1,
        id,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        DateTimeOffset.UnixEpoch,
        objects,
        new Dictionary<string, PlayerStatRecord>
        {
            ["1"] = new(1, "stat", 0, false, null, null, false, null, statsHash),
        },
        [new("bonus", 1, null, null, null, null, null, 0, 0, 0, false, [], fameHash)],
        statsHash,
        fameHash);

    private static GameObjectRecord Object(int id, string metadataHash) => new(
        id,
        $"object-{id}",
        "Equipment",
        "Equipment",
        null,
        null,
        new string('a', 64),
        metadataHash);
}
