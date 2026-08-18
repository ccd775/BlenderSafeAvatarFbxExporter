# Contributing

Contributions and reproducible bug reports are welcome.

## Development baseline

- Unity 2022.3 LTS
- `com.unity.formats.fbx` 4.2.1
- `com.autodesk.fbx` 4.2.1
- Blender 4.2 LTS for import verification

The Autodesk property adapter is deliberately pinned to 4.2.1. A dependency-version change must include adapter review, saved-FBX reopen tests, and Blender import regression results.

## Before submitting a change

1. Keep all runtime behavior Editor-only.
2. Run the EditMode tests in `Tests/Editor`.
3. Export a generated synthetic avatar with a manually rotated bone, a nonzero BlendShape value, and an embedded texture.
4. Reopen the saved FBX through Autodesk FBX SDK.
5. Import the result into a declared Blender version.
6. For geometry or BlendShape changes, compare corresponding control points by index; bounds and nearest-surface tests are not sufficient.
7. Confirm the source GameObject, Prefab, Scene, Mesh, and Material assets are unchanged.
8. Confirm failed output replacement restores the previous FBX.

## Test data policy

Do not submit paid, private, or redistributability-unclear avatar assets. Prefer fixtures created entirely in test code. A fixture copied from an avatar project must have an explicit redistribution license and attribution.

## Scope

Keep the exporter focused on final baked VRChat avatar and general skinned-avatar hierarchies. Optional integrations with Modular Avatar, NDMF, VRChat SDK, or shader packages should remain isolated so the core Editor assembly does not acquire those compile-time dependencies.
