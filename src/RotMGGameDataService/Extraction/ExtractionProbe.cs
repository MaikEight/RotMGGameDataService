using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetImageBuffer = RotMGAssetExtractor.Flatc.ImageBuffer;
using RealmObject = RotMGAssetExtractor.Model.Object;
using RotMGAssetExtractor.Model;
using RotMGAssetExtractor.ModelHelpers;
using RotMGGameDataService.Data;
using RotMGGameDataService.Realm;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RotMGGameDataService.Extraction;

public sealed class ExtractionProbe(
    ILogger<ExtractionProbe> logger,
    BuildIdentity buildIdentity)
{
    private const int TileSize = 40;
    private const int IconSize = 32;
    private const int IconCanvasSize = IconSize + 2;

    private static readonly string[] RenderableModelTypes =
    [
        "Equipment",
        "Skin",
        "PetSkin",
        "PetAbility",
        "Dye",
        "Emote",
        "Entrance",
    ];

    public async Task<ExtractionResult> RunAsync(
        string resourcesAssetsPath,
        RealmBuildInfo build,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var generatedAt = DateTimeOffset.UtcNow;
        var buildId = buildIdentity.Calculate(build.Resource.Checksum);
        logger.LogInformation("Loading Realm resources from {ResourcesPath}", resourcesAssetsPath);
        await global::RotMGAssetExtractor.RotMGAssetExtractor.LoadLocalResourcesAsync(
            resourcesAssetsPath);

        var categoryCounts = global::RotMGAssetExtractor.RotMGAssetExtractor.BuildModelsByType
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Count, StringComparer.Ordinal);
        var totalModelCount = categoryCounts.Values.Sum();

        var outputDirectory = Path.GetFullPath(Path.Combine(
            workDirectory,
            "probes",
            build.Resource.Checksum));
        var spriteDirectory = Path.Combine(outputDirectory, "sprites");
        Directory.CreateDirectory(spriteDirectory);

        var objectsById = new Dictionary<int, RealmObject>();
        var duplicateObjectIdCount = 0;
        foreach (var modelType in RenderableModelTypes)
        {
            if (!global::RotMGAssetExtractor.RotMGAssetExtractor.BuildModelsByType.TryGetValue(
                    modelType,
                    out var models))
            {
                continue;
            }

            foreach (var model in models.OfType<RealmObject>())
            {
                if (model.type <= 0)
                    continue;
                if (!objectsById.TryAdd(model.type, model))
                    duplicateObjectIdCount++;
            }
        }

        var visualHashes = new HashSet<string>(StringComparer.Ordinal);
        var pngHashes = new HashSet<string>(StringComparer.Ordinal);
        var sprites = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var objectRecords = new SortedDictionary<string, GameObjectRecord>(StringComparer.Ordinal);
        var visualCatalogEntries = new List<string>(objectsById.Count);
        var pngCatalogEntries = new List<string>(objectsById.Count);
        long uniquePngBytes = 0;
        var missingTextureCount = 0;
        var failedImageCount = 0;
        var processedCount = 0;

        foreach (var (objectId, model) in objectsById.OrderBy(entry => entry.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var texture = GetTexture(model);
            if (texture is null)
            {
                missingTextureCount++;
                continue;
            }

            using var source = AssetImageBuffer.GetImage(texture, objectId);
            if (source is null || source.Width <= 0 || source.Height <= 0)
            {
                failedImageCount++;
                continue;
            }

            using var tile = BuildTile(source);
            var visualHash = CalculatePixelHash(tile);
            await using var pngStream = new MemoryStream();
            await tile.SaveAsPngAsync(pngStream, cancellationToken);
            var pngBytes = pngStream.ToArray();
            var pngHash = Convert.ToHexString(SHA256.HashData(pngBytes)).ToLowerInvariant();

            visualHashes.Add(visualHash);
            visualCatalogEntries.Add($"{objectId}:{visualHash}");
            pngCatalogEntries.Add($"{objectId}:{pngHash}");
            objectRecords[objectId.ToString(CultureInfo.InvariantCulture)] =
                CreateObjectRecord(model, objectId, pngHash);

            if (pngHashes.Add(pngHash))
            {
                uniquePngBytes += pngBytes.Length;
                sprites[pngHash] = pngBytes;
                var hashDirectory = Path.Combine(spriteDirectory, pngHash[..2]);
                Directory.CreateDirectory(hashDirectory);
                var spritePath = Path.Combine(hashDirectory, pngHash + ".png");
                if (!File.Exists(spritePath))
                    await File.WriteAllBytesAsync(spritePath, pngBytes, cancellationToken);
            }

            processedCount++;
            if (processedCount % 1_000 == 0)
            {
                logger.LogInformation(
                    "Generated {ProcessedCount} of {ObjectCount} renderable objects",
                    processedCount,
                    objectsById.Count);
            }
        }

        var playerStats = CreatePlayerStats();
        var fameBonuses = CreateFameBonuses();
        var manifest = new GameDataManifest(
            buildIdentity.SchemaVersion,
            buildId,
            build.BuildHash,
            build.Resource.Checksum,
            generatedAt,
            objectRecords,
            playerStats,
            fameBonuses,
            GameDataJson.HashJson(playerStats),
            GameDataJson.HashJson(fameBonuses));

        stopwatch.Stop();
        var report = new ExtractionProbeReport(
            build.BuildHash,
            build.Resource.Checksum,
            generatedAt,
            stopwatch.Elapsed,
            Process.GetCurrentProcess().PeakWorkingSet64,
            categoryCounts,
            totalModelCount,
            processedCount,
            duplicateObjectIdCount,
            missingTextureCount,
            failedImageCount,
            visualHashes.Count,
            pngHashes.Count,
            uniquePngBytes,
            CalculateCatalogHash(visualCatalogEntries),
            CalculateCatalogHash(pngCatalogEntries),
            outputDirectory);

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "probe-report.json"),
            JsonSerializer.Serialize(report, serializerOptions),
            cancellationToken);

        logger.LogInformation(
            "Extraction probe produced {RenderableCount} objects and {UniqueSpriteCount} unique PNGs in {Duration}",
            report.RenderableObjectCount,
            report.UniquePngCount,
            report.Duration);
        return new ExtractionResult(manifest, sprites, report);
    }

    private static GameObjectRecord CreateObjectRecord(
        RealmObject model,
        int objectId,
        string spriteHash)
    {
        var equipment = model as Equipment;
        var equipmentData = equipment is null
            ? null
            : new EquipmentData(
                equipment.SlotType,
                equipment.BagType,
                equipment.feedPower,
                equipment.Tier,
                equipment.ItemTier,
                equipment.PowerLevel,
                NullIfEmpty(equipment.Rarity),
                equipment.Soulbound,
                equipment.Consumable,
                equipment.DropTradable,
                equipment.Usable,
                equipment.MpCost,
                equipment.Cooldown,
                equipment.SeasonalOnly,
                equipment.EnchantmentSlots,
                equipment.Labels?.Contains("shiny", StringComparison.OrdinalIgnoreCase) == true);

        var record = new GameObjectRecord(
            objectId,
            model.id ?? string.Empty,
            model.GetType().Name,
            NullIfEmpty(model.Class),
            NullIfEmpty(GetDisplayName(model)),
            equipmentData,
            spriteHash,
            string.Empty);
        return record with { MetadataHash = GameDataJson.HashJson(record) };
    }

    private static SortedDictionary<string, PlayerStatRecord> CreatePlayerStats()
    {
        var records = new SortedDictionary<string, PlayerStatRecord>(StringComparer.Ordinal);
        if (!global::RotMGAssetExtractor.RotMGAssetExtractor.BuildModelsByType.TryGetValue(
                "PlayerStat",
                out var models))
        {
            return records;
        }

        foreach (var stat in models.OfType<PlayerStat>().OrderBy(value => value.index))
        {
            var record = new PlayerStatRecord(
                stat.index,
                stat.id ?? string.Empty,
                stat.reportEvery,
                stat.dungeon,
                NullIfEmpty(stat.displayName),
                NullIfEmpty(stat.displayColor),
                stat.displayOnDeath,
                NullIfEmpty(stat.dungeonId),
                string.Empty);
            record = record with { MetadataHash = GameDataJson.HashJson(record) };
            records.TryAdd(stat.index.ToString(CultureInfo.InvariantCulture), record);
        }

        return records;
    }

    private static IReadOnlyList<FameBonusRecord> CreateFameBonuses()
    {
        if (!global::RotMGAssetExtractor.RotMGAssetExtractor.BuildModelsByType.TryGetValue(
                "FameBonus",
                out var models))
        {
            return [];
        }

        var records = new List<FameBonusRecord>();
        foreach (var bonus in models
                     .OfType<FameBonus>()
                     .OrderBy(value => value.code)
                     .ThenBy(value => value.id, StringComparer.Ordinal))
        {
            var conditions = (bonus.Condition ?? [])
                .Select(condition => new FameConditionRecord(
                    condition.threshold,
                    NullIfEmpty(condition.stat),
                    NullIfEmpty(condition.Value)))
                .ToArray();
            var record = new FameBonusRecord(
                bonus.id ?? string.Empty,
                bonus.code,
                NullIfEmpty(bonus.DisplayGroup),
                NullIfEmpty(bonus.DisplayCategory),
                NullIfEmpty(bonus.DisplayName),
                bonus.AbsoluteBonus,
                bonus.RelativeBonus,
                bonus.MaxRepeatCount,
                bonus.Repeatable,
                conditions,
                string.Empty);
            records.Add(record with { MetadataHash = GameDataJson.HashJson(record) });
        }

        return records;
    }

    private static string? GetDisplayName(RealmObject model)
    {
        var property = model.GetType().GetProperty("DisplayId");
        return property?.GetValue(model) as string;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static ITexture? GetTexture(RealmObject model) => model switch
    {
        Equipment equipment =>
            (ITexture?)equipment.AnimatedTexture
            ?? (ITexture?)model.AnimatedTexture
            ?? (ITexture?)equipment.Texture,
        Skin skin =>
            (ITexture?)skin.AnimatedTexture
            ?? (ITexture?)model.AnimatedTexture
            ?? (ITexture?)skin.Texture,
        _ => (ITexture?)model.AnimatedTexture ?? model.Texture,
    };

    private static Image<Rgba32> BuildTile(Image<Rgba32> source)
    {
        using var resized = source.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(IconSize, IconSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.NearestNeighbor,
        }));
        using var iconCanvas = new Image<Rgba32>(IconCanvasSize, IconCanvasSize);
        iconCanvas.Mutate(context =>
        {
            context.Clear(Color.Transparent);
            context.DrawImage(resized, new Point(1, 1), 1f);
        });
        using var edge = CreateEdgeOutline(iconCanvas);

        var tile = new Image<Rgba32>(TileSize, TileSize);
        var offset = new Point(
            (TileSize - IconCanvasSize) / 2,
            (TileSize - IconCanvasSize) / 2);
        tile.Mutate(context =>
        {
            context.Clear(Color.Transparent);
            context.DrawImage(iconCanvas, offset, 1f);
            context.DrawImage(edge, offset, 1f);
        });
        return tile;
    }

    private static Image<Rgba32> CreateEdgeOutline(Image<Rgba32> icon)
    {
        var outline = new Image<Rgba32>(icon.Width, icon.Height);
        outline.Mutate(context => context.Clear(Color.Transparent));

        for (var y = 0; y < icon.Height; y++)
        {
            for (var x = 0; x < icon.Width; x++)
            {
                if (icon.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span[x].A == 0)
                    continue;

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var neighborX = x + offsetX;
                        var neighborY = y + offsetY;
                        if (neighborX < 0
                            || neighborY < 0
                            || neighborX >= icon.Width
                            || neighborY >= icon.Height)
                        {
                            continue;
                        }

                        if (icon.Frames.RootFrame
                                .DangerousGetPixelRowMemory(neighborY)
                                .Span[neighborX]
                                .A == 0)
                        {
                            outline.Frames.RootFrame
                                .DangerousGetPixelRowMemory(neighborY)
                                .Span[neighborX] = new Rgba32(0, 0, 0, 255);
                        }
                    }
                }
            }
        }

        return outline;
    }

    private static string CalculatePixelHash(Image<Rgba32> image)
    {
        var pixelBytes = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixelBytes);
        return Convert.ToHexString(SHA256.HashData(pixelBytes)).ToLowerInvariant();
    }

    private static string CalculateCatalogHash(IEnumerable<string> entries)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', entries));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
