using System.Globalization;
using System.Xml.Linq;
using RotMGAssetExtractor.UnityExtractor.resextractor;

namespace RotMGGameDataService.Extraction;

/// <summary>
/// Reads fame-bonus definitions from the Realm client's XML text assets.
/// </summary>
/// <remarks>
/// <para>
/// The extractor's <c>FameBonus</c> model declares <c>Description</c> and
/// <c>ShortDisplayName</c> as <c>int</c>, but the client writes both as text:
/// </para>
/// <code>
/// &lt;FameBonus id="Undead ForestAdversary" code="548"&gt;
///   &lt;AbsoluteBonus&gt;100&lt;/AbsoluteBonus&gt;
///   &lt;Description&gt;Kill 1000 Undead Forest Enemies&lt;/Description&gt;
///   &lt;ShortDisplayName&gt;Adversary&lt;/ShortDisplayName&gt;
///   &lt;Condition stat="Undead Forest" threshold="100"&gt;StatValue&lt;/Condition&gt;
/// &lt;/FameBonus&gt;
/// </code>
/// <para>
/// The mapper parses those as numbers, so both land as zero and the text is
/// gone by the time the model is built. Reading the XML is the only way to
/// recover them, and once it is being read the whole entry comes from there
/// rather than being merged across two sources.
/// </para>
/// <para>
/// Reading it also drops the stray whitespace the client ships in a few names.
/// Two ids arrive as <c>id="PotionDrinker&amp;#xD;"</c> and
/// <c>id="CritterFoe&amp;#xD;"</c>, and passing a trailing carriage return
/// through to the API breaks any consumer that keys on the id.
/// </para>
/// <para>
/// Every other field this produces is identical to what the extractor's model
/// yields; that was checked against the published manifest for build
/// <c>974bde45c06b313b1e425bc2cb222c75</c>, which matched on all 612 bonuses
/// and all 817 conditions.
/// </para>
/// </remarks>
public static class FameBonusXmlReader
{
    private const string FameBonusElementName = "FameBonus";

    /// <summary>
    /// Parses every fame-bonus definition found in the client's text assets.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ClassIDType.TextAsset"/> objects are materialized, so
    /// this pass skips the texture and atlas decoding that dominates a full
    /// extraction. Call it before the extractor loads the same file so the two
    /// copies of the asset bytes do not overlap in memory.
    /// </remarks>
    public static async Task<IReadOnlyList<FameBonusDefinition>> ReadAsync(
        string resourcesAssetsPath,
        CancellationToken cancellationToken)
    {
        var assetBytes = await File.ReadAllBytesAsync(resourcesAssetsPath, cancellationToken);
        var header = new FileHeader(assetBytes);
        if (header.Type != FileHeader.AssetsFile)
            return [];

        var serializedFile = new SerializedFile(assetBytes, header);
        var definitions = new List<FameBonusDefinition>();
        foreach (var entry in serializedFile.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type != ClassIDType.TextAsset)
                continue;

            var textAsset = new TextAsset(entry);
            if (TextAsset.NonXmlFiles.Contains(textAsset.Name))
                continue;

            definitions.AddRange(ReadDocument(textAsset.Script));
        }

        return definitions;
    }

    /// <summary>
    /// Parses the fame bonuses in a single XML document, ignoring any text
    /// asset that is not XML or holds no fame bonuses.
    /// </summary>
    public static IReadOnlyList<FameBonusDefinition> ReadDocument(byte[] xmlBytes)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(xmlBytes);
            document = XDocument.Load(stream);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        if (document.Root is null)
            return [];

        return document.Root
            .Elements()
            .Where(IsFameBonus)
            .Select(ReadBonus)
            .ToArray();
    }

    /// <summary>
    /// Matches the extractor's own dispatch, which prefers an explicit
    /// <c>Class</c> child over the element name.
    /// </summary>
    private static bool IsFameBonus(XElement element)
    {
        var declaredClass = element.Element("Class")?.Value?.Trim();
        var typeName = string.IsNullOrEmpty(declaredClass)
            ? element.Name.LocalName
            : declaredClass;
        return string.Equals(typeName, FameBonusElementName, StringComparison.OrdinalIgnoreCase);
    }

    private static FameBonusDefinition ReadBonus(XElement element) => new(
        ReadString(element, "id") ?? string.Empty,
        ReadInt(element, "code"),
        ReadString(element, "DisplayGroup"),
        ReadString(element, "DisplayCategory"),
        ReadString(element, "DisplayName"),
        ReadString(element, "ShortDisplayName"),
        ReadString(element, "Description"),
        ReadInt(element, "AbsoluteBonus"),
        ReadFloat(element, "RelativeBonus"),
        ReadInt(element, "MaxRepeatCount"),
        ReadBool(element, "Repeatable"),
        element.Elements("Condition").Select(ReadCondition).ToArray());

    private static FameConditionDefinition ReadCondition(XElement element) => new(
        // The condition's type is the element's own text, not a named field.
        NullIfEmpty(element.Value) ?? string.Empty,
        ReadInt(element, "threshold"),
        ReadString(element, "stat"));

    /// <summary>
    /// Reads a value written either as an attribute or as a child element,
    /// preferring the attribute exactly as the extractor's mapper does. The
    /// client uses attributes for <c>id</c>, <c>code</c>, and a condition's
    /// fields, and child elements for everything else.
    /// </summary>
    private static string? ReadRaw(XElement element, string name)
    {
        var attribute = element
            .Attributes()
            .FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is not null)
            return attribute.Value;

        return element
            .Elements()
            .FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string? ReadString(XElement element, string name) =>
        NullIfEmpty(ReadRaw(element, name));

    private static int ReadInt(XElement element, string name)
    {
        var raw = ReadRaw(element, name);
        if (raw is null)
            return 0;

        // Hexadecimal appears in the client's numeric fields, so it is read the
        // way the extractor reads it, but a malformed value falls back to zero
        // rather than failing the whole build.
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                raw.AsSpan(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var hexValue)
                ? hexValue
                : 0;
        }

        return int.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
    }

    private static float ReadFloat(XElement element, string name) =>
        float.TryParse(
            ReadRaw(element, name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0f;

    /// <summary>
    /// Applies the client's convention that a present but empty element means
    /// true, matching how the extractor reads every other boolean.
    /// </summary>
    private static bool ReadBool(XElement element, string name)
    {
        var raw = ReadRaw(element, name);
        if (raw is null)
            return false;
        return string.IsNullOrWhiteSpace(raw) || !IsExplicitFalse(raw);
    }

    private static bool IsExplicitFalse(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("0", StringComparison.Ordinal)
            || trimmed.Equals("no", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A fame bonus exactly as the client's XML declares it.</summary>
public sealed record FameBonusDefinition(
    string Id,
    int Code,
    string? DisplayGroup,
    string? DisplayCategory,
    string? DisplayName,
    string? ShortDisplayName,
    string? Description,
    int AbsoluteBonus,
    float RelativeBonus,
    int MaxRepeatCount,
    bool Repeatable,
    IReadOnlyList<FameConditionDefinition> Conditions);

/// <summary>
/// One requirement of a fame bonus. <paramref name="Type"/> names how it is
/// tested, such as <c>StatValue</c>, <c>MaxedStat</c>, or <c>FirstCharacter</c>.
/// </summary>
public sealed record FameConditionDefinition(string Type, int Threshold, string? Stat);
