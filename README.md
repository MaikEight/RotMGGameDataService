# RotMG Game Data Service

RotMG Game Data Service is a .NET 8 service that extracts public game
metadata and item images from the current Realm of the Mad God client and makes
versioned results available through a read-only HTTP API.

The service is intentionally consumer-neutral. Exalt Account Manager can use
it, but its contracts should also be useful to other tools that need current
item definitions, player-stat definitions, fame bonuses, or item sprites.

> Status: extraction proof of concept. Live Realm discovery, bounded download,
> checksum verification, extraction, sprite rendering, and a Linux container
> build work. PostgreSQL publication and the public data API are not yet
> implemented.

## Goals

- Detect new Realm client builds without manual asset contributions.
- Download only the client file required by the extractor.
- Reuse `TadusPro/RotMGAssetExtractor` as a pinned C# dependency.
- Publish immutable, versioned game-data builds.
- Let unauthenticated clients report an observed Realm build hash so an update
  can be discovered quickly.
- Represent each item image as a content-addressed PNG instead of a shared
  sprite atlas.
- Track added, modified, removed, and unchanged records between builds.
- Run safely as a multi-instance ASP.NET API behind a load balancer.
- Run extraction as a single scheduled job in Docker or Kubernetes.
- Keep serving the last successful build when an update fails.

## Non-goals

- Launching or updating a user's local Realm installation.
- Trusting client-supplied hashes as proof of an update. Public update hints may
  queue a rate-limited check against Realm's official metadata, but only the
  official result can authorize a download and extraction.
- Acting as a game proxy or handling Realm account credentials.
- Reproducing client presentation details such as EAM-specific dungeon image
  paths.

## Target components

| Component | Responsibility |
| --- | --- |
| ASP.NET API | Serves build metadata, manifests, diffs, and immutable sprites. |
| Updater worker | Receives coalesced update hints, checks Realm periodically, and publishes confirmed builds. |
| PostgreSQL | Stores build metadata, normalized records, sprite bytes, and checksums. |
| CDN/load balancer | Caches immutable responses and absorbs public download traffic. |

The API and updater will be two execution modes of the same application and
Docker image. Kubernetes can run two or more API replicas and one lightweight
updater worker instance.

## Run the proof of concept

Initialize the pinned extractor dependency and run the offline tests:

```powershell
git submodule update --init --recursive
dotnet test RotMGGameDataService.sln
```

Run a live extraction on the host:

```powershell
dotnet run --project src/RotMGGameDataService -- refresh
```

Or run the same extraction in Linux. The named volume preserves the verified
source file so later probes do not redownload it:

```powershell
docker build --tag rotmg-game-data-service:dev .
docker run --rm --volume rotmg-game-data-probe:/data rotmg-game-data-service:dev refresh
```

This is a live smoke test: it downloads the current official Windows Realm
resource archive. The current archive is about 47 MB and expands to about
394 MB. See the [proof-of-concept results](docs/proof-of-concept.md) for the
measured output and current memory limitation.

## Documentation

- [Architecture](docs/architecture.md)
- [API contract](docs/api.md)
- [Development and testing](docs/development.md)
- [Readiness checklist](docs/readiness.md)
- [Proof-of-concept results](docs/proof-of-concept.md)

## Repository ownership

The production repository should ideally live with the organization that owns
and operates the deployment. Development can begin in this local repository
and move to the final GitHub organization after the proof of concept works.

## Project notice

This is an independent community project and is not affiliated with or
endorsed by DECA Games.
