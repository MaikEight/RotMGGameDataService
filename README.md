# RotMG Game Data Service

RotMG Game Data Service is a .NET 8 service that downloads the current public
Realm of the Mad God client data, extracts game metadata and rendered sprites,
and publishes immutable, versioned results through an HTTP API.

The service is consumer-neutral. Exalt Account Manager can use it, but the API
also supports other tools that need current object definitions, player-stat
definitions, fame bonuses, or sprites.

> Status: operational implementation. The repository includes live extraction,
> transactional PostgreSQL publication, two API replicas behind a hardened
> gateway, an update worker, and daily retained backups.

## What it provides

- Official Realm build discovery and bounded, checksum-verified downloads.
- A pinned C# `RotMGAssetExtractor` dependency.
- Neutral game objects, equipment metadata, player stats, and fame bonuses.
- Final 40x40 transparent PNG sprites addressed by SHA-256.
- Immutable manifests and diffs with `ETag` and compression support.
- Safe anonymous update hints that can trigger only a rate-limited official
  metadata check, never a caller-selected download.
- Atomic publication, advisory-lock coordination, and last-good-build fallback.
- A Compose stack with PostgreSQL, two API replicas, an updater worker, an
  unprivileged NGINX gateway, and daily database backups.

## Quick start

Prerequisites are Git, Docker, and Docker Compose. The .NET 8 SDK is optional
unless tests are run directly on the host.

```powershell
git submodule update --init --recursive
Copy-Item .env.example .env
```

Set a long random `POSTGRES_PASSWORD` in `.env`. For local development, set
`DATA_ROOT=./.data` and keep `BIND_ADDRESS=127.0.0.1`. Then start the stack and
publish the current Realm build:

```powershell
docker compose up --build --detach
docker compose run --rm --no-deps worker refresh
Invoke-RestMethod http://127.0.0.1:8090/api/v1/builds/latest
```

The long-running worker checks immediately when no build exists, every six
hours thereafter, and after a coalesced update hint. Re-running `refresh` is
idempotent when the official build is unchanged.

Run the offline test suite with:

```powershell
dotnet test RotMGGameDataService.sln
```

## API overview

| Route | Purpose |
| --- | --- |
| `GET /api/v1/builds/latest` | Current published build metadata. |
| `GET /api/v1/builds/{id}/manifest` | Complete immutable game-data manifest. |
| `GET /api/v1/builds/{to}/diff?from={from}` | Immutable diff between retained builds. |
| `GET /api/v1/sprites/{sha256}.png` | Content-addressed rendered sprite. |
| `POST /api/v1/update-hints` | Queue a bounded official update check. |
| `GET /api/v1/status` | Publication and updater status. |
| `GET /info` | Service name, version, author, and last restart. |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL-backed readiness. |

See the [API contract](docs/api.md) for request and response details.

## Documentation

- [Architecture](docs/architecture.md)
- [API contract](docs/api.md)
- [Development and testing](docs/development.md)
- [Operations](docs/operations.md)
- [Readiness and validation](docs/readiness.md)
- [Extraction proof results](docs/proof-of-concept.md)

## Security boundary

The Compose gateway defaults to loopback and plain HTTP. Put it behind an
existing TLS ingress or CDN for public use; do not expose PostgreSQL. Public
hints are untrusted signals only. Upstream URLs remain constrained to the
configured HTTPS Realm endpoint and allow-listed CDN hosts.

## Project notice

This is an independent community project and is not affiliated with or
endorsed by DECA Games.
