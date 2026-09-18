# 架构

[English](./Architecture.md) | 简体中文

> 本文描述的是在一个生产项目中验证过的设计（见仓库根 README 的状态说明）。文中凡提到"原型/prototype"指的都是那个代码库，不是本仓库——本仓库尚未包含移植后的源码。

## 设计目标

1. **非破坏性**：重新 cook 一个 Houdini 资产、或编辑某一个编辑层，绝不能破坏另一层的数据。
2. **GPU 常驻混合**：图层通过 `RenderTexture` 乒乓合成，而不是反复调用 CPU 端的 `TerrainData.SetHeights`。
3. **Houdini 是生产者，不是引擎的依赖**：`Runtime/Landscape`（混合引擎）必须在零 Houdini Engine 引用的情况下编译并运行，只有 `Runtime/Translators` 依赖它。强制这一点的汇编拆分见各自文件夹的 README。
4. **可追溯到 Unreal 的真实实现**：凡是本包镜像了 Unreal Engine HoudiniEngine 插件里的概念，字段/类型名都尽量贴近原版（`FHoudiniHeightFieldPartData`、`FHoudiniTileInfo`、`FHoudiniLandscapeCreationInfo`、`FHoudiniUnrealLandscapeTarget`），方便日后交叉对照源码。

## 数据模型

### `FHoudiniHeightFieldPartData`（Translators）

每个 Houdini heightfield volume part 对应一个实例。在可行处逐字段镜像 Unreal 的同名结构体（`HoudiniLandscapeUtils.h`，`struct FHoudiniHeightFieldPartData`）。

| 字段 | 含义 |
| :--- | :--- |
| `ObjectId` / `GeoId` / `PartId` | 这个 part 的 HAPI 身份三元组。 |
| `TargetLayerName` | volume 的名字——`"height"`、`"visibility"`，或像 `"dirt"` 这样的 paint 层名。从 volume info **直接**读取，不用缓存/过期的值——见下文"陷阱"。 |
| `UnityLayerName` | *编辑层*的名字（如 `"Base_Height"`、`"River_Erosion"`）。 |
| `HeightField` | 这个 part 体素数据的来源 `HEU_PartData`（或等价物）。 |
| `EditLayerType` | `HAPI_UNREAL_LANDSCAPE_EDITLAYER_TYPE_BASE`（0）或 `..._ADDITIVE`（1）。 |
| `bSubtractiveEditLayer` | 当 `EditLayerType` 为 additive 时，改为做减法而非加法。 |
| `bCreateNewLandscape` | true = Houdini 拥有这个 landscape 的生命周期（由 cook 创建/销毁）；false = 目标是一个外部拥有、只被修改、永不销毁的 landscape。 |
| `TargetLandscapeName` | 决定这个 part 属于哪个 `ALandscape` 的分组键。 |
| `SizeInfo` | `FHoudiniLandscapeCreationInfo`——网格尺寸、section/component 划分。 |
| `TileInfo` | `FHoudiniTileInfo?`——仅当这个 part 是某个多 tile landscape 的一块时才设置。 |
| `CreatedLandscape` | 当 `bCreateNewLandscape` 时，回指这个 part 解析到的 `ALandscape`。用于在下次 cook 重建前销毁 Houdini 拥有的 landscape。 |

### `FHoudiniTileInfo`（Translators）

```csharp
public struct FHoudiniTileInfo
{
    public Vector2Int TileStart;            // 这块 tile 在整张 landscape 中的位置
    public Vector2Int LandscapeDimensions;  // 整张 landscape 的尺寸
}
```

精确镜像 Unreal 的 `FHoudiniTileInfo`（`HoudiniLandscapeUtils.h`）。尽管名字相似，它与 `HAPI_VolumeTileInfo`（一个底层 HAPI 体素读取游标）**无关**——见"陷阱"一节。

### `ULandscapeInfo` / `FLandscapeEditLayer`（Landscape 引擎）

```
ALandscape（主体）
├── _streamingProxies : List<ALandscapeProxy>      // tile 归属挂在 actor 上，而不是 info 上
└── _landscapeInfo : ULandscapeInfo                // [SerializeField]——编辑层栈随场景持久化
        ULandscapeInfo
        ├── LandscapeGuid
        ├── EditLayers : List<FLandscapeEditLayer>  // 栈顺序 = 混合顺序（index 0 = base，最底层）
        └──（按名字缓存的 layer-info，预留给 paint 层）
                FLandscapeEditLayer : IHeightModifier
                ├── Name, bLocked, bVisible
                ├── CombineMode   (HeightStamp.CombineMode；height 只用到 Override/Add——见"混合模式映射")
                ├── Opacity                              // 编辑层的 Alpha（shader 里的 _CombineBlend）
                ├── SavedHeight : Texture2D              // 持久化的高度资产——让一层无需重新 cook 也能挺过场景重载
                ├── TileHeightData : Dictionary<Vector2Int, RenderTexture>   // [NonSerialized] 运行时 GPU 状态
                └── TilePaintData  : Dictionary<Vector2Int, Dictionary<string, RenderTexture>>
```

