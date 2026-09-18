# 打包与移植指南

[English](./Packaging.md) | 简体中文

面向维护者的笔记：如何把验证过的源码从它最初构建的生产项目里搬出来，以及如何切出一个可分发的版本。对安装本包的终端用户无关。

## 1. 把验证过的源码移植进本仓库

大部分源码是干净的复制 + 改命名空间。少数几块是对 *vendor*（Houdini Engine for Unity 插件自己的）文件的改动，需要不同处理——在复制任何东西之前先看下面的 "Vendor patch" 行。

| 源（验证项目） | 目标（本仓库） | 改什么 |
| :--- | :--- | :--- |
| `.../Landscape/ALandscapeProxy.cs` | `Runtime/Landscape/` | 命名空间保持 `Hollow.Landscape.Core`，或改成与本包匹配——选一个，并在整个 `Runtime/Landscape` 文件夹里保持一致。 |
| `.../Landscape/ALandscape.cs` | `Runtime/Landscape/` | 同上。 |
| `.../Landscape/ALandscapeStreamingProxy.cs` | `Runtime/Landscape/` | 同上。 |
| `.../Landscape/ULandscapeInfo.cs` | `Runtime/Landscape/` | 同上。 |
| `.../Landscape/FLandscapeEditLayer.cs` | `Runtime/Landscape/` | 同上。 |
| `.../Landscape/EditLayerCombine.shader` | `Runtime/Shaders/` | 无——shader 名/路径与 C# 命名空间无关。 |
| `.../Landscape/Editor/ALandscapeEditor.cs` | `Editor/Inspectors/` | 移到本包 `Editor` asmdef 下等价于 `Hollow.Editor.Landscape` 的命名空间。 |
| `.../Landscape/Editor/GUIUtil.cs` | `Editor/Inspectors/` | 同上。 |
| `.../Landscape/Resources/`（编辑层图标 PNG） | `Editor/...`（或一个被编辑器代码识别的 `Resources` 文件夹） | 可选的自定义图标（`editlayer_lock_on` 等）。缺失时会回退到 Unity 内置图标，所以不是必需。 |
| `Plugins/HoudiniEngineUnity/Scripts/Utility/FHoudiniHeightFieldPartData.cs` | `Runtime/Translators/` | 把 `namespace HoudiniEngineUnity` → 本包的 Houdini 命名空间，并为 `HEU_PartData` 等加 `using HoudiniEngineUnity;`。 |
| `Plugins/HoudiniEngineUnity/Scripts/Utility/HEU_LandscapeUtility.cs` | `Runtime/Translators/`（拆成下面这些类型） | 这个文件目前捆了好几个类型——把它们拆出来，并**把静态工具类从 `HEU_` 前缀改名**（如 `FHoudiniLandscapeUtility`）。`HEU_` 表示"官方 SideFX 插件代码"；当它不再物理位于 `Plugins/HoudiniEngineUnity` 内部时，给本包拥有的代码保留这个前缀会误导。 |
| ↳ `FHoudiniUnityTransform`、`FHoudiniLandscapeMaterial`、`FHoudiniHeightFieldData`、`FHoudiniUnrealLandscapeTarget`、`FHoudiniLayersToUnrealLandscapeMapping` | `Runtime/Translators/` | 与上面那个文件相同的命名空间改动。 |
| `Plugins/HoudiniEngineUnity/Scripts/Asset/FHoudiniLandscapeTranslator.cs` | `Runtime/Translators/` | 同时含 `FHoudiniLandscapeCreationInfo` 和 `FHoudiniLandscapeTranslator` 静态类——只改命名空间。 |

### Vendor patch（无法"复制"——必须重新施加到用户自己的插件安装上）

这些动到的是真正属于官方 Houdini Engine for Unity 插件的文件。它们不能放进本包的 `Runtime/Translators`，因为本包不分发/不拥有那些文件。在 Houdini Engine for Unity 暴露一个真正的扩展点之前，记录确切的 diff，并让用户（或一个小安装脚本）把它施加到他们自己那份插件上：

