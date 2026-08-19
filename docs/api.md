# API Contract

This document describes the implemented version 1 HTTP contract.

## Conventions

- Base path: `/api/v1`
- JSON property names use camel case.
- Realm build hashes and source checksums are 32 lowercase hexadecimal
  characters. Service build IDs and SHA-256 values are 64 characters.
- Dates are UTC ISO 8601 timestamps.
- Object IDs and player-stat indexes are decimal strings when used as JSON
  object keys.
- Errors use ASP.NET `ProblemDetails` or validation-problem responses.
- Immutable resources support `ETag` and `If-None-Match`.
- JSON responses support Brotli and gzip response compression.

## Get the latest build

```http
GET /api/v1/builds/latest
```

```json
{
  "schemaVersion": 1,
  "buildId": "5f7b6e64160ce740ec0b7c9efb8b0b71a21a0c80e76ec46cf1fd6f8ca27b84a9",
  "realmBuildHash": "aeceb1f212da9dcaba9e6c41a34c9d69",
  "sourceChecksum": "c32dd849645d89ff0841eabfd8c17a67",
  "generatedAt": "2026-08-19T10:00:00Z",
  "manifestUrl": "/api/v1/builds/5f7b6e64160ce740ec0b7c9efb8b0b71a21a0c80e76ec46cf1fd6f8ca27b84a9/manifest",
  "manifestSha256": "728597600cce39011fb752a0f899f05c07e1a2db696d190ccb57e060d2e16588"
}
```

The service build ID identifies the complete output contract. It includes the
Realm source checksum, schema version, extractor version, and renderer version,
so a renderer or mapping change can publish a new immutable build even when the
Realm build has not changed.

The response uses a five-minute cache lifetime and an ETag. It returns `503`
only before the first successful publication; a failed later refresh leaves the
last successful build available.

## Submit an update hint

```http
POST /api/v1/update-hints
Content-Type: application/json

{
  "observedBuildHash": "b14d91945492e348d572f7ae72f273cd"
}
```

Successful validation returns immediately:

```http
HTTP/1.1 202 Accepted
```

```json
{
  "status": "accepted",
  "checkQueued": true,
  "alreadyCurrent": false
}
```

The value is an untrusted signal. It is shape-validated, coalesced in
PostgreSQL, and may wake the worker. Only Realm's configured official metadata
endpoint can authorize a download. Callers cannot supply an upstream URL,
filesystem path, or checksum.

`checkQueued` may be false when the hash is already current, an equivalent
hint is pending, or the cluster-wide official-check cooldown applies. Clients
should return to the normal `latest` polling flow and must not wait on this
request.

Unknown JSON properties are rejected. The request body limit is 16 KiB and the
endpoint requires `application/json`.

## Get a complete manifest

Both the 64-character service build ID and its 32-character Realm build hash
can identify a retained build:

```http
GET /api/v1/builds/{buildIdentifier}/manifest
```

The manifest structure is:

```json
{
  "schemaVersion": 1,
  "buildId": "service-build-sha256",
  "realmBuildHash": "realm-build-md5",
  "sourceChecksum": "resources-assets-md5",
  "generatedAt": "2026-08-19T10:00:00Z",
  "objects": {
    "10022": {
      "id": 10022,
      "internalName": "Sword Rune",
      "kind": "Equipment",
      "class": "Equipment",
      "displayName": null,
      "equipment": {
        "slotType": 10,
        "bagType": 4,
        "feedPower": 750,
        "tier": 0,
        "itemTier": 0,
        "powerLevel": 0,
        "rarity": null,
        "soulbound": true,
        "consumable": true,
        "dropTradable": false,
        "usable": false,
        "mpCost": 0,
        "cooldown": 0,
        "seasonalOnly": false,
        "enchantmentSlots": false,
        "shiny": false
      },
      "spriteHash": "sprite-sha256",
      "metadataHash": "metadata-sha256"
    }
  },
  "playerStats": {
    "13": {
      "index": 13,
      "id": "PirateCavesCompleted",
      "reportEvery": 1,
      "dungeon": true,
      "displayName": "Pirate Cave",
      "displayColor": null,
      "displayOnDeath": true,
      "dungeonId": "PirateCave",
      "metadataHash": "metadata-sha256"
    }
  },
  "fameBonuses": [],
  "playerStatsHash": "section-sha256",
  "fameBonusesHash": "section-sha256"
}
```

`objects` is deliberately neutral: the Realm client contains renderable
equipment, tokens, portals, characters, and other object categories. A
consumer decides which kinds it needs.

The response is immutable:

```http
Cache-Control: public, max-age=31536000, immutable
ETag: "<manifest-sha256>"
```

## Get a build diff

```http
GET /api/v1/builds/{toBuildIdentifier}/diff?from={fromBuildIdentifier}
```

```json
{
  "schemaVersion": 1,
  "fromBuildId": "old-service-build-sha256",
  "toBuildId": "new-service-build-sha256",
  "addedObjects": {},
  "modifiedObjects": {},
  "removedObjectIds": [],
  "playerStats": null,
  "fameBonuses": null
}
```

Added and modified entries contain complete `GameObjectRecord` values.
`playerStats` and `fameBonuses` are null when unchanged and complete replacement
sections when changed. A missing retained diff returns `404`; the client should
then download the target manifest.

Clients should download and verify every newly referenced sprite before
atomically replacing their local active manifest.

## Get a sprite

```http
GET /api/v1/sprites/{spriteSha256}.png
```

The body is the exact transparent PNG whose SHA-256 appears in the URL.
Current renderer output is a final 40x40 tile suitable for EAM-style display.
Different objects may share one sprite hash.

```http
Content-Type: image/png
Cache-Control: public, max-age=31536000, immutable
ETag: "<sprite-sha256>"
```

## Get updater status

```http
GET /api/v1/status
```

The response reports the latest build IDs, check timestamps, whether a hint is
pending, and whether the last refresh has an error. It never exposes the
stored error message or secrets.

## Health endpoints

```http
GET /health/live
GET /health/ready
```

`live` checks process responsiveness. `ready` checks PostgreSQL connectivity.
Health routes are not rate limited.

## Rate limits

The built-in limits are per client and per API replica:

- Metadata: 180 requests per minute.
- Sprites: 600 requests per minute.
- Update hints: 6 requests per minute.

Rejected requests return `429 Too Many Requests` with `Retry-After: 60`.
Update-trigger abuse is additionally bounded by a PostgreSQL-backed five-minute
official-check cooldown shared by all replicas. A public deployment should add
cluster-wide ingress or CDN limits.
