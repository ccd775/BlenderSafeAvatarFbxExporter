# Blender-Safe Avatar FBX Exporter

[English README](README.md)

这是一个 Unity Editor 工具，用于把 **Modular Avatar Manual Bake 后的 Avatar/Prefab 实例**导出为 Blender 兼容的 FBX，并保留用户在烘焙结果上手动调整的状态。

它主要解决以下问题：Manual Bake 结果在 Unity 中显示正常，但多个蒙皮网格可能依赖互相冲突的 bind pose、缩放补偿骨、重合控制点或特殊 FBX 节点结构，直接导出后在 Blender 中会出现骨骼飞远、网格偏移、BlendShape 丢失或形变损坏。

## 已包含的功能

- 将当前骨骼变换烘焙为统一、可编辑的 Blender Rest Pose。
- 保留蒙皮权重与 Armature。
- 保留单帧 BlendShape 通道、逐顶点 delta 及当前非零权重。
- 保留用户在 Manual Bake 结果上手调的骨骼位置/旋转和 BlendShape 数值。
- 将缩放补偿骨架标准化为单位缩放，同时保持可见姿势。
- 将材质贴图内嵌进 FBX。
- 把常见的 Diffuse、Normal、Emission、Alpha、Metallic、Smoothness 语义映射到标准 FBX 材质通道。
- 用 `UnityTexture_<属性名>` 自定义元数据保存全部 Unity 材质贴图关联，包括非标准 Shader 属性。
- 支持生成的 `Texture2D` / `RenderTexture`，以及位于 `Packages/` 中的贴图。
- 正确处理 NDMF NaNimation 删除骨：裁掉受影响 primitive，而不是让已隐藏身体重新出现。
- 修复 Unity FBX Exporter 4.2.1 合并重合控制点而静默破坏 BlendShape 的问题。
- 修复 Renderer Transform 同时作为骨骼时，FBX Skeleton 覆盖 Mesh 的问题。
- 保存后重新打开 FBX，并验证骨架、bind pose 与通道数量。
- 采用事务式覆盖：新 FBX 验证成功前不会替换旧文件。
- 只操作临时、非激活克隆，不修改选中的角色、Prefab 或源资源。

## 环境要求

首版为了保证 FBX SDK 适配稳定，严格锁定以下版本：

- Unity **2022.3 LTS**（已验证 2022.3.22f1）
- Unity FBX Exporter `com.unity.formats.fbx` **4.2.1**
- Autodesk FBX SDK `com.autodesk.fbx` **4.2.1**（由 FBX Exporter 4.2.1 间接安装）
- 推荐 Blender **4.2 LTS**（已验证 Blender 4.2.23）
- 所有源 Mesh 必须开启 **Read/Write**

Modular Avatar、NDMF、VRChat SDK 和 lilToon **不是编译依赖**。Modular Avatar 只是生成目标输入的工作流。

## 安装

1. 打开 Unity 的 **Window > Package Manager**。
2. 从 Unity Registry 安装 **FBX Exporter 4.2.1**。
3. 将本仓库放入项目：

   ```text
   Assets/BlenderSafeAvatarFbxExporter
   ```

   也可以把仓库作为 Git submodule 添加到这个路径。
4. 等待 Unity 编译完成。

## 使用步骤

1. 使用 Modular Avatar 的 **Manual Bake**。
2. 选择生成的烘焙角色根节点，不要选择仍包含 Merge Armature 等组件的原始 Avatar。
3. 在烘焙结果上完成最后调整：
   - 移动或旋转骨骼；
   - 设置 BlendShape 权重；
   - 确认当前材质与贴图。
4. 打开 **Tools > Avatar > Blender-Safe FBX Exporter**。
   - 或右键当前选择，使用 **Avatar > Export Blender-Safe FBX...**。
5. 指定 Manual Bake 角色根节点。
6. 默认保持 **Embed all material textures** 开启。
7. 导出并在 Blender 中导入 FBX。

当前骨骼姿势会成为 FBX Rest Pose，而不是被写成动画片段。

## 贴图说明

- `Assets/` 下有实体源文件的贴图直接读取源文件。
- `Packages/` 下的贴图通过 Unity Package Manager 解析实际路径。
- 运行时生成的 `Texture2D` 和 `RenderTexture` 转换为 PNG。
- Blender 能识别的标准通道会自动连接。
- 其他 Shader 专用贴图以 FBX 自定义元数据形式保存。

本工具不会完整重建 lilToon 等 Unity Shader。图片与关联会保留，但复杂 Shader 节点通常仍需在 Blender 中手工重建。

## 代码调用

```csharp
using ccd775.AvatarFbxExporter;

var result = BlenderSafeAvatarFbxExporter.Export(
    manualBakeRoot,
    outputPath,
    embedAllMaterialTextures: true,
    overwriteExisting: false);
```

程序接口默认拒绝覆盖现有文件；Editor UI 会先询问用户，再显式允许覆盖。

## 重要限制

- 不支持一个 BlendShape 中含多个 in-between frame；遇到时会明确拒绝导出。
- 所有加权骨骼与可选 `rootBone` 都必须位于所选角色层级内。
- 每个蒙皮顶点必须有有限、非负且总和大于零的骨权重。
- Mesh 必须可读，且 bind pose 数量必须与骨骼数量一致。
- Animator、Animation 与 Unity Constraint 不会被导出；它们会从临时副本移除。
- NaNimation 删除转换会移除所有接触已删除顶点的 primitive；成功窗口会显示受影响数量。
- 普通 SkinnedMeshRenderer 若带子对象会被拒绝，以免规格化 Renderer Transform 时改变子对象；Renderer 本身同时作为骨骼的情况会自动拆分。
- 反射、奇异或无法表示为纯 TRS 的骨变换会被拒绝。
- Unity FBX Exporter 会处理所选根节点下的完整层级，因此支持的静态 Mesh、Camera、Light 也可能进入 FBX。
- 生成的 Cubemap、Texture Array 等非二维纹理暂不支持自动转换。
- 材质只是近似映射，不是 Shader 转换器。
- 0.1.0 目前只在 Windows 上完成认证。

更多细节参见：

- [技术说明](Documentation~/technical-notes.md)
- [验证记录](Documentation~/validation.md)

## 测试

`Tests/Editor` 中包含 EditMode 测试，覆盖：

- 手调骨骼旋转保留；
- 非零 BlendShape 权重保留；
- 生成贴图内嵌；
- 保存后 FBX 重开与通道验证；
- 源对象不被修改；
- 未确认时拒绝覆盖旧文件。

## 许可证

本仓库原创代码使用 [MIT License](LICENSE.md)。Unity FBX Exporter 与 Autodesk FBX SDK 只是依赖，不随本仓库重新授权或分发。详见 [Third Party Notices.md](Third%20Party%20Notices.md)。
