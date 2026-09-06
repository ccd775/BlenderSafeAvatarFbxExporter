# Blender-Safe VRChat Avatar FBX Exporter

![将 Unity 中 Modular Avatar Manual Bake 后的 VRChat 角色导出为 FBX 并在 Blender 中打开](https://raw.githubusercontent.com/ccd775/BlenderSafeAvatarFbxExporter/v0.2.1/Documentation~/images/blender-safe-export-overview.png)

[![Source validation](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/actions/workflows/source-validation.yml/badge.svg)](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/actions/workflows/source-validation.yml)
[![Latest release](https://img.shields.io/github/v/release/ccd775/BlenderSafeAvatarFbxExporter?label=Release)](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.md)
[![Unity 2022.3 LTS](https://img.shields.io/badge/Unity-2022.3%20LTS-black.svg)](https://unity.com/releases/editor/whats-new/2022.3.22)
[![爱发电赞助](https://img.shields.io/badge/爱发电-赞助作者-946CE6)](https://ifdian.net/a/ccd775)
[English README](README.en.md)

这是一个面向 **VRChat 角色创作者**的 Unity Editor FBX 导出工具。它会把经由 **Modular Avatar Manual Bake** 合并后的 VRChat Avatar / Prefab 导出为 Blender 兼容 FBX，并保留用户在烘焙结果上手动调整的骨骼姿势、BlendShape、蒙皮权重与贴图。

它适用于需要把 VRChat Avatar 从 Unity 带回 Blender 继续编辑，或将 Modular Avatar 合并结果导出为 FBX 的工作流。Manual Bake 结果在 Unity 中可能显示正常，但多个蒙皮网格会依赖互相冲突的 bind pose、缩放补偿骨、重合控制点或特殊 FBX 节点结构，直接导出后便可能在 Blender 中出现骨骼飞远、网格偏移、BlendShape 丢失或形变损坏。

## 适用工作流

- 在 Unity 中使用 Modular Avatar 组合 VRChat 角色、服装、头发与配件。
- 对 Manual Bake 结果手动调整骨骼姿势或 BlendShape 后再导出。
- 需要在 Blender 中继续编辑合并后的角色，同时保留可编辑骨架、蒙皮权重和形态键。

> 本项目是面向 VRChat 创作者的非官方社区工具，与 VRChat Inc. 无隶属或官方关联。它不会上传 Avatar，也不调用 VRChat API。

## 配套 FBX 后处理工具

我们还提供一个面向导出后 FBX 的独立优化仓库，用于**合并与整理骨架及蒙皮权重、进行安全拓扑清理，并可选择保留原 UV 或构建和重烘焙语义 Atlas**。本仓库专注于从 Unity 安全导出，进一步的模型优化由该配套工具完成。

**配套工具：** [VRChatAvatarFBXOptimizer](https://github.com/ccd775/VRChatAvatarFBXOptimizer)

## 已包含的功能

- 将当前骨骼变换烘焙为统一、可编辑的 Blender Rest Pose。
- 保留蒙皮权重与 Armature。
- 保留 BlendShape 通道、逐顶点 delta 及当前非零权重（目标形状统一归一化到满权重 100）。
- 保留用户在 Manual Bake 结果上手调的骨骼位置/旋转和 BlendShape 数值。
- 将缩放补偿骨架标准化为单位缩放，同时保持可见姿势。
- 将材质贴图内嵌进 FBX。
- 把常见的 Diffuse、Normal、Emission、Alpha、Metallic、Smoothness 语义映射到标准 FBX 材质通道。
- 用 `UnityTexture_<属性名>` 自定义元数据保存全部 Unity 材质贴图关联，包括非标准 Shader 属性。
- 支持生成的 `Texture2D` / `RenderTexture`，以及位于 `Packages/` 中的贴图。
- 正确处理 NDMF NaNimation 删除骨：裁掉受影响 primitive，而不是让已隐藏身体重新出现。
- 修复 Unity FBX Exporter 4.2.1 合并重合控制点而静默破坏 BlendShape 的问题。
- 修复 Renderer Transform 同时作为骨骼、或带有子对象时无法规格化的问题。
- 保存后重新打开 FBX，并验证骨架、bind pose 与通道数量。
- 采用事务式覆盖：新 FBX 验证成功前不会替换旧文件。
- 只操作临时、非激活克隆，不修改选中的角色、Prefab 或源资源。

## 环境要求

- Unity **2022.3 LTS**（已验证 2022.3.22f1）
- Unity FBX Exporter `com.unity.formats.fbx` **4.2.1**（已验证版本）
- Autodesk FBX SDK `com.autodesk.fbx` **4.2.1**（由 FBX Exporter 间接安装）
- 推荐 Blender **4.2 LTS**（已验证 Blender 4.2.23）
- 所有源 Mesh 必须开启 **Read/Write**

其他版本的 FBX Exporter / FBX SDK 会以警告方式放行；只有缺少包体或版本低于 4.1.0 才会拒绝导出。使用未验证版本时，请先在 Blender 中确认导出结果。

Modular Avatar、NDMF、VRChat SDK 和 lilToon **不是编译依赖**。Modular Avatar 只是生成目标输入的工作流。

## 安装

### 推荐：GitHub Release 的 `.unitypackage`

1. 使用 Unity **2022.3 LTS** 打开目标项目。
2. 在 **Window > Package Manager** 中，从 Unity Registry 安装 **FBX Exporter 4.2.1**。
3. 从 [v0.2.1 Release](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/releases/tag/v0.2.1) 下载 [`BlenderSafeAvatarFbxExporter-v0.2.1.unitypackage`](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/releases/download/v0.2.1/BlenderSafeAvatarFbxExporter-v0.2.1.unitypackage)。
4. 双击该文件，或在 Unity 中使用 **Assets > Import Package > Custom Package...**。
5. 保持全部文件勾选并点击 **Import**，等待 Editor 程序集编译完成。

> `.unitypackage` 只包含本工具，不包含 Unity FBX Exporter 或 Autodesk FBX SDK。必须先安装 FBX Exporter 4.2.1；Autodesk FBX SDK 4.2.1 会随其间接安装。

如果项目中已有旧版或通过源码安装的副本，请先确认没有第二份 `Assets/BlenderSafeAvatarFbxExporter`，避免重复程序集。

### 源码安装

也可以下载或克隆本仓库，并将仓库完整放到：

```text
Assets/BlenderSafeAvatarFbxExporter
```

也可在该路径使用 Git submodule。完成后等待 Unity 编译 Editor 程序集。

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
    new BlenderSafeFbxExportOptions
    {
        EmbedAllMaterialTextures = true,
        OverwriteExisting = false,
        ValidationLevel = BlenderSafeFbxValidationLevel.Balanced
    });

foreach (var warning in result.Warnings)
{
    Debug.Log(warning);
}
```

程序接口默认拒绝覆盖现有文件；Editor UI 会先询问用户，再显式允许覆盖。

## 校验与偏差预算

导出过程会测量四项几何偏差：姿势烘焙、骨架标准化、bind pose 统一、控制点去重。每项都按被测几何体自身的尺寸归一化，因此以厘米为单位建模的角色和以米为单位的角色适用同一套标准。

- 偏差低于报告阈值：静默通过。
- 介于报告阈值与中止阈值之间：写入 `result.Warnings` 和 Console，FBX 正常输出。
- 超过中止阈值：拒绝导出，并给出最差控制点、相关 BlendShape 以及常见成因。

窗口的 **Advanced > Validation** 提供三档：`Balanced`（默认）、`Strict`（等同 0.1.x）、`Report only`（几何偏差永不中止）。

姿势烘焙偏差的含义很具体：**导出的基础网格与 BlendShape 目标本身始终是精确的**，该数值只描述"形态键取默认值时的外观"与 Unity 的差距。结构性问题（非有限数值、网格无法重建、反射/奇异变换、层级外骨骼）不受预算控制，任何档位下都会中止。

## 重要限制

- 一个 BlendShape 含多个 in-between frame 时，会压平为满权重帧并给出警告（Blender 每个通道只保留一个目标形状）。
- 所有加权骨骼与可选 `rootBone` 都必须位于所选角色层级内。
- Mesh 必须可读，且 bind pose 数量必须与骨骼数量一致。
- 权重为负或总和为零的顶点会按 Unity 实际显示的样子导出，并给出警告。
- Animator、Animation 与 Unity Constraint 不会被导出；它们会从临时副本移除。
- NaNimation 删除转换会移除所有接触已删除顶点的 primitive；成功窗口会显示受影响数量。
- Renderer Transform 同时作为骨骼、或带有子对象时，会自动拆分到独立的 `__Mesh` 节点。
- 反射、奇异或无法表示为纯 TRS 的骨变换会被拒绝。
- Unity FBX Exporter 会处理所选根节点下的完整层级，因此支持的静态 Mesh、Camera、Light 也可能进入 FBX。
- 生成的 Cubemap、Texture Array 等非二维纹理会跳过并给出警告。
- 材质只是近似映射，不是 Shader 转换器。
- 目前只在 Windows 上完成认证。

更多细节参见：

- [技术说明](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/blob/v0.2.1/Documentation~/technical-notes.md)
- [验证记录](https://github.com/ccd775/BlenderSafeAvatarFbxExporter/blob/v0.2.1/Documentation~/validation.md)

## 测试

`Tests/Editor` 中包含 EditMode 测试，覆盖：

- 手调骨骼旋转保留；
- 非零 BlendShape 权重保留；
- 生成贴图内嵌；
- 保存后 FBX 重开与通道验证；
- 源对象不被修改；
- 未确认时拒绝覆盖旧文件；
- 带子对象的 Renderer 自动拆分；
- in-between BlendShape 压平并按满权重重新标定 `DeformPercent`；
- 正常角色只报告偏差、不中止导出。

## 许可证

本仓库原创代码使用 [MIT License](LICENSE.md)。Unity FBX Exporter 与 Autodesk FBX SDK 只是依赖，不随本仓库重新授权或分发。详见 [Third Party Notices.md](Third%20Party%20Notices.md)。