`FLandscapeEditLayer` 实现 `IHeightModifier`（与手摆的高度笔刷实现的是同一个接口），因此它走的是完全相同的 GPU 混合通道——这是关键的解耦点：混合引擎里没有任何"Houdini 层"对"手摆笔刷"的特判代码。

streaming-proxy 注册表由主体 `ALandscape` 持有（`_streamingProxies`），**不是** `ULandscapeInfo`——单个 `ULandscapeInfo` 实例由主体拥有，并通过 `GetLandscapeActor().GetLandscapeInfo()` 共享给每个 streaming proxy。只有编辑层栈（及每层的设置）会被序列化；每层的 GPU `RenderTexture` 是 `[NonSerialized]`，会在下次 cook 时重建，或在重载后从 `SavedHeight` 惰性重建。

## Cook 管线

```
GetPartsToTranslate(volumeParts)
  -> 每个 volume part 产出一个 FHoudiniHeightFieldPartData，若有 TileInfo 则一并解析
  -> "Update Existing" 的 part（bCreateNewLandscape == false）需要 HDA 的
     AllowLandscapeModification 开关；若关闭，整个 cook 的 landscape 输出
     被中止（返回空）——镜像 Unreal 的 IsLandscapeModificationEnabled() 闸门。
       |
       v
ResolveLandscapes(landscapeMap, parts, inputLandscapes)
  -> 3 趟：
     1. 按 bCreateNewLandscape 给每个 part 分桶：
          true  + 已在 landscapeMap 中 -> 复用（同次 cook 去重，见"陷阱"）
          true  + 尚未创建            -> 分桶进 LandscapesToCreate[name][layerKey]
          false                       -> FindTargetLandscapeProxy（场景搜索 / "Input<N>"）
     2. 为每个 LandscapesToCreate 条目创建一个 ALandscape；用 height 数据 part 给它定尺寸；
        写入 base height（ImportHeightData），让 Terrain 在任何编辑层混合进来之前先有合法的分辨率。
     3. 对已有/外部拥有的 landscape 施加逐 part 的更新（材质等）。
       |
       v
TranslateHeightFieldPart(landscapeTarget, part)   // 每个 part，解析之后
  -> 取出并归一化高度数据
  -> （height）写入这个 part 的编辑层的 TileHeightData RenderTexture；
     CombineMode 由 EditLayerType / bSubtractiveEditLayer 决定
  -> （paint/visibility）归一化到 0..1（写回 alphamap：TODO）
       |
       v
ALandscape.RebuildHeightmap()   // 每个被触及的 landscape 一次，在所有 part 翻译完之后
  -> 收集 ULandscapeInfo.EditLayers（栈顺序）+ GetComponentsInChildren<IHeightModifier>
  -> GenerateHeightmap：对合并后的列表做乒乓 RenderTexture 混合
  -> ApplyRawHeightsToTerrain：读回 + TerrainData.SetHeights（原始 0..1，Y 翻转）写回 Terrain
       |
       v
SaveTerrainDataToAssetCache(landscape)
  -> 把运行时 TerrainData 持久化进 HDA 的资产缓存目录，让 landscape 能 bake 成可用的 prefab
     （运行时 TerrainData 无法被 prefab 引用）
       |
       v
SaveEditLayerHeightsToAssetCache(landscape)
  -> 把每个编辑层混合后的高度 RT 读回成同一缓存目录里的 Texture2D，并把 layer.SavedHeight 指向它，
     这样整个栈在 bake 之前就能挺过场景重载（见"编辑层资产生命周期"）
```

在下一次 cook 的 `ResolveLandscapes` 运行之前，任何上次 cook 的 part 创建出来的 landscape（`bCreateNewLandscape && CreatedLandscape != null`）都会被无条件销毁并重建——这与 Unreal 的实际行为一致（`FHoudiniLandscapeRuntimeUtils::DeleteLandscapeCookedData`），它是一种销毁重建策略，**不是**逐层增量 diff。通过 `bCreateNewLandscape == false` 锁定的 landscape 永远不会被本包销毁；它们是外部拥有的。

