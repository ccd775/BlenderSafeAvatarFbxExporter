# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

## [0.2.1] - 2026-09-04

Packaging fix. The exporter itself is unchanged from 0.2.0.

### Fixed

- The 0.2.0 release `.unitypackage` could not be imported. It was not produced by
  Unity, and its archive was laid out in a way Unity's package reader does not
  accept: entries carried PAX extended headers, the per-GUID directory entries
  were missing, and the gzip stream recorded the package's own file name instead
  of the `archtemp.tar` name Unity looks for. Unity reported the import as
  completed and installed nothing. The release package is now exported by Unity
  itself and verified by importing it into a clean project.

## [0.2.0] - 2026-09-03

This release reworks validation. Every check that used to abort an export over a
deviation far below what anyone can see now measures the same number, reports it,
and aborts only when the deviation is large enough to matter.

### Changed

- Geometry deviation budgets are now relative to the size of the geometry they were
  measured on instead of a fixed `1e-4` in avatar-root units, so an avatar authored
  at a different unit scale is judged by the same standard.
- Pose baking, skeleton normalization, bind-pose unification and control-point
  disambiguation report a deviation between the warn and abort budgets instead of
  failing. The measured values are in `BlenderSafeFbxExportResult` either way.
- The pose-bake deviation is measured only over control points a triangle actually
  references. Orphan and NaNimation-culled points are reported separately rather
  than vetoing an export nobody could see a problem in.
- A failing pose-bake check now names the worst control point, the BlendShapes that
  move it, and the project settings that commonly explain the drift.
- BlendShape targets are exported against a normalized 100-weight frame, and the
  recorded `DeformPercent` is rescaled to match. A channel whose source frame weight
  was not 100 now round-trips exactly.
- Unity's "Clamp BlendShapes (Deprecated)" player setting is honoured when recording
  weights, so the FBX reproduces what the editor displays instead of extrapolating.
- FBX Exporter and Autodesk FBX SDK versions other than the verified 4.2.1 are
  accepted with a warning. Only a missing package or one older than 4.1.0 is fatal.
- The Autodesk typed-property adapter is now an optional fallback. When it is
  unavailable, individual material values are skipped instead of the whole export.
- Material and texture metadata failures are reported and skipped. An unsupported
  texture type such as a Cubemap no longer discards the geometry export.
- Bind-pose and shear checks on the FBX side use scale-relative, two-tier budgets.

### Added

- `BlenderSafeFbxExportOptions` with a `ValidationLevel` of `Balanced` (default),
  `Strict` (0.1.x behaviour) or `ReportOnly`, exposed in the exporter window under
  **Advanced**, plus an explicit `GeometryFailRatio` override for scripted exports.
- `BlenderSafeFbxExportResult.Warnings`, surfaced in the completion dialog and the
  Unity console.
- BlendShape channels defined more than once on a mesh are exported under unique
  names instead of being silently merged into one channel.

### Fixed

- BlendShape channels with in-between frames are flattened onto their full-weight
  frame and reported, instead of refusing the export outright.
- A skinned mesh whose Transform carries child objects is separated onto a dedicated
  mesh node automatically, instead of asking the user to restructure the hierarchy.
- Vertices with negative or zero total bone weight are reported and exported exactly
  as Unity displays them, instead of aborting the export.
- A vertex left with no usable bone influence is pinned to its baked rest position
  rather than producing a mesh FBX cannot express.

### Documentation

- Clarified that the exporter is designed for VRChat avatar authoring workflows.
- Improved GitHub discovery metadata and Unity-to-Blender search terminology.
- Documented the deviation budgets and what each measured number means.

## [0.1.0] - 2026-08-18

### Added

- Initial public source release.
- Export of Modular Avatar Manual Bake results to Blender-compatible binary FBX.
- Preservation of current bone transforms as a unified FBX rest pose.
- Preservation of skin weights, single-frame BlendShapes, BlendShape frame deltas, and current weights.
- Unit-scale skeleton normalization with visible-pose validation.
- Embedded material texture collection, including package and generated 2D textures.
- Standard FBX material-channel approximation plus Unity texture metadata.
- NDMF NaNimation deletion conversion with explicit culling statistics.
- Mesh/bone node separation for FBX's single-node-attribute limitation.
- Control-point disambiguation for Unity FBX Exporter 4.2.1 BlendShape correctness.
- Saved-FBX bind-pose, default-pose, channel-count, and reopen validation.
- Version-pinned Autodesk FBX SDK compatibility adapter.
- Transactional output replacement and explicit overwrite policy.
- Inactive temporary clone workflow that leaves source objects unchanged.
- EditMode tests built entirely from generated, license-safe data.
- English and Simplified Chinese documentation.
- Ready-to-import `.unitypackage` distribution through GitHub Releases.
