# Packaging & Porting Guide

[English](./Packaging.md) | [简体中文](./Packaging-CN.md)

Maintainer-facing notes: how to move the validated source out of the production project it was
built in, and how to cut a distributable release. Not relevant to end users installing the package.

## 1. Porting the validated source into this repo

Most of the source is a clean copy + namespace change. A few pieces are edits to the *vendor*
Houdini Engine for Unity plugin's own files and need different handling - see the "Vendor patch"
rows below before you copy anything.

| Source (validated project) | Destination (this repo) | What changes |
| :--- | :--- | :--- |
| `.../Landscape/ALandscapeProxy.cs` | `Runtime/Landscape/` | Namespace stays `Hollow.Landscape.Core` or rename to match this package - pick one and keep it consistent across the whole `Runtime/Landscape` folder. |
| `.../Landscape/ALandscape.cs` | `Runtime/Landscape/` | Same as above. |
| `.../Landscape/ALandscapeStreamingProxy.cs` | `Runtime/Landscape/` | Same as above. |
| `.../Landscape/ULandscapeInfo.cs` | `Runtime/Landscape/` | Same as above. |
| `.../Landscape/FLandscapeEditLayer.cs` | `Runtime/Landscape/` | Same as above. |
| `.../Landscape/EditLayerCombine.shader` | `Runtime/Shaders/` | None - shader names/paths are independent of C# namespaces. |
| `.../Landscape/Editor/ALandscapeEditor.cs` | `Editor/Inspectors/` | Move to the `Hollow.Editor.Landscape`-equivalent namespace under this package's `Editor` asmdef. |
| `.../Landscape/Editor/GUIUtil.cs` | `Editor/Inspectors/` | Same. |
| `Plugins/HoudiniEngineUnity/Scripts/Utility/FHoudiniHeightFieldPartData.cs` | `Runtime/Translators/` | Change `namespace HoudiniEngineUnity` → this package's Houdini namespace, add `using HoudiniEngineUnity;` for `HEU_PartData` etc. |
| `Plugins/HoudiniEngineUnity/Scripts/Utility/HEU_LandscapeUtility.cs` | `Runtime/Translators/` (split into the types below) | This file currently bundles several types - split them out and **rename the static utility class away from the `HEU_` prefix** (e.g. `FHoudiniLandscapeUtility`). `HEU_` signals "official SideFX plugin code"; keeping it on code this package owns is misleading once it's no longer physically inside `Plugins/HoudiniEngineUnity`. |
| ↳ `FHoudiniUnityTransform`, `FHoudiniLandscapeMaterial`, `FHoudiniHeightFieldData`, `FHoudiniUnrealLandscapeTarget`, `FHoudiniLayersToUnrealLandscapeMapping` | `Runtime/Translators/` | Same namespace change as the file above. |
| `Plugins/HoudiniEngineUnity/Scripts/Asset/FHoudiniLandscapeTranslator.cs` | `Runtime/Translators/` | Contains both `FHoudiniLandscapeCreationInfo` and the `FHoudiniLandscapeTranslator` static class - namespace change only. |

### Vendor patches (cannot be "copied" - must be reapplied to the user's own plugin install)

These touch files that genuinely belong to the official Houdini Engine for Unity plugin. They
cannot live in this package's `Runtime/Translators` because the package doesn't ship/own those
files. Until Houdini Engine for Unity exposes a real extension point for this, document the exact
diff and have users (or a small installer script) apply it to their own copy of the plugin:

| Vendor file | What was added |
| :--- | :--- |
| `Scripts/Core/HEU_Defines.cs` | `HAPI_UNITY_ATTRIB_LANDSCAPE_PIPELINE = "unity_landscape_pipeline"` constant. |
| `Scripts/Asset/HEU_ObjectNode.cs` | `EHoudiniLandscapePipeline` enum, and the `switch` in `GenerateGeometry` that dispatches volume parts to either the new non-destructive path or the existing `ProcessVolumeParts`. |
| `Scripts/Asset/HEU_GeoNode.cs` | `_heightFieldParts` field, `ProcessVolumePartsInNonDestructive(...)` method, and the `_heightFieldParts` cleanup branch in `DestroyLandscapeCache()`. |
| `Scripts/Asset/HEU_HoudiniAsset.cs` | `_heightFieldPartCaches` field + `AddHeightFieldPartCache` / `RemoveHeightFieldPartCache` methods. |

