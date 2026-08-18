# Release checklist

- [ ] Version constant and changelog agree.
- [ ] Unity 2022.3 LTS opens and compiles the source with only declared dependencies.
- [ ] All EditMode tests pass.
- [ ] Synthetic FBX reopens through Autodesk FBX SDK.
- [ ] Synthetic FBX imports in Blender 4.2 LTS.
- [ ] At least one complex Manual Bake fixture passes mesh/channel and per-control-point validation.
- [ ] Source Scene/Prefab dirty state is unchanged after export.
- [ ] Existing-output refusal and explicit transactional overwrite both pass.
- [ ] Output contains no private avatar assets, absolute staging paths, or unexpected external texture dependencies.
- [ ] Release `.unitypackage` imports into a clean Unity 2022.3 LTS project after FBX Exporter 4.2.1 is installed.
- [ ] Release `.unitypackage` contains only the tool, its documentation, and license notices; external dependencies are not bundled.
- [ ] Repository contains no `.unity`, `.prefab`, `.fbx`, `.blend`, `.vrm`, or `.unitypackage` fixture/artifact files.
- [ ] Third-party notices remain accurate.
- [ ] GitHub source-validation workflow passes.
- [ ] Tag and GitHub release notes match the changelog.
