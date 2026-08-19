# API Contract

This document defines the planned version 1 HTTP contract. It may change while
the proof of concept is being implemented, but breaking changes must be
reflected in the URL version or schema version before production use.

## Conventions

- Base path: `/api/v1`
- JSON uses camel-case property names.
- Build identifiers and SHA-256 values are lowercase hexadecimal strings.
- Dates use UTC ISO 8601 timestamps.
- Item IDs are represented as decimal strings when used as JSON object keys.
- Errors use ASP.NET `ProblemDetails` responses.
- Immutable responses support `ETag` and `If-None-Match`.

## Get the latest build

```http
GET /api/v1/builds/latest
```

Example response:

```json
{
  "schemaVersion": 1,
  "buildHash": "aeceb1f212da9dcaba9e6c41a34c9d69",
  "sourceChecksum": "c32dd849645d89ff0841eabfd8c17a67",
  "generatedAt": "2026-08-19T10:00:00Z",
  "manifestUrl": "/api/v1/builds/aeceb1f212da9dcaba9e6c41a34c9d69/manifest",
  "manifestSha256": "eaa6b80000000000000000000000000000000000000000000000000000000000"
}
```

Clients should cache this response, send `If-None-Match` on later checks, and
avoid checking repeatedly from different application components.

Suggested response caching:

```http
Cache-Control: public, max-age=300, stale-while-revalidate=300
```

The endpoint returns `503 Service Unavailable` when no successful build has
ever been published. A failed refresh does not cause `503` when an older
successful build exists.

## Get a complete manifest

```http
GET /api/v1/builds/{buildHash}/manifest
```

Example structure:

```json
{
  "schemaVersion": 1,
  "buildHash": "aeceb1f212da9dcaba9e6c41a34c9d69",
  "generatedAt": "2026-08-19T10:00:00Z",
  "items": {
    "1234": {
      "id": 1234,
      "name": "Example Item",
      "slotType": 1,
      "tier": 12,
      "feedPower": 450,
      "bagType": 4,
      "soulbound": false,
      "rarity": "tiered",
      "isShiny": false,
      "spriteHash": "a8f2930000000000000000000000000000000000000000000000000000000000",
      "spriteUrl": "/api/v1/sprites/a8f2930000000000000000000000000000000000000000000000000000000000.png"
    }
  },
  "playerStats": {
    "13": {
      "index": 13,
      "id": "PirateCavesCompleted",
      "displayName": "Pirate Cave",
      "isDungeon": true,
      "dungeonId": "PirateCave"
    }
  },
  "fameBonuses": [
    {
      "id": "TunnelRat",
      "code": 0,
      "displayGroup": "Dungeon Bonuses",
      "displayCategory": "Dungeon Collection",
      "displayName": "Tunnel Rat",
      "absoluteBonus": 3000,
      "relativeBonus": 7.5,
      "maxRepeatCount": 0,
      "repeatable": false,
      "conditions": []
    }
  ]
}
```

The complete manifest is intended for a fresh installation, cache recovery, or
a client that does not implement diffs. HTTP compression should be enabled;
JSON metadata is expected to compress well.

The response is immutable for the lifetime of the retained build:

```http
Cache-Control: public, max-age=31536000, immutable
ETag: "<manifest-sha256>"
```

## Get a build diff

```http
GET /api/v1/builds/{toBuildHash}/diff?from={fromBuildHash}
```

Example response:

```json
{
  "schemaVersion": 1,
  "fromBuild": "old-build-hash",
  "toBuild": "new-build-hash",
  "addedItems": {
    "1234": {
      "id": 1234,
      "name": "New Item",
      "spriteHash": "new-sprite-hash",
      "spriteUrl": "/api/v1/sprites/new-sprite-hash.png"
    }
  },
  "modifiedItems": {},
  "removedItemIds": [991],
  "playerStats": null,
  "fameBonuses": null
}
```

`playerStats` and `fameBonuses` are `null` when unchanged. When either section
changes, the response contains its complete replacement rather than attempting
to patch deeply nested records in the first API version.

Clients must download and verify all newly referenced sprites before replacing
their locally active manifest. If a delta is unavailable because the source
build has expired, the API returns `404 Not Found` and the client downloads the
complete target manifest.

## Get a sprite

```http
GET /api/v1/sprites/{spriteHash}.png
```

The response body is the exact PNG whose SHA-256 is used in the URL. Sprites
are final transparent item tiles, ready to render without atlas coordinates or
additional image processing.

```http
Content-Type: image/png
Cache-Control: public, max-age=31536000, immutable
ETag: "<sprite-sha256>"
```

An unchanged item retains the same sprite URL across builds. Two items may
reference the same URL.

## Health endpoints

```http
GET /health/live
GET /health/ready
```

- `live` indicates that the process is responsive and does not query external
  dependencies.
- `ready` verifies that the API can reach PostgreSQL and serve requests.

Health endpoints are intended for Docker and Kubernetes probes and are not
rate limited.

## Rate-limit behavior

The CDN or ingress owns the primary cluster-wide limits. ASP.NET adds a
per-instance safety policy and returns:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 30
```

Metadata routes may use a conventional per-IP token bucket. Sprite routes must
permit large legitimate bursts and should rely more heavily on CDN caching and
concurrency limits than a low requests-per-minute limit.

No public endpoint starts or forces a refresh.
