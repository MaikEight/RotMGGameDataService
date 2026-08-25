using System.Text;
using RotMGGameDataService.Extraction;
using Xunit;

namespace RotMGGameDataService.Tests.Extraction;

/// <summary>
/// Reads the fixture taken verbatim from Realm build
/// <c>974bde45c06b313b1e425bc2cb222c75</c>, so the reader is exercised against
/// the client's real markup rather than an invented shape.
/// </summary>
public sealed class FameBonusXmlReaderTests
{
    private static readonly IReadOnlyList<FameBonusDefinition> Bonuses = ReadFixture();

    /// <summary>
    /// The two text fields are the reason this reader exists: the extractor's
    /// model types them <c>int</c>, so they parsed to zero and never reached
    /// the API.
    /// </summary>
    [Fact]
    public void ReadDocument_RecoversTheTextFieldsTheExtractorModelDrops()
    {
        var bonus = Single("Undead ForestAdversary");

        Assert.Equal("Kill 1000 Undead Forest Enemies", bonus.Description);
        Assert.Equal("Adversary", bonus.ShortDisplayName);
    }

    [Fact]
    public void ReadDocument_ReadsADescriptionForEveryBonusThatDeclaresOne()
    {
        Assert.All(Bonuses, bonus => Assert.False(string.IsNullOrWhiteSpace(bonus.Description)));

        // Only some entries carry a short name, so it stays null rather than
        // being filled with the long one.
        Assert.Null(Single("PotionDrinker").ShortDisplayName);
        Assert.Equal("Nemesis {0}", Single("Undead ForestNemesis").ShortDisplayName);
    }

    /// <summary>
    /// The client ships an escaped carriage return inside two ids. Passing that
    /// through breaks any consumer that keys on the id.
    /// </summary>
    [Fact]
    public void ReadDocument_StripsTheStrayCarriageReturnTheClientShipsInIds()
    {
        Assert.Contains(Bonuses, bonus => bonus.Id == "PotionDrinker");
        Assert.DoesNotContain(Bonuses, bonus => bonus.Id != bonus.Id.Trim());
    }

    [Fact]
    public void ReadDocument_ReadsIdAndCodeFromAttributesAndTheRestFromElements()
    {
        var bonus = Single("Undead ForestAdversary");

        Assert.Equal(548, bonus.Code);
        Assert.Equal("Enemy Bonuses", bonus.DisplayGroup);
        Assert.Equal("Undead Forest Kills", bonus.DisplayCategory);
        Assert.Equal("Undead Forest Adversary", bonus.DisplayName);
        Assert.Equal(100, bonus.AbsoluteBonus);
        Assert.Equal(0f, bonus.RelativeBonus);
    }

    [Fact]
    public void ReadDocument_ReadsRepeatableBonusesWithTheirRepeatLimit()
    {
        var repeatable = Single("Undead ForestNemesis");
        Assert.True(repeatable.Repeatable);
        Assert.Equal(20, repeatable.MaxRepeatCount);

        // Absent means false; the client omits the element for a one-off bonus.
        var single = Single("PotionDrinker");
        Assert.False(single.Repeatable);
        Assert.Equal(0, single.MaxRepeatCount);
    }

    /// <summary>
    /// Conditions are direct children of the bonus, with the type in the
    /// element's own text and the rest in attributes.
    /// </summary>
    [Fact]
    public void ReadDocument_ReadsEveryConditionOfAMultiConditionBonus()
    {
        Assert.Equal(
            [
                new FameConditionDefinition("StatValue", 1, "PirateCavesCompleted"),
                new FameConditionDefinition("StatValue", 1, "SnakePitsCompleted"),
                new FameConditionDefinition("StatValue", 2, "WineCellarsCompleted"),
            ],
            Single("TunnelRat").Conditions);
    }

    [Fact]
    public void ReadDocument_ReadsTheConditionTypesTheClientUses()
    {
        Assert.Equal(
            [new FameConditionDefinition("MaxedStat", 1, "health")],
            Single("Maxed_Life").Conditions);

        // FirstCharacter takes no stat, so it stays null rather than empty.
        var first = Assert.Single(Single("Ancestor").Conditions);
        Assert.Equal("FirstCharacter", first.Type);
        Assert.Null(first.Stat);
        Assert.Equal(0, first.Threshold);
    }

    [Fact]
    public void ReadDocument_ReadsAFractionalRelativeBonus()
    {
        Assert.Equal(7.5f, Single("TunnelRat").RelativeBonus);
        Assert.Equal(10f, Single("Ancestor").RelativeBonus);
    }

    [Fact]
    public void ReadDocument_SkipsElementsThatAreNotFameBonuses()
    {
        Assert.Equal(6, Bonuses.Count);
        Assert.DoesNotContain(Bonuses, bonus => bonus.Id == "NotAFameBonus");
    }

    /// <summary>
    /// The client marks an entry's type with a Class child when the element
    /// name does not carry it, which is how the extractor dispatches too.
    /// </summary>
    [Fact]
    public void ReadDocument_AcceptsABonusDeclaredByItsClassChild()
    {
        var bonuses = Read("""
            <Objects>
              <Object id="Declared"><Class>FameBonus</Class><AbsoluteBonus>10</AbsoluteBonus></Object>
              <Object id="Sword"><Class>Equipment</Class></Object>
            </Objects>
            """);

        var bonus = Assert.Single(bonuses);
        Assert.Equal("Declared", bonus.Id);
    }

    [Fact]
    public void ReadDocument_IgnoresAssetsThatAreNotXml()
    {
        Assert.Empty(FameBonusXmlReader.ReadDocument("not xml at all"u8.ToArray()));
        Assert.Empty(FameBonusXmlReader.ReadDocument([]));
    }

    [Fact]
    public void ReadDocument_IgnoresXmlWithoutFameBonuses()
    {
        Assert.Empty(Read("<Objects><PlayerStat id=\"Kills\" /></Objects>"));
    }

    private static FameBonusDefinition Single(string id) =>
        Bonuses.Single(bonus => bonus.Id == id);

    private static IReadOnlyList<FameBonusDefinition> Read(string xml) =>
        FameBonusXmlReader.ReadDocument(Encoding.UTF8.GetBytes(xml));

    private static IReadOnlyList<FameBonusDefinition> ReadFixture() =>
        FameBonusXmlReader.ReadDocument(
            File.ReadAllBytes(Path.Combine("Fixtures", "fame-bonuses.xml")));
}
