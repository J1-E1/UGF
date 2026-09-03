# Unity Game Framework (UGF)

## Modular game framework for Unity projects

![Status](https://img.shields.io/badge/status-in%20development-orange.svg)
![Unity Version](https://img.shields.io/badge/Unity-6.0%2B-blue.svg)
![C#](https://img.shields.io/badge/C%23-9.0%20%2F%20.NET%20Standard%202.1-178600.svg)
![License](https://img.shields.io/badge/License-MIT-green.svg)

English | [简体中文](./README-CN.md)

A lightweight, modular game framework for Unity that provides common game systems with minimal coupling. Each module can be used independently or composed together for complete game functionality.

## Table of Contents

- [Requirements](#-requirements)
- [Installation](#-installation)
- [Project Structure](#-project-structure)
- [Modules](#-modules)
- [Documentation](#-documentation)
- [License](#-license)

## 📋 Requirements

| Component | Version | Notes |
| :--- | :--- | :--- |
| Unity | 6.0+ | Tested on Unity 6.0 and newer. Targets .NET Standard 2.1 / C# 9.0. |
| UniTask | 2.5.0+ | Required for async operations. Install via Package Manager or UPM. |
| Addressables | 2.0.0+ | Required for DataModule asset loading and runtime management. |

## 💽 Installation

**Option 1: Unity Package Manager** (via git URL)

1. Window -> Package Manager -> + -> Install package from git URL...
2. Paste repository URL

**Option 2: Local clone** (for development)

Clone and install via Package Manager -> Install package from disk...

## 📂 Project Structure

```
UGF/
├── DataModule/           # Configuration data management (Luban tables, Addressables)
│   └── DataManager.cs
├── Runtime/              # Core runtime systems (planned)
├── Editor/               # Editor tools and inspectors (planned)
└── Documentation/        # Architecture docs and API references
```

## 🎮 Modules

### DataModule

Configuration data management system that handles Luban-generated tables via Addressables.

**Key Features:**
- **Load Policies**: AlwaysLoaded (on init) / OnDemand (lazy)
- **Group Management**: Register tables by group, load/unload together
- **Query API**: Get by ID, GetAll, FindAll with predicates
- **Async Loading**: UniTask-based with concurrent group loading
- **Binary Format**: Runtime uses .bytes only for optimal performance

**API Overview:**

```csharp
// Initialize at game start
await DataManager.Instance.InitializeAsync();

// Get single entry
var chapter = DataManager.Instance.Get<ChapterData>(101);

// Query with predicate
var unlocked = DataManager.Instance.FindAll<ChapterData>(c => c.IsUnlocked);

// Pre-warm on-demand tables
await DataManager.Instance.EnsureLoadedAsync<WeaponData>();

// Group loading
await DataManager.Instance.LoadGroupAsync("CoreConfig");
```

**[-> Full DataModule Documentation](./Documentation/DataModule.md)**

## 📚 Documentation

- [DataModule API Reference](./Documentation/DataModule.md)
- Architecture Overview (planned)
- Best Practices (planned)

## 📝 License

MIT License
