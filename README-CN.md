# Unity Game Framework (UGF)

## Unity 项目的模块化游戏框架

![Status](https://img.shields.io/badge/status-开发中-orange.svg)
![Unity Version](https://img.shields.io/badge/Unity-6.0%2B-blue.svg)
![C#](https://img.shields.io/badge/C%23-9.0%20%2F%20.NET%20Standard%202.1-178600.svg)
![License](https://img.shields.io/badge/License-MIT-green.svg)

[English](./README.md) | 简体中文

一个轻量级、模块化的 Unity 游戏框架，提供常用游戏系统，耦合度极低。每个模块可以独立使用，也可以组合使用以实现完整的游戏功能。

## 目录

- [系统要求](#-系统要求)
- [安装](#-安装)
- [项目结构](#-项目结构)
- [模块](#-模块)
- [文档](#-文档)
- [许可证](#-许可证)

## 📋 系统要求

| 组件 | 版本 | 说明 |
| :--- | :--- | :--- |
| Unity | 6.0+ | 在 Unity 6.0 及更新版本上测试。目标 .NET Standard 2.1 / C# 9.0。 |
| UniTask | 2.5.0+ | 异步操作必需。通过 Package Manager 或 UPM 安装。 |
| Addressables | 2.0.0+ | DataModule 资源加载和运行时管理必需。 |

## 💽 安装

**方式 1：Unity Package Manager**（通过 git URL）

1. Window -> Package Manager -> + -> Install package from git URL...
2. 粘贴仓库 URL

**方式 2：本地克隆**（用于开发）

克隆后通过 Package Manager -> Install package from disk... 安装

## 📂 项目结构

```
UGF/
├── DataModule/           # 配置数据管理（Luban 表格、Addressables）
│   └── DataManager.cs
├── Runtime/              # 核心运行时系统（计划中）
├── Editor/               # 编辑器工具和检视器（计划中）
└── Documentation/        # 架构文档和 API 参考
```

## 🎮 模块

### DataModule

配置数据管理系统，通过 Addressables 处理 Luban 生成的表格，支持优化的二进制格式。

**核心特性：**
- **加载策略**：AlwaysLoaded（初始化时加载）/ OnDemand（首次访问时懒加载）
- **分组管理**：按组注册表格，统一加载/卸载以优化内存
- **双格式支持**：运行时使用二进制（.bytes），调试时回退到 JSON
- **查询 API**：按 ID 获取、GetAll、支持谓词和 LINQ 的 FindAll
- **异步加载**：基于 UniTask，支持并发分组加载和取消
- **性能优化**：反射缓存、批量反序列化、FStreamableManager 集成
- **类型安全**：泛型方法，编译期类型检查

**API 概览：**

```csharp
// 初始化时注册组（通常在引导程序中）
DataManager.Instance.RegisterGroup("CoreConfig", 
    TableLoadPolicy.AlwaysLoaded, 
    AssetFormat.BinWithJsonFallback,
    (typeof(ChapterData), "Config/Tables/Chapter"),
    (typeof(PlayerData), "Config/Tables/Player")
);

// 游戏启动时初始化 - 加载所有 AlwaysLoaded 组
await DataManager.Instance.InitializeAsync();

// 按 ID 获取单个条目
var chapter = DataManager.Instance.Get<ChapterData>(101);
if (chapter != null)
{
    Debug.Log($"章节: {chapter.Name}");
}

// 使用谓词查询（LINQ 兼容）
var unlocked = DataManager.Instance.FindAll<ChapterData>(c => c.IsUnlocked && c.Level > 5);

// 获取某类型的所有条目
var allChapters = DataManager.Instance.GetAll<ChapterData>();

// 首次使用前预加载按需表格
await DataManager.Instance.EnsureLoadedAsync<WeaponData>();

// 手动加载整个组
await DataManager.Instance.LoadGroupAsync("DynamicContent");
```

**使用模式：**

```csharp
// 模式 1：早期注册 + 自动加载
public class GameBootstrap : MonoBehaviour
{
    async void Start()
    {
        DataManager.Instance.RegisterGroup("Core", 
            TableLoadPolicy.AlwaysLoaded, 
            AssetFormat.BinWithJsonFallback,
            (typeof(ConfigData), "Config/Tables/Config")
        );
        
        await DataManager.Instance.InitializeAsync();
        Debug.Log("核心配置已加载");
    }
}

// 模式 2：大型数据集的按需加载
public class WeaponShop : MonoBehaviour
{
    async void OnShopOpened()
    {
        await DataManager.Instance.EnsureLoadedAsync<WeaponData>();
        var weapons = DataManager.Instance.FindAll<WeaponData>(w => w.Rarity >= 3);
        DisplayWeapons(weapons);
    }
}

// 模式 3：场景基于分组的加载
public class LevelLoader : MonoBehaviour
{
    async UniTask LoadLevel(int levelId)
    {
        await DataManager.Instance.LoadGroupAsync($"Level{levelId}");
        var levelData = DataManager.Instance.Get<LevelData>(levelId);
        // 使用加载的数据设置关卡
    }
}
```

**[-> 完整 DataModule 文档](./Documentation/DataModule-CN.md)**

## 📚 文档

- [DataModule API 参考](./Documentation/DataModule-CN.md)
- 架构概述（计划中）
- 最佳实践（计划中）

## 📝 许可证

MIT 许可证