> **新建 vs. 更新**，由 `bCreateNewLandscape`（Houdini 的 `unreal_landscape_output_mode`）决定：
> `GENERATE` 新建一个 `ALandscape`（若同名的在上次 cook 后仍存在则复用，从而保留它其余的编辑层栈）；
> 其它任何模式（"Update Existing"）针对的是场景中已有的 landscape，只在它上面*添加/更新*一个编辑层。
> Update-Existing 的 part 只有在 HDA 通过 `AllowLandscapeModification`（默认开）选择启用时才被处理；
> 当该开关关闭时，整个 cook 的 landscape 输出被中止，镜像 Unreal 的
> `Cookable->IsLandscapeModificationEnabled()` 返回 `{}`。

## 编辑层资产生命周期

每个编辑层的高度存在两处：一个 `[NonSerialized]` 的运行时 `RenderTexture`（`TileHeightData`，每次 cook 重建）和一个持久化的 `Texture2D` 资产（`SavedHeight`，在 HDA 缓存里）。资产正是让未 bake 的 landscape 无需重新 cook 就能重载——并让 Inspector 预览真实高度——的东西（`GetHeightRenderTexture` 会从 `SavedHeight` 惰性重建 RT）。

`SaveEditLayerHeightsToAssetCache` 让这两者保持同步，**且不会泄漏重复资产**：

- **重新 cook 时原地覆盖**：若某层的 `SavedHeight` 已经是一个持久化资产，就把像素重新读进**同一个** `Texture2D`（分辨率变了则用 `Reinitialize`），而不是再次对同一路径 `CreateAsset`——后者无法干净地覆盖一个已被占用的路径。（`SaveTerrainDataToAssetCache` 出于同样原因有等价的 `ContainsAsset` 守卫。）
- **改名同步**：资产文件名由层名派生；若该层自上次保存以来在 Inspector 里被改名，下次 cook 会 `RenameAsset` 让资产文件名跟上。
- **删除时清理**：删除一层（Inspector 的"垃圾桶"按钮）或销毁一个 Houdini 拥有的 landscape，会删除该层的 `SavedHeight` 资产（`FLandscapeEditLayer.DeleteSavedHeightAsset`），从而缓存里永远不会堆积孤儿 `*_Height.asset` 文件。这是 editor-only 的（`#if UNITY_EDITOR`），因为引擎汇编也会编进 player 构建，那里没有 `AssetDatabase`。

## Inspector（`ALandscapeEditor`）

自定义 Inspector 复刻了 Unreal 的 landscape **Edit Layers** 面板：一个带层数和 `+`（在顶部新增）按钮的表头，然后每层一行、自上而下绘制（index 最高 = 面板最上 = 最后施加的层），每行显示锁定开关、可见性（眼睛）开关、可编辑的名字、一个 `Alpha` 字段（层的 `Opacity`）、删除按钮、一个 `Additive` 开关、重排箭头，下方是一个小高度预览。

- **锁定**会禁用该行的可编辑字段，并告诉 cook 不要覆盖该层的高度（`bLocked`）；它本身保持可点，以便重新解锁。
- **眼睛**切换 `bVisible`，`IHeightModifier.IsEnabled()` 读取它来决定是否把该层纳入混合。
- **Additive** 是唯一暴露的高度混合选择（Unreal 的高度编辑层只有两种混合方式——叠加 delta，或替换并按 Alpha 混合），所以它映射到 `CombineMode` 的 `Add`/`Override`，而不是完整的 `HeightStamp.CombineMode` 枚举。
- 任何字段/结构变化都会把 `ALandscape` 标脏（这样序列化的 `ULandscapeInfo` 才真正写回），然后调用 `RebuildHeightmap` 做一次实时重混。
- **图标**按三级回退解析，保证一行永远不空白：本包 `Resources` 文件夹里的自定义 PNG（命名 `editlayer_lock_on`、`editlayer_lock_off`、`editlayer_eye_on`、`editlayer_eye_off`、`editlayer_delete`、`editlayer_stack`）→ Unity 内置编辑器图标 → 纯文字字形。

## GPU 混合（`GenerateHeightmap`）

在两个同尺寸的 `RenderTexture` 之间乒乓；每个启用的高度修改器把自己的贡献从源 RT 混合进目标 RT，然后二者交换：

```csharp
foreach (var modifier in heightModifiers)
    if (modifier.GetBounds().Intersects(terrainBounds))
        if (modifier.ApplyHeightStamp(rt1, rt2, heightmapData, od))
            (rt1, rt2) = (rt2, rt1);
```

`FLandscapeEditLayer.ApplyHeightStamp` 用的是一个专门的全渲染目标 combine shader（`EditLayerCombine.shader`），而不是手摆 `HeightStamp` 用的笔刷摆放 shader（`Hidden/MicroVerse/HeightmapStamp`）——一个编辑层本就覆盖整张地形，所以不需要笔刷的变换/衰减数学，只需要 `src`、`combine-mode`、`layer height`、`opacity` 进，混合后的高度出。