Keep these as a single `.patch` file (e.g. `Documentation~/vendor.patch`) generated with
`git diff` against a clean install of the plugin version this package targets, rather than prose
instructions that drift out of sync with the real diff.

### Checklist for each file you port

1. Copy the file into its destination folder above.
2. Fix the namespace/`using` directives per the table.
3. Confirm it lands in the assembly you expect - `Runtime/Landscape` vs. `Runtime/Translators`
   (see `Architecture.md` → "Design goals" #3). If a `Runtime/Landscape` file ends up needing a
   Houdini type, that's a sign it's misplaced, not a sign to add the Houdini reference to that
   asmdef.
4. Re-run the scenario that validated it originally before considering the port done - a clean
   compile is necessary, not sufficient.

## 2. Building a `.unitypackage`

This is a **separate distribution channel** from the UPM git-URL install already documented in the
root README - useful for Asset Store distribution or users who don't want to touch
`Packages/manifest.json`. You do not need to choose one over the other; both can be offered.

1. Open a Unity project with this package installed (e.g. via "Install package from disk" against
   your local clone) and, ideally, **Houdini Engine for Unity also installed** so any optional
   integration code is included in the export.
2. In the Project window, select `Runtime/`, `Editor/`, and (if you want the demo bundled)
   `Samples~/` - or right-click the package's row in the Package Manager window and choose
   *Export Package* if your Unity version supports exporting a package-manager package directly.
3. *Export Package...* → uncheck "Include dependencies" (you don't want to accidentally bundle the
   official Houdini Engine for Unity plugin into the export - see the root README's "Why Houdini
   Engine isn't bundled") → export.
4. **Where the output goes:**
   - For local testing only: into `Builds~/` (already gitignored - never committed, regardless of
     how big the file gets).
   - For an actual release: attach it to a **tagged GitHub Release**, not to a git commit. Release
     assets are stored outside the repository's git history, so the repo doesn't accumulate a
     multi-MB binary on every version bump. Name it predictably, e.g.
     `HoudiniTerrainLayers-0.1.0.unitypackage`.

## 3. Where HDA files go

Houdini Engine for Unity loads an HDA from a path inside the *consuming* Unity project, so any HDA
this repository ships has to be Unity-importable content, not loose Houdini-side data:

- **Demo/sample HDAs** (the common case - an HDA whose only purpose is to exercise this package's
  edit-layer pipeline): put them in `Samples~/EditLayerDemo/` next to the demo scene that references
  them. Because `Samples~` is only copied into a user's project when they explicitly import the
  sample from Package Manager, the HDA doesn't bloat anyone's project who isn't using the demo.
- **Anything meant to ship as core functionality**: reconsider first - this package's job is to be a
  generic translator for *any* HDA that uses the right attribute conventions
  (`unreal_landscape_output_mode`, edit-layer attributes, etc., see `Architecture.md`), not to ship a
  specific terrain HDA. If you do need one (e.g. a reference implementation), it likely still belongs
  under `Samples~/`, just possibly its own sample entry in `package.json`'s `"samples"` array.

**Licensing note on the file format** (`.hda` vs `.hdalc` vs `.hdanc`): which extension is
appropriate depends on the Houdini license tier you authored it with and what tier your *consumers*
are expected to have (e.g. Houdini Engine-only users without a full Houdini seat). This has genuine
commercial licensing implications that depend on specifics of your situation - confirm the correct
format against SideFX's own current terms ([Licensing](https://www.sidefx.com/Support/licensing/),
[Indie FAQ](https://www.sidefx.com/faq/indie-new/)) rather than assuming; this document isn't a
substitute for that.
