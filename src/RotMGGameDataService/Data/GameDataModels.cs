using System.Text.Json.Serialization;

namespace RotMGGameDataService.Data;

public sealed record EquipmentData(
    int SlotType,
    int BagType,
    int FeedPower,
    int Tier,
    int ItemTier,
    int PowerLevel,
    string? Rarity,
    bool Soulbound,
    bool Consumable,
    bool DropTradable,
    bool Usable,
    int MpCost,
    float Cooldown,
    bool SeasonalOnly,
    bool EnchantmentSlots,
    bool Shiny);

public sealed record GameObjectRecord(
    int Id,
    string InternalName,
    string Kind,
    string? Class,
    string? DisplayName,
    EquipmentData? Equipment,
    string SpriteHash,
    string MetadataHash);

public sealed record PlayerStatRecord(
    int Index,
    string Id,
    int ReportEvery,
    bool Dungeon,
    string? DisplayName,
    string? DisplayColor,
    bool DisplayOnDeath,
    string? DungeonId,
    string MetadataHash);

public sealed record FameConditionRecord(
    int Threshold,
    string? Stat,
    string? Value);

public sealed record FameBonusRecord(
    string Id,
    int Code,
    string? DisplayGroup,
    string? DisplayCategory,
    string? DisplayName,
    int AbsoluteBonus,
    float RelativeBonus,
    int MaxRepeatCount,
    bool Repeatable,
    IReadOnlyList<FameConditionRecord> Conditions,
    string MetadataHash);

public sealed record GameDataManifest(
    int SchemaVersion,
    string BuildId,
    string RealmBuildHash,
    string SourceChecksum,
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, GameObjectRecord> Objects,
    IReadOnlyDictionary<string, PlayerStatRecord> PlayerStats,
    IReadOnlyList<FameBonusRecord> FameBonuses,
    string PlayerStatsHash,
    string FameBonusesHash);

public sealed record GameDataDiff(
    int SchemaVersion,
    string FromBuildId,
    string ToBuildId,
    string RealmBuildHash,
    string SourceChecksum,
    DateTimeOffset GeneratedAt,
    string PlayerStatsHash,
    string FameBonusesHash,
    IReadOnlyDictionary<string, GameObjectRecord> AddedObjects,
    IReadOnlyDictionary<string, GameObjectRecord> ModifiedObjects,
    IReadOnlyList<int> RemovedObjectIds,
    IReadOnlyDictionary<string, PlayerStatRecord>? PlayerStats,
    IReadOnlyList<FameBonusRecord>? FameBonuses);

public sealed record PublishedBuild(
    string BuildId,
    string RealmBuildHash,
    string SourceChecksum,
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string ManifestHash);

public sealed record StoredPayload(byte[] Bytes, string Hash);

/// <summary>
/// A resolved sprite-bundle request. <paramref name="FromBuildId"/> is null for
/// a complete bundle and set when only the sprites added since that build are
/// wanted.
/// </summary>
public sealed record SpriteBundle(string ToBuildId, string? FromBuildId, string ETag);

public sealed record RefreshStatus(
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastOfficialCheckAt,
    DateTimeOffset? PendingHintAt,
    DateTimeOffset? LastErrorAt,
    string? LastError);

public sealed record HintResult(bool CheckQueued, bool AlreadyCurrent);

public sealed record UpdateHintRequest(
    [property: JsonPropertyName("observedBuildHash")] string ObservedBuildHash);
