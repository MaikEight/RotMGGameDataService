using System.Text;
using RotMGGameDataService.Data;
using RotMGGameDataService.Extraction;
using Xunit;

namespace RotMGGameDataService.Tests.Extraction;

public sealed class FameBonusRecordTests
{
    /// <summary>
    /// The section hash is what tells a consumer whether fame bonuses changed,
    /// so the published order has to depend only on the content.
    /// </summary>
    [Fact]
    public void CreateFameBonuses_OrdersByCodeThenIdWhateverTheInputOrder()
    {
        var records = ExtractionProbe.CreateFameBonuses(
            [Definition("Zebra", 2), Definition("Apple", 2), Definition("Middle", 1)]);

        Assert.Equal(["Middle", "Apple", "Zebra"], records.Select(record => record.Id));

        var hashes = records.Select(record => record.MetadataHash).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        Assert.Equal(
            hashes,
            ExtractionProbe.CreateFameBonuses(
                    [Definition("Middle", 1), Definition("Apple", 2), Definition("Zebra", 2)])
                .Select(record => record.MetadataHash));
    }

    [Fact]
    public void CreateFameBonuses_NormalizesBlankTextToNull()
    {
        var record = Assert.Single(ExtractionProbe.CreateFameBonuses(
            [new FameBonusDefinition("Blank", 0, "  ", "", null, "", "   ", 0, 0f, 0, false, [])]));

        Assert.Null(record.DisplayGroup);
        Assert.Null(record.DisplayCategory);
        Assert.Null(record.DisplayName);
        Assert.Null(record.ShortDisplayName);
        Assert.Null(record.Description);
    }

    /// <summary>
    /// The condition shape is unchanged from schema 1, so an existing consumer
    /// keeps reading the same property names.
    /// </summary>
    [Fact]
    public void FameConditionRecord_KeepsItsPublishedShape()
    {
        var json = Encoding.UTF8.GetString(
            GameDataJson.Serialize(new FameConditionRecord(25, "MonsterKills", "StatValue")));

        Assert.Equal(
            """{"threshold":25,"stat":"MonsterKills","value":"StatValue"}""",
            json);
    }

    [Fact]
    public void FameBonusRecord_OmitsTextTheClientDoesNotDeclare()
    {
        var record = Assert.Single(ExtractionProbe.CreateFameBonuses(
            [new FameBonusDefinition("Bare", 7, null, null, null, null, null, 5, 0f, 0, false, [])]));
        var json = Encoding.UTF8.GetString(GameDataJson.Serialize(record));

        Assert.DoesNotContain("\"description\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"shortDisplayName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"Bare\"", json, StringComparison.Ordinal);
    }

    private static FameBonusDefinition Definition(string id, int code) =>
        new(id, code, "Group", "Category", id, id, $"{id} description", 100, 1f, 0, false,
            [new FameConditionDefinition("StatValue", 1, id)]);
}
