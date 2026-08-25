# API Contract

This document describes the implemented version 1 HTTP contract, currently
serving schema version 2 payloads.

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

## Schema versions

The route path stays `/api/v1`; `schemaVersion` describes the payload. It is
folded into the build ID, so a schema change publishes new immutable builds and
leaves already-published ones readable at their original version.

| Version | Change |
| --- | --- |
| 2 | Fame bonuses gained `description` and `shortDisplayName`, and stray whitespace is trimmed from their ids. |
| 1 | Initial contract. |

Schema 1 dropped both fame-bonus text fields, because the extractor's model
declares them as numbers and the client writes them as text. It also published
two ids with a trailing carriage return, `PotionDrinker` and `CritterFoe`, which
the client ships that way; anything keying on the id saw them as distinct from
the names used everywhere else.

Nothing else about a fame bonus changed. Conditions, bonuses, and every other
field are byte-for-byte what schema 1 published.

## Get the latest build

```http
GET /api/v1/builds/latest
```

```json
{
  "schemaVersion": 2,
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
  "schemaVersion": 2,
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
  "fameBonuses": [
    {
      "id": "Undead ForestAdversary",
      "code": 548,
      "displayGroup": "Enemy Bonuses",
      "displayCategory": "Undead Forest Kills",
      "displayName": "Undead Forest Adversary",
      "shortDisplayName": "Adversary",
      "description": "Kill 1000 Undead Forest Enemies",
      "absoluteBonus": 100,
      "relativeBonus": 0,
      "maxRepeatCount": 0,
      "repeatable": false,
      "conditions": [
        {
          "threshold": 100,
          "stat": "Undead Forest",
          "value": "StatValue"
        }
      ],
      "metadataHash": "metadata-sha256"
    }
  ],
  "playerStatsHash": "section-sha256",
  "fameBonusesHash": "section-sha256"
}
```

A fame bonus is earned when every one of its `conditions` holds. A condition's
`value` names how it is tested; current builds use `StatValue` (the player stat
named by `stat` has reached `threshold`), `MaxedStat` (that stat is at its
maximum), and `FirstCharacter`. `stat` is absent for a type that reads none. A
`repeatable` bonus grants its value once per whole multiple of the threshold, up
to `maxRepeatCount` times, and its display names contain a `{0}` placeholder for
the repeat count.

`displayGroup`, `displayCategory`, `displayName`, `shortDisplayName`, and
`description` are omitted when the client leaves them empty. The first two are
what group the flat list for display.

Every value is reproduced exactly as the client declares it, including where the
client disagrees with itself. The example above declares `threshold` 100 while
its own description says 1000, and the same gap appears on the `Adversary` and
`Slaughterer` tier of all 20 per-biome kill categories. The game awards the
bonus at the threshold, so the threshold is what a character actually needs, and
the service publishes it unchanged: a consumer that mirrors this API shows what
the game shows. Correcting either value here would put every consumer out of
step with the game.

So `threshold` is the number to evaluate progress against, and `description` is
prose that can be out of date with it.

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
  "schemaVersion": 2,
  "fromBuildId": "old-service-build-sha256",
  "toBuildId": "new-service-build-sha256",
  "realmBuildHash": "realm-build-md5",
  "sourceChecksum": "resources-assets-md5",
  "generatedAt": "2026-08-19T10:00:00Z",
  "playerStatsHash": "section-sha256",
  "fameBonusesHash": "section-sha256",
  "objectCount": 14655,
  "objectsCatalogHash": "objects-catalog-sha256",
  "addedObjects": {},
  "modifiedObjects": {},
  "removedObjectIds": [],
  "playerStats": null,
  "fameBonuses": null
}
```

`objectCount` and `objectsCatalogHash` describe the target manifest's object map
so a client can verify what it assembled. A downloaded manifest is checked
against `manifestSha256`, but an assembled one cannot be, because reproducing
the canonical serialized bytes is not something another language can be relied
on to do. The catalog hash avoids that: it is the SHA-256 of

```text
<objectId>:<metadataHash>
```

lines for every object, joined with `\n`, with the ids ordered by ordinal
comparison — so `"10"` precedes `"9"`. A client that applies a diff and gets a
different catalog should discard the result and fetch the full manifest.

Added and modified entries contain complete `GameObjectRecord` values.
`playerStats` and `fameBonuses` are null when unchanged and complete replacement
sections when changed. The target metadata lets clients atomically reconstruct
an internally complete target manifest. A missing retained diff returns `404`;
the client should then download the target manifest.

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

## Get a sprite bundle

```http
GET /api/v1/builds/{toBuildIdentifier}/sprites
GET /api/v1/builds/{toBuildIdentifier}/sprites?from={fromBuildIdentifier}
```

Returns every sprite for a build as a single uncompressed tar archive. With
`from`, only the sprites that build added relative to the other one are
included, which is the normal path for a consumer that already holds an earlier
build.

Each entry is named `<sprite-sha256>.png` and entries are ordered by hash, so a
consumer verifies them exactly as it would a single sprite response. A consumer
that already holds every sprite receives a valid empty archive, not an empty
body.

```http
Content-Type: application/x-tar
Cache-Control: public, max-age=31536000, immutable
ETag: "<build-id>"
```

The bundle for a build, or for a pair of builds, never changes, so the entity
tag is derived from the build identifiers rather than from the archive bytes and
`If-None-Match` returns `304`.

Requesting sprites individually is still supported and remains correct, but a
cold consumer needs thousands of them at once and each contains only a few
hundred bytes, so the per-request overhead dominates by an order of magnitude.
Prefer the bundle for the initial population and the `from` form afterwards.

Tar pads every entry to a 512-byte boundary, which roughly doubles the archive
relative to its payload. The route participates in response compression, so a
consumer sending `Accept-Encoding` receives close to the raw PNG size.

## Get updater status

```http
GET /api/v1/status
```

The response reports the latest build IDs, check timestamps, whether a hint is
pending, and whether the last refresh has an error. It never exposes the
stored error message or secrets.

## Get service information

```http
GET /info
```

```json
{
  "name": "EAM Game Assets API",
  "version": "1.2.0",
  "author": "TadusPro & MaikEight",
  "description": "Publishes versioned Realm of the Mad God game data and rendered item sprites extracted from the official client.",
  "lastRestart": "2026-08-25T02:33:02.487Z"
}
```

The shape every EAM service publishes, so one probe can read them all. `version`
is the release version the container image is tagged with. `lastRestart` is when
this process started, captured once at startup and held in memory, in the format
JavaScript's `Date.toISOString()` produces; it does not change until the process
restarts. Behind two API replicas, a caller reaches one of them and sees that
replica's start time.

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
- Sprite bundles: 12 requests per minute.
- Update hints: 6 requests per minute.

Rejected requests return `429 Too Many Requests` with `Retry-After: 60`.
Update-trigger abuse is additionally bounded by a PostgreSQL-backed five-minute
official-check cooldown shared by all replicas. A public deployment should add
cluster-wide ingress or CDN limits.