**安全守卫**：如果实际上什么都没混合（还没有层有数据，或 combine shader 加载失败），`GenerateHeightmap` 返回 `null` 而不是一张全零 RT，调用方会跳过高度图写回，而不是把地形抹平。

**写回用 `SetHeights`，而非 `CopyActiveRenderTextureToHeightmap`**：混合产出一张原始 `0..1` 高度 RT，`ALandscape.ApplyRawHeightsToTerrain` 把它读回 CPU 并用 `TerrainData.SetHeights` 写入（做 Y 翻转到 Unity 的左下原点）。这与 Unity 的 "Import Raw" 走的是同一条路。最初试过 `CopyActiveRenderTextureToHeightmap`，但它消费的是 Unity 的*打包*高度图格式（原始 `1.0` 存成 `~0.5`）；精确匹配那个打包被证明不可靠——山顶被钳平成"缺一截"——而把同一份数据直接 Import Raw 却渲染正确,这恰好把 bug 定位在写回环节而非数据本身。混合全程留在 GPU；只有最终的地形写入在 CPU(在 cook / 编辑这种非逐帧的节奏下开销可忽略)。

## 混合模式映射

| Houdini 属性状态 | `HeightStamp.CombineMode` |
| :--- | :--- |
| `EditLayerType == BASE`（且非减法） | `Override` |
| `EditLayerType == ADDITIVE`，非减法 | `Add` |
| `bSubtractiveEditLayer == true` | `Subtract`（无论 `EditLayerType` 为何） |

## 多编辑层键（这个设计修掉的一个真实 bug）

只按 `TargetLandscapeName` + `TargetLayerName` 分桶，会悄悄丢掉第二个针对同一 volume 层的编辑层（例如两个 `"height"` 编辑层叠在一张 landscape 上）——第二个会记一条"duplicate layer"警告然后被丢弃。修法是按 **`(TargetLayerName, UnityLayerName)`** 作键，让每个不同的编辑层都能挺过分桶。

## Tile 摆放（不是数据合并）

一个常见的误读：`FHoudiniTileInfo` **不**意味着多块物理 Houdini tile 会被合进同一张 landscape 的高度图。Unreal 自己的分桶（每个 landscape 名一个 `TMap<FString /*layer*/, FHoudiniHeightFieldPartData*>`，重复项*警告并丢弃*、从不合并）证实了一个 `TargetLandscapeName` = 一张独立的 landscape actor。Tiling 的做法是给每块 tile 自己的 `TargetLandscapeName`（让每块成为自己的 `ALandscape`），再由 `TileInfo.TileStart` 平移那个 actor 的世界变换，让各自独立创建/摆放的 landscape 在边缘对齐：

```cpp
// Unreal: GetLandscapeActorTransformFromTileTransform —— 纯平移，不合并数据。
Result.SetLocation(Result.GetLocation() - OffsetX - OffsetY);
```

本包目前会在创建时写入 base height，但尚未施加这个 tile 偏移变换——见根 README 的 TODO。

## 相对 Unreal 的已知简化

- `ULandscapeInfo` **不是**通过一个全局子系统（Unreal：`ULandscapeSubsystem`）按 GUID 索引的。一个 info 实例直接由主体 `ALandscape` 拥有，并通过 `GetLandscapeActor().GetLandscapeInfo()` 共享给它的 streaming proxy。
- `FLandscapeEditLayer.TileHeightData` 目前只解析单个（`Vector2Int.zero`）条目；真正的逐 tile 查找属于延后的 World Partition / tiling 工作。
- paint/visibility 层会归一化，但尚未写入 `TerrainData` 的 alphamap。
- `FHoudiniHeightFieldPartData` 刻意省略了 Unreal 的 `HeightRange`、`PropertyAttributes` 以及若干在本包里暂无消费者的 `TOptional<...>` 字段。

## 改动前值得重读的陷阱

- **`TargetLayerName` 必须来自 volume 自己的 name 属性**，而不是某个只有其它无关管线才填充的 `GetVolumeLayerName()` 式访问器——在这里用了过期/null 的值会让字典以 null 为键并在解析阶段崩溃。
- **需要"任意 owner"（Unreal 的 `HAPI_ATTROWNER_INVALID`）的字符串属性读取**必须用一个 owner 无关的读取方式，而不是一个会直接拒绝显式"invalid" owner 值的"严格 owner"辅助函数。
- **运行时创建的 `TerrainData` 在 URP/HDRP 下会渲染成粉红色**，除非显式指定一个管线对应的地形 shader；内置地形 shader 不属于那些管线。
- **`HAPI_VolumeTileInfo`（HAPI 的体素块读取游标）和 `FHoudiniTileInfo`（多 tile landscape 摆放）尽管名字里都有 "tile"，但二者无关**——别在想要其一时抓了另一个。
