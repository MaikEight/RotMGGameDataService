using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetImageBuffer = RotMGAssetExtractor.Flatc.ImageBuffer;
using RealmObject = RotMGAssetExtractor.Model.Object;
using RotMGAssetExtractor.Model;
using RotMGAssetExtractor.ModelHelpers;
using RotMGGameDataService.Realm;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RotMGGameDataService.Extraction;

public sealed class ExtractionProbe(ILogger<ExtractionProbe> logger)
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

    public async Task<ExtractionProbeReport> RunAsync(
        string resourcesAssetsPath,
        RealmBuildInfo build,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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

            if (pngHashes.Add(pngHash))
            {
                uniquePngBytes += pngBytes.Length;
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

        stopwatch.Stop();
        var report = new ExtractionProbeReport(
            build.BuildHash,
            build.Resource.Checksum,
            DateTimeOffset.UtcNow,
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
        return report;
    }

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
