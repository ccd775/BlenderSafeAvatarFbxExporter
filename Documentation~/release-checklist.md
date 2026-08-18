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
- [ ] Repository contains no `.unity`, `.prefab`, `.fbx`, `.blend`, or `.vrm` fixture files.
- [ ] Third-party notices remain accurate.
- [ ] GitHub source-validation workflow passes.
- [ ] Tag and GitHub release notes match the changelog.
