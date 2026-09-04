# Technical notes

## Why a normal FBX export is not sufficient

A VRChat avatar assembled with Modular Avatar Manual Bake can combine meshes that were authored against different skeleton rest poses. Unity can preserve this by storing a separate bind-pose array on each Mesh. Blender requires one edit-bone rest matrix per Armature bone, so a direct Unity-to-Blender FBX export may import with shifted meshes, giant helper bones, or incorrect deformation.

The exporter creates a temporary clone, evaluates the current visible pose, bakes every skinned mesh into avatar-root space, and rebuilds one consistent set of bind poses. The current bone transforms therefore become the exported rest pose.

## BlendShape reconstruction

For each source BlendShape channel, the exporter temporarily applies a measurement weight, calls `SkinnedMeshRenderer.BakeMesh`, transforms the result to avatar-root space, and stores the difference from the zero-BlendShape baked mesh.

Every exported target shape carries a full weight of 100, and the recorded `DeformPercent` is rescaled to match. A channel whose source frame weight was not 100 therefore round-trips exactly, and a `DeformPercent` always means the same thing regardless of how the source mesh was authored. A single-frame channel is linear in its weight, so it is measured at 100 directly, which stays well conditioned even when the source frame weight is very small.

Unity's "Clamp BlendShapes (Deprecated)" player setting is applied to the recorded weight, because with that setting on the editor displays a clamped value while an unclamped FBX would extrapolate past it.

Blender's FBX importer keeps one target shape per channel. In-between frames are therefore flattened onto the channel's full-weight frame, measured at that frame so the exported target is the authored full shape rather than an extrapolation past it. The flattening is reported, and the pose-bake deviation quantifies what it actually costs.

## Deviation budgets

Four stages measure how far the rebuilt avatar drifts from what Unity displays: pose baking, skeleton normalization, bind-pose unification, and control-point disambiguation.

Each deviation is divided by the size of the geometry it was measured on, so an avatar authored in centimetres is judged by the same standard as one authored in metres. The dimensionless result is compared against two budgets: above the first it is reported as a warning, above the second the export aborts. `Strict` reproduces the 0.1.x thresholds, `Balanced` is the default, and `ReportOnly` never aborts on a deviation.

The pose-bake number has a precise meaning: it is how far the exported base geometry plus BlendShape targets, evaluated at the recorded weights, sits from the mesh Unity displays. The base geometry and the target shapes themselves are exact regardless of its value, so the deviation only describes the shape at the default BlendShape weights.

It is measured only over control points that a triangle references. Orphan points and geometry culled by NaNimation stay in the vertex array but can never be seen, so they are reported separately instead of vetoing the export.

Structural faults are not deviations and are never budgeted: non-finite values, a mesh that could not be rebuilt with a comparable vertex or channel count, reflected or singular transforms, and bones outside the avatar hierarchy always abort.

## Control-point disambiguation

Unity FBX Exporter 4.2.1 deduplicates control points by the base `Vector3` position. Two coincident Unity vertices may intentionally have different BlendShape deltas. If merged, one delta silently overwrites the other even though the channel count remains correct.

Before the Unity FBX export step, duplicate base positions on BlendShape meshes are separated by deterministic one-ULP adjustments. The maximum adjustment is measured and rejected if it exceeds the geometry error budget.

## Renderer that cannot be normalized in place

An FBX node can hold only one node attribute. Unity FBX Exporter first attaches a Mesh and can later replace it with a Skeleton attribute when the same Transform is also used as a bone. A renderer Transform that carries children of its own cannot have its transform zeroed either, because the children would move with it.

In both cases the temporary clone moves the renderer to a zero-transform child named `__Mesh`, leaving the source hierarchy untouched.

## Skeleton normalization

Scale-compensated helper hierarchies can import into Blender as extremely long or distant bones. The exporter stores each used bone's world position and rotation, sets local scale to one in hierarchy order, restores world pose, and rebuilds mesh bind poses. It compares baked vertices before and after normalization.

Reflected, singular, non-finite, or non-TRS transforms are rejected rather than approximated silently.

## NDMF NaNimation deletion

NDMF can encode deleted geometry through non-finite transforms on specially named deletion bones. Replacing NaN scale with a finite value makes deleted body geometry visible again. The exporter identifies only the recognized deletion-bone pattern, culls every primitive touching an affected vertex, remaps finite bones, and removes the invalid leaf transforms. The result reports affected vertices and primitives.

## Materials and textures

All material texture properties are recorded as FBX metadata. Common standard channels are selected according to material feature toggles and render semantics. Physical source files are embedded when suitable; generated 2D textures are converted to PNG. Arbitrary Unity shader graphs are not converted.

HDR colors are normalized before writing to FBX Color properties because those template properties clamp to `[0,1]`. Numeric factors are range-limited where needed to avoid native SDK failures.

## FBX SDK post-processing

After Unity FBX Exporter writes its intermediate file, Autodesk FBX SDK is used to:

- align bone node defaults with bind-pose matrices;
- restore current BlendShape weights;
- apply material semantics and texture metadata;
- embed textures;
- save binary FBX;
- reopen and validate the saved file.

Imported template Number properties in Autodesk FBX SDK for Unity 4.2.1 reject some generic `FbxProperty.Set(float)` calls. A small isolated adapter reopens the native property as `FbxPropertyDouble`. Because the adapter reaches into a private binding layout, it is treated as an optional fallback: when it is unavailable the affected material values are skipped with a warning rather than failing the export.

Both packages are verified against 4.2.1. A different version is accepted with a warning; only a missing package, or one older than 4.1.0, is refused. Material and texture metadata is side data, so a node that cannot be located, a material slot that does not line up, an unsupported texture type, or a rejected property write is reported and skipped instead of discarding the geometry export.

## Source and output safety

The selected object is instantiated beneath an inactive, hidden, non-saving container. All destructive operations affect only this clone. Output is written to an operating-system temporary directory, validated, copied to a pending file beside the destination, then atomically replaced. Existing output is backed up and restored if Unity import fails.

## Release package format

The release `.unitypackage` must be produced by Unity's own **Assets > Export Package**. The format is a gzipped tar holding one directory per asset GUID, each containing `pathname`, `asset.meta` and, for non-folder assets, `asset`. That description is complete enough to reproduce with a generic tar writer and still get a file Unity refuses, because its reader is stricter than the format:

- Entries must not carry PAX extended headers. A writer that defaults to PAX — Python's `tarfile` does — prefixes every entry with a `././@PaxHeader` record, and the reader takes the first path segment of an entry as an asset GUID.
- Every GUID must have its own directory entry. An archive containing only the files inside those directories enumerates as empty.
- The gzip stream's stored original file name must be `archtemp.tar`, which is the name Unity's exporter writes and its importer looks for. Storing the package's own file name is enough on its own to make the import a no-op.

None of these produce an error. Unity raises `importPackageCompleted`, the progress dialog closes, and nothing is installed. Verifying a release package therefore means importing it into a clean project and counting the files that arrive; a successful-looking import proves nothing.
