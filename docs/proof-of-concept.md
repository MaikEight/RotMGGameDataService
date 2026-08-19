# Extraction Proof of Concept

This page records the first end-to-end live extraction completed on 2026-08-19.
The values describe one Realm build and are measurements, not API constants.

## Result

The proof succeeded on both Windows and Linux. Both platforms discovered the
same official build, verified and decompressed the source, extracted the same
models, rendered the same objects, and produced identical hashes for normalized
pixels and encoded PNG bytes.

| Measurement | Windows | Linux |
| --- | ---: | ---: |
| Realm build hash | `aeceb1f212da9dcaba9e6c41a34c9d69` | same |
| Source MD5 | `c32dd849645d89ff0841eabfd8c17a67` | same |
| Rendered objects | 14,655 | 14,655 |
| Unique PNGs | 6,201 | 6,201 |
| Unique PNG bytes | 2,566,427 | 2,566,427 |
| Extraction duration | 15.69 seconds | 18.06 seconds |
| Peak working set | 3,205,611,520 bytes | 3,196,280,832 bytes |
| Missing textures | 8 | 8 |
| Failed images | 0 | 0 |
| Duplicate object IDs | 1 | 1 |

The normalized visual catalog SHA-256 was
`4871a9d4b36d3c2386cbbe464930a5c0239607683ee6ff36c337e23397eb858e`.
The exact PNG catalog SHA-256 was
`fc897083696cda45b22a75d0e0a96a1fccfbc8938c117e5e6ca434396dbaaa03`.

The official archive was approximately 47 MB compressed and the verified
`resources.assets` file was 393,885,168 bytes after decompression. The generated
PNG set is small because identical visuals are stored once by content hash.

## Extracted model counts

| Model kind | Count |
| --- | ---: |
| Dye | 802 |
| Emote | 314 |
| Enchantment | 1,016 |
| Entrance | 39 |
| Equipment | 11,353 |
| EquipmentSet | 120 |
| FameBonus | 612 |
| ForgeProperties | 2,049 |
| GameObject | 8,469 |
| Object | 66 |
| PetAbility | 9 |
| PetSkin | 701 |
| Player | 19 |
| PlayerStat | 130 |
| Portal | 209 |
| Skin | 1,446 |

These model collections overlap; their 27,354 total entries are not 27,354
unique renderable IDs. The renderer selects the object categories that have a
client-facing sprite and resolves them by object ID.

## Current limitation

The approximately 3.2 GB peak working set is high relative to the small final
output. The next implementation step is to profile the extractor and determine
whether decoded Unity textures and model data can be released or processed in
smaller batches. Until that work is complete, a production updater should not
be assigned a speculative low memory limit.

## What this proves

- The pinned extractor runs in a non-root .NET 8 Linux container.
- Live metadata and source validation work without trusting a caller-supplied
  URL or checksum.
- Reusing the verified source avoids a second large download.
- The accepted 40-by-40 EAM-compatible renderer is deterministic across the
  tested Windows and Linux environments.
- Per-object, content-addressed PNG storage is practical for this catalog.

It does not yet prove PostgreSQL publication, API contracts, build diffs,
multi-instance hint coordination, or EAM integration with the service.
