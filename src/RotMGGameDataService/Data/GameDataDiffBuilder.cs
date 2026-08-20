namespace RotMGGameDataService.Data;

public static class GameDataDiffBuilder
{
    public static GameDataDiff Create(GameDataManifest from, GameDataManifest to)
    {
        var added = new SortedDictionary<string, GameObjectRecord>(StringComparer.Ordinal);
        var modified = new SortedDictionary<string, GameObjectRecord>(StringComparer.Ordinal);
        var removed = new List<int>();

        foreach (var (id, current) in to.Objects)
        {
            if (!from.Objects.TryGetValue(id, out var previous))
                added[id] = current;
            else if (!string.Equals(previous.MetadataHash, current.MetadataHash, StringComparison.Ordinal))
                modified[id] = current;
        }

        foreach (var (id, previous) in from.Objects)
        {
            if (!to.Objects.ContainsKey(id))
                removed.Add(previous.Id);
        }

        removed.Sort();
        return new GameDataDiff(
            to.SchemaVersion,
            from.BuildId,
            to.BuildId,
            to.RealmBuildHash,
            to.SourceChecksum,
            to.GeneratedAt,
            to.PlayerStatsHash,
            to.FameBonusesHash,
            added,
            modified,
            removed,
            string.Equals(from.PlayerStatsHash, to.PlayerStatsHash, StringComparison.Ordinal)
                ? null
                : to.PlayerStats,
            string.Equals(from.FameBonusesHash, to.FameBonusesHash, StringComparison.Ordinal)
                ? null
                : to.FameBonuses);
    }
}
