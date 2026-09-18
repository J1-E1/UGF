# TerrainModule / LandscapeModule

## Unity 地形系统与 Houdini 集成

[English](./README.md) | 简体中文

本模块为 Unity 提供非破坏性的分层地形/Landscape 系统，灵感来自 Unreal Engine 的 Landscape 系统及其编辑层功能。它与 Houdini Engine for Unity 集成，支持程序化地形工作流。

## 状态

⚠️ **文档阶段**：本目录当前包含从 Houdini-Terrain-Layers 原型仓库移植过来的架构文档和设计规范。实际的运行时代码将在未来的更新中集成到 UGF。

## 核心特性（计划中）

- **非破坏性编辑层**：多个地形层可以独立编辑，不会破坏其他层的数据
- **GPU 常驻混合**：图层通过 RenderTexture 乒乓合成，性能优异
- **Houdini 集成**：将 Houdini heightfield volume 翻译成 Unity 地形编辑层
- **World Partition 支持**：大型地形拆分成流式代理（streaming proxies），高效加载
- **运行时流式加载**：基于距离的瓦片加载/卸载，适用于开放世界场景

## 文档

### 架构与设计

- [Architecture Overview](./Architecture.md) - 系统设计、数据模型、cook 管线（英文）
- [架构概述](./Architecture-CN.md) - 系统设计、数据模型、Cook 管线
- [与 Unreal 的对比](./Unreal-Comparison-CN.md) - 与 Unreal Engine 实现的详细对比

### 开发者指南

- [Packaging & Porting Guide](./Packaging.md) - 如何移植已验证的源码并创建可分发版本（英文）
- [打包与移植指南](./Packaging-CN.md) - 如何移植已验证的源码并创建可分发版本

## 设计目标

1. **非破坏性**：重新 cook 一个 Houdini 资产、或编辑某一个编辑层，绝不能破坏另一层的数据
2. **GPU 常驻混合**：图层通过 `RenderTexture` 乒乓合成，而不是反复调用 CPU 端的 `TerrainData.SetHeights`
3. **Houdini 是生产者，不是引擎的依赖**：核心 Landscape 引擎必须在零 Houdini Engine 引用的情况下编译并运行
4. **可追溯到 Unreal 的真实实现**：字段/类型名镜像 Unreal Engine HoudiniEngine 插件，方便交叉参考

## 集成状态

| 组件 | 状态 | 位置 |
|------|------|------|
| 文档 | ✅ 已迁移 | `Documentation/TerrainModule/` |
| 运行时代码 | 🔄 计划中 | `Runtime/TerrainModule/`（未来） |
| 编辑器工具 | 🔄 计划中 | `Editor/TerrainModule/`（未来） |
| 示例 | 🔄 计划中 | `Samples~/TerrainDemo/`（未来） |

## 相关仓库

本模块的架构最初在 [Houdini-Terrain-Layers](https://github.com/hollow-tech/Houdini-Terrain-Layers) 仓库中进行原型验证。文档已迁移至此；运行时代码集成计划在未来版本中进行。

## 许可证

MIT 许可证
