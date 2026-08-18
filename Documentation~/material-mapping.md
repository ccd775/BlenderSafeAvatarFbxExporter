# Material and texture mapping

The exporter does not reproduce Unity shader code in Blender. It preserves source information and maps a useful subset to standard FBX material properties. This includes common VRChat avatar workflows that use shaders such as lilToon, but it is not a shader converter.

## Standard channels

The following Unity property families are recognized when present and enabled:

- Diffuse/base color: `_MainTex`, `_BaseMap`, `_BaseColorMap`
- Normal: `_BumpMap`, `_NormalMap`
- Emission: `_EmissionMap`
- Alpha mask: `_AlphaMask`
- Metallic/smoothness scalar semantics

Common feature toggles such as `_UseEmission`, `_UseBumpMap`, `_UseReflection`, `_TransparentMode`, and `_AlphaMaskMode` are respected when available. A texture property existing on a shader does not by itself mean the feature is enabled.

## Custom metadata

Every collected material texture binding is also stored as a user property named:

```text
UnityTexture_<sanitized Unity property name>
```

Materials also receive:

```text
UnityMaterialName
UnityShaderName
UnityRenderType
```

Blender may warn that custom texture links are ignored during automatic material construction. This does not mean the embedded image is missing; non-standard links are metadata for manual or scripted shader reconstruction.

## Texture sources

- `Assets/`: resolved directly from the Unity project.
- `Packages/`: resolved through `PackageInfo.resolvedPath`.
- Generated `Texture2D`/`RenderTexture`: converted to PNG.
- Generated Cubemap, Texture Array, and other non-2D types: rejected with a clear error.

## HDR and alpha

FBX template Color properties clamp RGB to `[0,1]`. HDR color hue is normalized to a representable value and scalar properties are limited to safe ranges. For opaque or mask-driven diffuse textures whose source can contain alpha, a standard RGB PNG variant may be generated. Alpha-mask bindings can receive a grayscale/alpha PNG variant for Blender compatibility.
