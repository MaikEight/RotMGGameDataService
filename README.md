# RotMG Game Data Service

RotMG Game Data Service is a proposed .NET 8 service that extracts public game
metadata and item images from the current Realm of the Mad God client and makes
versioned results available through a read-only HTTP API.

The service is intentionally consumer-neutral. Exalt Account Manager can use
it, but its contracts should also be useful to other tools that need current
item definitions, player-stat definitions, fame bonuses, or item sprites.

> Status: architecture and API planning. The service has not been implemented
> yet, and the commands in the development guide describe the intended
> developer experience.

## Goals

- Detect new Realm client builds without manual asset contributions.
- Download only the client file required by the extractor.
- Reuse `TadusPro/RotMGAssetExtractor` as a pinned C# dependency.
- Publish immutable, versioned game-data builds.
- Represent each item image as a content-addressed PNG instead of a shared
  sprite atlas.
- Track added, modified, removed, and unchanged records between builds.
- Run safely as a multi-instance ASP.NET API behind a load balancer.
- Run extraction as a single scheduled job in Docker or Kubernetes.
- Keep serving the last successful build when an update fails.

## Non-goals

- Launching or updating a user's local Realm installation.
- Triggering an extraction from an unauthenticated public HTTP request.
- Acting as a game proxy or handling Realm account credentials.
- Reproducing client presentation details such as EAM-specific dungeon image
  paths.

## Planned components

| Component | Responsibility |
| --- | --- |
| ASP.NET API | Serves build metadata, manifests, diffs, and immutable sprites. |
| One-shot updater | Checks Realm, downloads `resources.assets.gz`, extracts data, and publishes a build. |
| PostgreSQL | Stores build metadata, normalized records, sprite bytes, and checksums. |
| CDN/load balancer | Caches immutable responses and absorbs public download traffic. |

The API and updater will be two execution modes of the same application and
Docker image. Kubernetes can run two or more API replicas and one scheduled
updater CronJob.

## Documentation

- [Architecture](docs/architecture.md)
- [API contract](docs/api.md)
- [Development and testing](docs/development.md)

## Repository ownership

The production repository should ideally live with the organization that owns
and operates the deployment. Development can begin in this local repository
and move to the final GitHub organization after the proof of concept works.

## Project notice

This is an independent community project and is not affiliated with or
endorsed by DECA Games.
