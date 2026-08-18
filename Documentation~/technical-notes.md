# Technical notes

## Why a normal FBX export is not sufficient

A VRChat avatar assembled with Modular Avatar Manual Bake can combine meshes that were authored against different skeleton rest poses. Unity can preserve this by storing a separate bind-pose array on each Mesh. Blender requires one edit-bone rest matrix per Armature bone, so a direct Unity-to-Blender FBX export may import with shifted meshes, giant helper bones, or incorrect deformation.

The exporter creates a temporary clone, evaluates the current visible pose, bakes every skinned mesh into avatar-root space, and rebuilds one consistent set of bind poses. The current bone transforms therefore become the exported rest pose.

## BlendShape reconstruction

For each source BlendShape frame, the exporter temporarily applies the frame weight, calls `SkinnedMeshRenderer.BakeMesh`, transforms the result to avatar-root space, and stores the difference from the zero-BlendShape baked mesh. Current renderer BlendShape weights are restored in the FBX `DeformPercent` properties during post-processing.

Multiple in-between frames in one BlendShape channel are rejected because Blender's FBX importer does not preserve that representation reliably.

## Control-point disambiguation

Unity FBX Exporter 4.2.1 deduplicates control points by the base `Vector3` position. Two coincident Unity vertices may intentionally have different BlendShape deltas. If merged, one delta silently overwrites the other even though the channel count remains correct.

Before the Unity FBX export step, duplicate base positions on BlendShape meshes are separated by deterministic one-ULP adjustments. The maximum adjustment is measured and rejected if it exceeds the geometry error budget.

## Renderer hosted on a bone

An FBX node can hold only one node attribute. Unity FBX Exporter first attaches a Mesh and can later replace it with a Skeleton attribute when the same Transform is also used as a bone. The temporary clone moves that renderer to a zero-transform child named `__Mesh`, leaving the source hierarchy untouched.

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

Imported template Number properties in Autodesk FBX SDK for Unity 4.2.1 reject some generic `FbxProperty.Set(float)` calls. A small isolated adapter reopens the native property as `FbxPropertyDouble`. The exporter hard-pins both required packages to 4.2.1 so an SDK update fails clearly instead of using an unverified private binding layout.

## Source and output safety

The selected object is instantiated beneath an inactive, hidden, non-saving container. All destructive operations affect only this clone. Output is written to an operating-system temporary directory, validated, copied to a pending file beside the destination, then atomically replaced. Existing output is backed up and restored if Unity import fails.
