# TerrainModule / LandscapeModule

## Unity Terrain System with Houdini Integration

[English](./README.md) | [简体中文](./README-CN.md)

This module provides a non-destructive, layered terrain/landscape system for Unity, inspired by Unreal Engine's Landscape system with edit layers. It integrates with Houdini Engine for Unity to enable procedural terrain workflows.

## Status

⚠️ **Documentation Phase**: This directory currently contains architecture documentation and design specifications ported from the Houdini-Terrain-Layers prototype repository. The actual runtime code will be integrated into UGF in future updates.

## Key Features (Planned)

- **Non-destructive Edit Layers**: Multiple terrain layers that can be edited independently without destroying other layers' data
- **GPU-resident Blending**: Layers combine via RenderTexture ping-pong for performance
- **Houdini Integration**: Translate Houdini heightfield volumes into Unity terrain edit layers
- **World Partition Support**: Large terrain split into streaming proxies for efficient loading
- **Runtime Streaming**: Distance-based tile loading/unloading for open-world scenarios

## Documentation

### Architecture & Design

- [Architecture Overview](./Architecture.md) - System design, data model, cook pipeline
- [架构概述（中文）](./Architecture-CN.md) - 系统设计、数据模型、Cook 管线
- [Unreal Comparison (CN)](./Unreal-Comparison-CN.md) - Detailed comparison with Unreal Engine's implementation

### Developer Guide

- [Packaging & Porting Guide](./Packaging.md) - How to port validated source and create distributable releases
- [打包与移植指南（中文）](./Packaging-CN.md) - 如何移植已验证的源码并创建可分发版本

## Design Goals

1. **Non-destructive**: Re-cooking a Houdini asset or editing one layer must never destroy another layer's data
2. **GPU-resident blending**: Layers combine via RenderTexture ping-pong, not repeated CPU `TerrainData.SetHeights` calls
3. **Houdini as producer, not dependency**: The core landscape engine must compile and function with zero Houdini Engine references
4. **Traceable to Unreal**: Field/type names mirror Unreal Engine's HoudiniEngine plugin for easy cross-reference

## Integration Status

| Component | Status | Location |
|-----------|--------|----------|
| Documentation | ✅ Migrated | `Documentation/TerrainModule/` |
| Runtime Code | 🔄 Planned | `Runtime/TerrainModule/` (future) |
| Editor Tools | 🔄 Planned | `Editor/TerrainModule/` (future) |
| Samples | 🔄 Planned | `Samples~/TerrainDemo/` (future) |

## Related Repositories

This module's architecture was originally prototyped in the [Houdini-Terrain-Layers](https://github.com/hollow-tech/Houdini-Terrain-Layers) repository. The documentation has been migrated here; runtime code integration is planned for future releases.

## License

MIT License
