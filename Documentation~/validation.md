# Validation

## Public synthetic EditMode tests

The repository includes generated tests that create:

- one skinned mesh;
- one manually rotated bone;
- one BlendShape with a nonzero current weight;
- one generated texture.

The tests verify:

- the source bone transform and BlendShape value remain unchanged;
- a binary FBX is produced;
- the saved FBX reopens through Autodesk FBX SDK;
- the expected Mesh and BlendShape channel exist;
- `DeformPercent` preserves the current value;
- texture embedding succeeds;
- an existing output file is not changed without explicit overwrite permission.

No private avatar data is part of the public test suite.

## Development production regression

The exporter was also exercised against private, non-redistributable VRChat avatar Manual Bake fixtures during development. Those assets are not included in this repository.

One high-complexity fixture produced:

- 25 skinned meshes;
- one 382-bone Blender Armature;
- 378 Shape Keys;
- 10 nonzero default Shape Key values;
- 56 embedded source texture files and 130 texture-property bindings;
- 59 packed Blender image datablocks;
- zero non-finite vertices.

All 378 channels across 16 BlendShape meshes were compared by corresponding control-point index against an independent Unity `BakeMesh(true)` source baseline. The maximum Shape Key error was `9.996004e-6 m`, with zero control points exceeding `0.01 mm`. Maximum base-geometry error was `5.63852e-7 m`.

## Acceptance rules

A regression is not considered passed solely because mesh/channel counts match. Geometry and BlendShape validation should use corresponding control-point indices. Blender command-line scripts must emit an expected completion marker and report file, and their logs must be checked for tracebacks because Blender can return exit code zero after a Python exception.

Bounds-only and nearest-surface comparisons are useful diagnostics but cannot detect local vertex drift or a silently zeroed Shape Key.
