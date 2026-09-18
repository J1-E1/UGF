# Architecture

[English](./Architecture.md) | [简体中文](./Architecture-CN.md)

> This document describes the design as validated in a production project (see the repo root
> README's status note). File/line references to "the prototype" mean that codebase, not this one -
> this repo does not yet contain the ported source.

## Design goals

1. **Non-destructive**: re-cooking a Houdini asset, or editing one edit layer, must never destroy
   another layer's data.
2. **GPU-resident blending**: layers combine via `RenderTexture` ping-pong, not repeated CPU
   `TerrainData.SetHeights` calls.
3. **Houdini is a producer, not a dependency of the engine**: `Runtime/Landscape` (the blend engine)
   must compile and function with zero Houdini Engine references. Only `Runtime/Translators`
   depends on it. See each folder's README for the assembly split that enforces this.
4. **Traceable to Unreal's actual implementation**: where this package mirrors a concept from
   Unreal Engine's HoudiniEngine plugin, the field/type names are kept close to the original
   (`FHoudiniHeightFieldPartData`, `FHoudiniTileInfo`, `FHoudiniLandscapeCreationInfo`,
   `FHoudiniUnrealLandscapeTarget`) so the source can be cross-referenced later.

## Data model

### `FHoudiniHeightFieldPartData` (Translators)

One instance per Houdini heightfield volume part. Mirrors Unreal's struct of the same name
(`HoudiniLandscapeUtils.h`, `struct FHoudiniHeightFieldPartData`) field-for-field where practical.

| Field | Meaning |
| :--- | :--- |
| `ObjectId` / `GeoId` / `PartId` | HAPI identity triplet for this part. |
| `TargetLayerName` | The volume's name - `"height"`, `"visibility"`, or a paint layer name like `"dirt"`. Read from the volume info, **not** a cached/stale value - see "Pitfalls" below. |
| `UnityLayerName` | The *edit layer* name (e.g. `"Base_Height"`, `"River_Erosion"`). |
| `HeightField` | The source `HEU_PartData` (or equivalent) this part's voxel data comes from. |
| `EditLayerType` | `HAPI_UNREAL_LANDSCAPE_EDITLAYER_TYPE_BASE` (0) or `..._ADDITIVE` (1). |
| `bSubtractiveEditLayer` | Subtracts instead of adding when `EditLayerType` is additive. |
| `bCreateNewLandscape` | True = Houdini owns this landscape's lifecycle (created/destroyed by the cook). False = it targets an externally-owned landscape that's only modified, never destroyed. |
| `TargetLandscapeName` | The grouping key for which `ALandscape` this part belongs to. |
| `SizeInfo` | `FHoudiniLandscapeCreationInfo` - grid dimensions, section/component sizing. |
| `TileInfo` | `FHoudiniTileInfo?` - set only when this part is one tile of a larger multi-tile landscape. |
| `CreatedLandscape` | Back-reference to the `ALandscape` this part resolved to, when `bCreateNewLandscape`. Used to destroy Houdini-owned landscapes before the next cook recreates them. |

### `FHoudiniTileInfo` (Translators)

```csharp
public struct FHoudiniTileInfo
{
    public Vector2Int TileStart;            // this tile's position within the whole landscape
    public Vector2Int LandscapeDimensions;  // dimensions of the entire landscape
}
```

Mirrors Unreal's `FHoudiniTileInfo` (`HoudiniLandscapeUtils.h`) exactly. **Not** related to
`HAPI_VolumeTileInfo` (a low-level HAPI voxel-read cursor) despite the similar name - see the
"Pitfalls" section.

### `ULandscapeInfo` / `FLandscapeEditLayer` (Landscape engine)

```
ALandscape (master)
├── _streamingProxies : List<ALandscapeProxy>      // tile membership lives on the actor, not the info
└── _landscapeInfo : ULandscapeInfo                // [SerializeField] - the edit-layer stack persists with the scene
        ULandscapeInfo
        ├── LandscapeGuid
        ├── EditLayers : List<FLandscapeEditLayer>  // stack order = blend order (index 0 = base, bottom)
        └── (layer-info-by-name cache, reserved for paint layers)
                FLandscapeEditLayer : IHeightModifier
                ├── Name, bLocked, bVisible
                ├── CombineMode   (HeightStamp.CombineMode; for height only Override/Add are used - see "Combine mode mapping")
                ├── Opacity                              // the edit layer's Alpha (shader _CombineBlend)
                ├── SavedHeight : Texture2D              // persisted height asset - lets a layer survive a scene reload without a re-cook
                ├── TileHeightData : Dictionary<Vector2Int, RenderTexture>   // [NonSerialized] runtime GPU state
                └── TilePaintData  : Dictionary<Vector2Int, Dictionary<string, RenderTexture>>
```

`FLandscapeEditLayer` implements `IHeightModifier` (the same interface hand-authored height stamps
implement) so it flows through the identical GPU blend pass - this is the key decoupling point: the
blend engine has no special-case code for "Houdini layer" vs. "hand-placed stamp".

The streaming-proxy registry is held by the master `ALandscape` (`_streamingProxies`), **not** by
`ULandscapeInfo` - a single `ULandscapeInfo` instance is owned by the master and shared with each
streaming proxy via `GetLandscapeActor().GetLandscapeInfo()`. Only the edit-layer stack (and each
layer's settings) is serialized; the per-layer GPU `RenderTexture`s are `[NonSerialized]` and rebuilt
on the next cook, or lazily from `SavedHeight` after a reload.

## Cook pipeline

```
GetPartsToTranslate(volumeParts)
  -> one FHoudiniHeightFieldPartData per volume part, with TileInfo parsed if present
  -> "Update Existing" parts (bCreateNewLandscape == false) require the HDA's
     AllowLandscapeModification flag; if it's off, the whole cook's landscape output is
     aborted (returns empty) - mirrors Unreal's IsLandscapeModificationEnabled() gate.
       |
       v
ResolveLandscapes(landscapeMap, parts, inputLandscapes)
  -> 3 passes:
     1. Bucket each part by bCreateNewLandscape:
          true  + already in landscapeMap -> reuse (same-cook dedup, see "Pitfalls")
          true  + not yet created         -> bucket into LandscapesToCreate[name][layerKey]
          false                           -> FindTargetLandscapeProxy (scene search / "Input<N>")
     2. Create an ALandscape for each LandscapesToCreate entry; size it from the
        height-data part; write the base height (ImportHeightData) so the Terrain has a
        valid resolution before any edit layer is blended in.
     3. Apply per-part updates (materials, etc.) to existing/externally-owned landscapes.
       |
       v
TranslateHeightFieldPart(landscapeTarget, part)   // per part, after resolve
  -> fetch + normalize height data
  -> (height) write into the part's edit layer's TileHeightData RenderTexture;
     CombineMode set from EditLayerType / bSubtractiveEditLayer
  -> (paint/visibility) normalize to 0..1 (alphamap write-back: TODO)
       |
       v
ALandscape.RebuildHeightmap()   // once per touched landscape, after all parts translated
  -> collect ULandscapeInfo.EditLayers (stack order) + GetComponentsInChildren<IHeightModifier>
  -> GenerateHeightmap: ping-pong RenderTexture blend across the combined list
  -> ApplyRawHeightsToTerrain: read back + TerrainData.SetHeights (raw 0..1, Y-flipped)
       |
       v
SaveTerrainDataToAssetCache(landscape)
  -> persists the runtime TerrainData into the HDA's asset cache folder so the landscape
     can be baked into a usable prefab (a runtime TerrainData cannot be referenced by a prefab)
       |
       v
SaveEditLayerHeightsToAssetCache(landscape)
  -> reads each edit layer's blended height RT back into a Texture2D in the same cache folder
     and points layer.SavedHeight at it, so the whole stack survives a scene reload before
     baking (see "Edit-layer asset lifecycle")
```

Before the next cook's `ResolveLandscapes` runs, any landscape a previous-cook part created
(`bCreateNewLandscape && CreatedLandscape != null`) is unconditionally destroyed and recreated - this
matches Unreal's actual behavior (`FHoudiniLandscapeRuntimeUtils::DeleteLandscapeCookedData`), which
is a destroy-and-rebuild strategy, **not** an incremental per-layer diff. Landscapes targeted via
`bCreateNewLandscape == false` are never destroyed by this package; they're externally owned.

> **Create vs. update**, by `bCreateNewLandscape` (Houdini's `unreal_landscape_output_mode`):
> `GENERATE` spawns a fresh `ALandscape` (reusing one of the same name if it survived an earlier
> cook, so the rest of its edit-layer stack is preserved); any other mode ("Update Existing")
> targets a landscape already in the scene and only *adds/updates* an edit layer on it. An
> Update-Existing part is honored only if the HDA opts in via `AllowLandscapeModification` (default
> on); when that flag is off, the whole cook's landscape output is aborted, mirroring Unreal's
> `Cookable->IsLandscapeModificationEnabled()` returning `{}`.

## Edit-layer asset lifecycle

Each edit layer's height lives in two places: a `[NonSerialized]` runtime `RenderTexture`
(`TileHeightData`, rebuilt every cook) and a persisted `Texture2D` asset (`SavedHeight`) in the HDA
cache. The asset is what lets an un-baked landscape reload - and the inspector preview the actual
height - without a re-cook (`GetHeightRenderTexture` lazily rebuilds the RT from `SavedHeight`).

`SaveEditLayerHeightsToAssetCache` keeps these in sync **without leaking duplicate assets**:

- **Overwrite in place on re-cook**: if a layer's `SavedHeight` is already a persisted asset, its
  pixels are re-read into that same `Texture2D` (resized via `Reinitialize` if the resolution
  changed) rather than calling `CreateAsset` again at the same path - the latter does not cleanly
  overwrite an occupied path. (`SaveTerrainDataToAssetCache` has the equivalent `ContainsAsset`
  guard for the same reason.)
- **Rename-sync**: the asset's filename is derived from the layer name; if the layer was renamed in
  the inspector since the last save, the asset is `RenameAsset`-d to match on the next cook.
- **Delete on removal**: removing a layer (inspector "trash" button) or destroying a Houdini-owned
  landscape deletes that layer's `SavedHeight` asset (`FLandscapeEditLayer.DeleteSavedHeightAsset`),
  so the cache never accumulates orphaned `*_Height.asset` files. This is editor-only
  (`#if UNITY_EDITOR`), since the engine assembly also compiles into player builds where
  `AssetDatabase` doesn't exist.

## Inspector (`ALandscapeEditor`)

The custom inspector reproduces Unreal's landscape **Edit Layers** panel: a header with the layer
count and a `+` (add-on-top) button, then one row per layer drawn top-to-bottom (highest index =
top of the panel = last-applied layer), each showing a lock toggle, a visibility (eye) toggle, the
editable name, an `Alpha` field (the layer's `Opacity`), a delete button, an `Additive` toggle, and
reorder arrows, above a small height preview.

- **Lock** disables that row's editable fields and tells the cook not to overwrite the layer's height
  (`bLocked`); it stays clickable so the layer can be unlocked again.
- **Eye** toggles `bVisible`, which `IHeightModifier.IsEnabled()` reads to include/exclude the layer
  from the blend.
- **Additive** is the only height combine choice exposed (Unreal height edit layers blend just two
  ways - additive delta vs. replace-and-alpha-blend), so it maps to `CombineMode` `Add`/`Override`
  rather than the full `HeightStamp.CombineMode` enum.
- Any field/structure change marks the `ALandscape` dirty (so the serialized `ULandscapeInfo`
  actually persists), then calls `RebuildHeightmap` for a live re-blend.
- **Icons** resolve in three tiers so a row is never blank: a custom PNG in the package's `Resources`
  folder (named `editlayer_lock_on`, `editlayer_lock_off`, `editlayer_eye_on`, `editlayer_eye_off`,
  `editlayer_delete`, `editlayer_stack`) → a built-in Unity editor icon → a plain text glyph.

## GPU blend (`GenerateHeightmap`)

Ping-pong between two same-size `RenderTexture`s; each enabled height modifier blits its
contribution from the source RT into the destination RT, then the two swap:

```csharp
foreach (var modifier in heightModifiers)
    if (modifier.GetBounds().Intersects(terrainBounds))
        if (modifier.ApplyHeightStamp(rt1, rt2, heightmapData, od))
            (rt1, rt2) = (rt2, rt1);
```

`FLandscapeEditLayer.ApplyHeightStamp` uses a dedicated full-render-target combine shader
(`EditLayerCombine.shader`) rather than the stamp-placement shader hand-authored `HeightStamp`s use
(`Hidden/MicroVerse/HeightmapStamp`) - an edit layer already covers the whole terrain, so it doesn't
need stamp transform/falloff math, just `src`, `combine-mode`, `layer height`, `opacity` in, blended
height out.

**Safety guard**: if nothing actually blended (no layer has data yet, or the combine shader failed
to load), `GenerateHeightmap` returns `null` instead of an all-zero RT, and the caller skips the
heightmap write-back rather than flattening the terrain.

**Write-back via `SetHeights`, not `CopyActiveRenderTextureToHeightmap`**: the blend produces a raw
`0..1` height RT, which `ALandscape.ApplyRawHeightsToTerrain` reads back to the CPU and writes with
`TerrainData.SetHeights` (Y-flipped to Unity's bottom-left origin). This is the same path Unity's
"Import Raw" uses. `CopyActiveRenderTextureToHeightmap` was tried first but consumes Unity's *packed*
heightmap format (raw `1.0` stored as `~0.5`); matching that packing exactly proved unreliable - peaks
clamped flat into a "missing chunk" - whereas a direct Import Raw of the identical data renders
correctly, which is what isolated the bug to the write-back rather than the data. The blend stays
fully on the GPU; only the final terrain write is on the CPU (negligible at cook / edit cadence,
which is not per-frame).

## Combine mode mapping

| Houdini attribute state | `HeightStamp.CombineMode` |
| :--- | :--- |
| `EditLayerType == BASE` (and not subtractive) | `Override` |
| `EditLayerType == ADDITIVE`, not subtractive | `Add` |
| `bSubtractiveEditLayer == true` | `Subtract` (regardless of `EditLayerType`) |

## Multi-edit-layer keying (a real bug this design fixes)

Bucketing parts only by `TargetLandscapeName` + `TargetLayerName` silently drops a second edit layer
targeting the same volume layer (e.g. two `"height"` edit layers stacked on one landscape) - the
second would log a "duplicate layer" warning and be discarded. The fix is keying by
**`(TargetLayerName, UnityLayerName)`** so each distinct edit layer survives bucketing.

## Tile placement (not data merging)

A common misreading: `FHoudiniTileInfo` does **not** mean multiple physical Houdini tiles get
merged into one landscape's heightmap. Unreal's own bucketing
(`TMap<FString /*layer*/, FHoudiniHeightFieldPartData*>` per landscape name, with duplicates
*warned and dropped*, never merged) confirms one `TargetLandscapeName` = one independent landscape
actor. Tiling instead works by giving each tile its own `TargetLandscapeName` (so each becomes its
own `ALandscape`), and `TileInfo.TileStart` shifts that actor's world transform so independently
created/positioned landscapes line up at the edges:

```cpp
// Unreal: GetLandscapeActorTransformFromTileTransform - pure translation, no data merge.
Result.SetLocation(Result.GetLocation() - OffsetX - OffsetY);
```

This package currently writes the base height at creation time but does not yet apply this
tile-offset transform - see TODO in the root README.

## Known simplifications vs. Unreal

- `ULandscapeInfo` is **not** GUID-keyed through a global subsystem (Unreal: `ULandscapeSubsystem`).
  One info instance is owned directly by the master `ALandscape` and shared with its streaming
  proxies via `GetLandscapeActor().GetLandscapeInfo()`.
- `FLandscapeEditLayer.TileHeightData` currently only resolves a single (`Vector2Int.zero`) entry;
  true per-tile lookup is part of the deferred World Partition / tiling work.
- Paint/visibility layers normalize but do not yet write into `TerrainData` alphamaps.
- `FHoudiniHeightFieldPartData` intentionally omits Unreal's `HeightRange`, `PropertyAttributes`, and
  a few other `TOptional<...>` fields that have no current consumer in this package.

## Pitfalls worth re-reading before touching this code

- **`TargetLayerName` must come from the volume's own name attribute**, not a cached
  `GetVolumeLayerName()`-style accessor that only some other, unrelated pipeline populates - using a
  stale/null value here null-keys a dictionary and crashes the resolve pass.
- **String-attribute reads that need "any owner" (Unreal's `HAPI_ATTROWNER_INVALID`)** must use an
  owner-agnostic attribute read, not a "strict owner" helper that rejects an explicit "invalid"
  owner value outright.
- **A runtime-created `TerrainData` renders pink under URP/HDRP** unless a pipeline-appropriate
  terrain shader is assigned explicitly; the built-in terrain shader is not part of those pipelines.
- **`HAPI_VolumeTileInfo` (HAPI's voxel-block read cursor) and `FHoudiniTileInfo` (multi-tile
  landscape placement) are unrelated** despite both having "tile" in the name - don't reach for one
  when you mean the other.
