namespace RotMGGameDataService.Realm;

public sealed record RealmBuildInfo(
    string BuildId,
    string BuildHash,
    Uri BuildCdn,
    Uri ChecksumUri,
    RealmResourceFile Resource);

public sealed record RealmResourceFile(
    string Path,
    string Checksum,
    long CompressedSize,
    Uri DownloadUri);

internal sealed record RealmAppInit(
    string BuildId,
    string BuildHash,
    Uri BuildCdn,
    Uri ChecksumUri);
