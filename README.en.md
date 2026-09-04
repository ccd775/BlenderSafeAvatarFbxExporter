# Blender-Safe VRChat Avatar FBX Exporter

![A VRChat avatar exported from a Modular Avatar Manual Bake result in Unity and opened in Blender](https://raw.githubusercontent.com/ccd775/BlenderSafeAvatarFbxExporter/v0.2.1/Documentation~/images/blender-safe-export-overview.png)

[![Source validation](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/actions/workflows/source-validation.yml/badge.svg)](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/actions/workflows/source-validation.yml)
[![Latest release](https://img.shields.io/github/v/release/ccd775/BlenderSafeAvatarFbxExporter?label=Release)](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.md)
[![Unity 2022.3 LTS](https://img.shields.io/badge/Unity-2022.3%20LTS-black.svg)](https://unity.com/releases/editor/whats-new/2022.3.22)

[简体中文（默认）](README.md)

An Editor-only Unity FBX exporter for **VRChat avatar creators**. It exports a VRChat Avatar/Prefab produced by **Modular Avatar Manual Bake** to a Blender-compatible FBX while preserving manually edited bone poses, BlendShapes, skin weights, and textures.

It is intended for Unity-to-Blender VRChat avatar workflows and for exporting a merged Modular Avatar result to FBX. A Manual Bake result can look correct in Unity while relying on conflicting bind poses, scale-compensated bones, duplicate control points, or FBX node layouts that Blender cannot represent directly, causing displaced bones, shifted meshes, missing BlendShapes, or corrupted deformation after a regular export.

## Intended workflow

- Assemble a VRChat avatar, outfits, hair, and accessories with Modular Avatar in Unity.
- Manually adjust bones or BlendShape values on the Manual Bake result before export.
- Continue editing the merged avatar in Blender while retaining an editable armature, skin weights, and shape keys.

> This is an unofficial community tool for VRChat creators and is not affiliated with or endorsed by VRChat Inc. It does not upload avatars or call the VRChat API.

## Companion FBX post-processing tool

A separate optimizer is available for exported FBX files. It **merges and consolidates skeletons and skin weights, performs topology-safe cleanup, and can either preserve source UVs or build and rebake semantic Atlases**. This repository remains focused on safe export from Unity; the companion tool handles those later optimization steps.

**Companion tool:** [VRChatAvatarFBXOptimizer](https://github.com/ccd775/VRChatAvatarFBXOptimizer)

## Features

- Bakes the current bone transforms into a unified Blender rest pose.
- Preserves skin weights and an editable armature.
- Preserves BlendShape channels, frame deltas, and current nonzero weights, normalized onto 100-weight target shapes.
- Preserves manually adjusted bone transforms and BlendShape values from the selected Manual Bake result.
- Normalizes scale-compensated bone hierarchies while preserving the visible pose.
- Embeds material textures into the FBX.
- Maps common diffuse, normal, emission, alpha, metallic, and smoothness semantics to standard FBX material properties.
- Preserves every Unity material texture binding as `UnityTexture_<property>` metadata, including non-standard shader properties.
- Handles generated `Texture2D`/`RenderTexture` values and textures located under `Packages/`.
- Handles NDMF NaNimation deletion bones by culling affected primitives instead of turning hidden geometry visible again.
- Works around Unity FBX Exporter 4.2.1 control-point merging that can silently corrupt BlendShapes.
- Separates a `SkinnedMeshRenderer` onto its own node when it hosts a bone or carries child objects, so its transform can be normalized safely.
- Reopens and validates the saved FBX before replacing the requested output.
- Uses transactional replacement so an existing FBX is not overwritten until the new file has passed validation.
- Modifies only a temporary inactive clone; the selected avatar and its assets are not changed.

## Requirements

- Unity **2022.3 LTS** (verified with 2022.3.22f1)
- Unity FBX Exporter package `com.unity.formats.fbx` **4.2.1** (the verified version)
- Autodesk FBX SDK package `com.autodesk.fbx` **4.2.1** (installed transitively)
- Blender **4.2 LTS** recommended (verified with Blender 4.2.23)
- Source meshes must have **Read/Write** enabled

Other FBX Exporter and FBX SDK versions are accepted with a warning. Only a missing package, or one older than 4.1.0, is refused. Check the exported FBX in Blender before relying on an unverified version.

Modular Avatar, NDMF, VRChat SDK, and lilToon are **not compile-time dependencies**. Modular Avatar is only the workflow that produces the intended input hierarchy.

## Installation

### Recommended: `.unitypackage` from GitHub Releases

1. Open the target project with Unity **2022.3 LTS**.
2. In **Window > Package Manager**, install **FBX Exporter 4.2.1** from the Unity Registry.
3. Download [`BlenderSafeAvatarFbxExporter-v0.2.1.unitypackage`](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/releases/download/v0.2.1/BlenderSafeAvatarFbxExporter-v0.2.1.unitypackage) from the [v0.2.1 release](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/releases/tag/v0.2.1).
4. Double-click the file, or choose **Assets > Import Package > Custom Package...** in Unity.
5. Keep all files selected, click **Import**, and wait for the Editor assembly to compile.

> The `.unitypackage` contains only this tool. It does not bundle Unity FBX Exporter or Autodesk FBX SDK. Install FBX Exporter 4.2.1 first; Autodesk FBX SDK 4.2.1 is installed transitively with it.

If an older or source-installed copy is already present, make sure there is only one `Assets/BlenderSafeAvatarFbxExporter` folder to avoid duplicate assemblies.

### Source installation

Alternatively, download or clone the repository and place the complete repository at:

```text
Assets/BlenderSafeAvatarFbxExporter
```

A Git submodule at that path is also supported. Wait for Unity to compile the Editor assembly afterward.

## Usage

1. Use Modular Avatar's **Manual Bake** workflow.
2. Select the generated baked avatar root, not the original avatar that still contains Merge Armature components.
3. Make any final manual adjustments on the baked result:
   - rotate or move bones;
   - set BlendShape weights;
   - choose active materials/textures.
4. Open **Tools > Avatar > Blender-Safe FBX Exporter**.
   - Alternatively, right-click the selected root and choose **Avatar > Export Blender-Safe FBX...**.
5. Assign the baked avatar root.
6. Leave **Embed all material textures** enabled unless you explicitly want geometry-only material references.
7. Export the FBX and import it into Blender.

The current bone pose becomes the FBX rest pose. This tool does not export an animation clip for those edits.

## Texture behavior

The exporter embeds all material texture properties it can represent:

- Physical texture files under `Assets/` are staged from their source files.
- Texture files under `Packages/` are resolved through Unity Package Manager.
- Generated `Texture2D` and `RenderTexture` values are converted to PNG.
- Common standard channels are connected for Blender's importer.
- Other shader-specific bindings are retained as FBX custom metadata.

Blender cannot reconstruct an arbitrary Unity shader such as lilToon. Embedded images and metadata are preserved, but complex shader graphs may need to be rebuilt manually.

## Programmatic API

```csharp
using ccd775.AvatarFbxExporter;

var result = BlenderSafeAvatarFbxExporter.Export(
    manualBakeRoot,
    outputPath,
    new BlenderSafeFbxExportOptions
    {
        EmbedAllMaterialTextures = true,
        OverwriteExisting = false,
        ValidationLevel = BlenderSafeFbxValidationLevel.Balanced
    });

foreach (var warning in result.Warnings)
{
    Debug.Log(warning);
}
```

`OverwriteExisting` defaults to `false`. The Editor UI asks for confirmation before setting it.

The returned `BlenderSafeFbxExportResult` includes mesh, vertex, BlendShape, texture, deletion-conversion, control-point-adjustment, pose-error, skeleton-error, and bind-pose statistics, plus the warnings recorded during the export.

## Validation and deviation budgets

Four stages measure how far the rebuilt avatar drifts from what Unity displays: pose baking, skeleton normalization, bind-pose unification, and control-point disambiguation. Each deviation is normalized by the size of the geometry it was measured on, so an avatar authored in centimetres is judged by the same standard as one authored in metres.

- Below the reporting budget: silent.
- Between the reporting and abort budgets: recorded in `result.Warnings` and the console, and the FBX is still written.
- Above the abort budget: the export is refused, naming the worst control point, the BlendShapes that move it, and the settings that commonly explain it.

**Advanced > Validation** in the exporter window offers `Balanced` (default), `Strict` (the 0.1.x thresholds) and `Report only` (a geometric deviation never aborts).

The pose-bake number has a precise meaning: **the exported base geometry and BlendShape targets are always exact**, so it only describes how the avatar looks at its recorded default BlendShape weights compared with Unity. Structural faults are outside the budget scheme and abort at every level: non-finite values, a mesh that cannot be rebuilt comparably, reflected or singular transforms, and bones outside the avatar hierarchy.

## Important limitations

- BlendShapes with in-between frames are flattened onto their full-weight frame and reported, because Blender keeps one target shape per channel.
- All weighted bones and the optional `rootBone` must be inside the selected avatar hierarchy.
- Meshes must be readable and have one bind pose per bone.
- Vertices with negative or zero total bone weight are reported and exported exactly as Unity displays them.
- Animation components and Unity constraints are intentionally removed from the temporary export clone.
- NDMF NaNimation conversion removes every primitive that touches a deleted vertex. The success report shows the affected vertex and primitive counts.
- A renderer hosted on a bone, or one whose Transform carries child objects, is moved onto a dedicated `__Mesh` node automatically.
- Reflected, singular, or non-TRS bone transforms cannot be converted safely and are rejected.
- The complete selected hierarchy is passed to Unity FBX Exporter; supported static meshes, cameras, and lights under that hierarchy may also be exported.
- Generated cubemaps, texture arrays, and other non-2D texture types are skipped with a warning.
- Material conversion is approximate, not a shader conversion system.
- Tested on Windows. macOS and Linux Editor behavior has not yet been certified.

See [the technical notes](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/blob/v0.2.1/Documentation~/technical-notes.md) and [validation record](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/blob/v0.2.1/Documentation~/validation.md) for implementation rationale and verification details.

## Tests

EditMode tests are included under `Tests/Editor` and cover:

- preservation of a manually rotated bone;
- preservation of a nonzero BlendShape value;
- generated texture embedding;
- saved-FBX reopen and channel validation;
- source-object immutability;
- refusal to overwrite an existing file without explicit permission;
- automatic separation of a renderer whose Transform carries child objects;
- in-between BlendShape flattening with a rescaled `DeformPercent`;
- a sound avatar reporting its pose-bake deviation instead of failing.

Run them from **Window > General > Test Runner > EditMode**.

## License

The source code in this repository is licensed under the [MIT License](LICENSE.md).

Unity FBX Exporter and Autodesk FBX SDK are dependencies and are not redistributed or relicensed by this repository. See [Third Party Notices.md](Third%20Party%20Notices.md).