| Vendor 文件 | 加了什么 |
| :--- | :--- |
| `Scripts/Core/HEU_Defines.cs` | `HAPI_UNITY_ATTRIB_LANDSCAPE_PIPELINE = "unity_landscape_pipeline"` 常量。 |
| `Scripts/Asset/HEU_ObjectNode.cs` | `EHoudiniLandscapePipeline` 枚举，以及 `GenerateGeometry` 里把 volume part 分派到新的非破坏性路径或既有 `ProcessVolumeParts` 的 `switch`。 |
| `Scripts/Asset/HEU_GeoNode.cs` | `_heightFieldParts` 字段、`ProcessVolumePartsInNonDestructive(...)` 方法，以及 `DestroyLandscapeCache()` 里的 `_heightFieldParts` 清理分支。 |
| `Scripts/Asset/HEU_HoudiniAsset.cs` | `_heightFieldPartCaches` 字段 + `AddHeightFieldPartCache` / `RemoveHeightFieldPartCache` 方法；以及 `AllowLandscapeModification` 开关（默认 true，对应 Unreal 的 `bIsLandscapeModification`），用于把守 "Update Existing" 模式。 |

把这些保留成单个 `.patch` 文件（如 `Documentation~/vendor.patch`），用 `git diff` 针对本包目标插件版本的干净安装生成，而不是会和真实 diff 渐渐不同步的散文式说明。

### 每个移植文件的检查清单

1. 把文件复制进上面对应的目标文件夹。
2. 按表格修正命名空间 / `using` 指令。
3. 确认它落在你预期的汇编里——`Runtime/Landscape` vs. `Runtime/Translators`（见 `Architecture-CN.md` →"设计目标" #3）。如果一个 `Runtime/Landscape` 文件最后需要某个 Houdini 类型，那是它放错了位置的信号，而不是要给那个 asmdef 加 Houdini 引用的信号。
4. 在认为移植完成之前，重跑当初验证它的那个场景——干净编译是必要条件，不是充分条件。

## 2. 构建 `.unitypackage`

这是与根 README 已记录的 UPM git-URL 安装**不同的分发渠道**——对 Asset Store 分发、或不想碰 `Packages/manifest.json` 的用户有用。你不需要二选一；两者都可以提供。

1. 打开一个装了本包的 Unity 项目（例如对本地克隆用 "Install package from disk"），并且最好**也装了 Houdini Engine for Unity**，这样任何可选的集成代码都会被纳入导出。
2. 在 Project 窗口里选中 `Runtime/`、`Editor/`，以及（若想捆 demo）`Samples~/`——或者，如果你的 Unity 版本支持直接导出 package-manager 包，就在 Package Manager 窗口里右键该包那行选 *Export Package*。
3. *Export Package...* → 取消勾选 "Include dependencies"（你不想把官方 Houdini Engine for Unity 插件意外打进导出——见根 README 的"为什么不把 Houdini Engine 一起打包"）→ 导出。
4. **输出去哪：**
   - 仅本地测试：放进 `Builds~/`（已 gitignore——无论文件多大都不会被提交）。
   - 正式发布：附到一个**打了 tag 的 GitHub Release**，而不是某个 git commit。Release 资产存在仓库 git 历史之外，所以仓库不会在每次版本升级时累积一个数 MB 的二进制。命名要可预测，如 `HoudiniTerrainLayers-0.1.0.unitypackage`。

## 3. HDA 文件放哪

Houdini Engine for Unity 从*消费方* Unity 项目内的路径加载 HDA，所以本仓库附带的任何 HDA 都得是 Unity 可导入的内容，而不是松散的 Houdini 侧数据：

- **Demo/示例 HDA**（常见情形——一个唯一目的就是演练本包编辑层管线的 HDA）：放进 `Samples~/EditLayerDemo/`，紧挨着引用它的 demo 场景。因为 `Samples~` 只有在用户从 Package Manager 显式导入示例时才会被拷进他们的项目，所以这个 HDA 不会拖累不用 demo 的人的项目。
- **任何打算作为核心功能发布的东西**：先三思——本包的职责是做一个对*任何*使用正确属性约定（`unreal_landscape_output_mode`、编辑层属性等，见 `Architecture-CN.md`）的 HDA 的通用 translator，而不是发布某个特定的地形 HDA。如果你确实需要一个（如参考实现），它多半仍属于 `Samples~/`，只是可能在 `package.json` 的 `"samples"` 数组里有自己的一条示例项。

**关于文件格式的授权说明**（`.hda` vs `.hdalc` vs `.hdanc`）：哪个扩展名合适，取决于你创作它所用的 Houdini 许可层级，以及你预期*消费方*持有的层级（如只有 Houdini Engine、没有完整 Houdini 席位的用户）。这有真实的商业授权含义，取决于你具体情况——请对照 SideFX 自己的现行条款（[Licensing](https://www.sidefx.com/Support/licensing/)、[Indie FAQ](https://www.sidefx.com/faq/indie-new/)）确认，而不是想当然；本文档不能替代那个。
