# Changelog

All notable changes to this project are documented in this file.

## [0.1.0] - 2026-08-02

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
