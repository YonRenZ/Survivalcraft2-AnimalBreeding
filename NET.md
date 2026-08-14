# 动物繁殖系统 · 联机版（SurvivalcraftNet）适配说明

本分支（`net`）为 [SurvivalcraftNet 联机版](https://gitee.com/SC-SPM/SurvivalcraftNet) 适配版本，基于 `main` 分支（单机版 v1.0.0）改造，**已通过 .NET 10 编译（0 错误）**。

> 说明：联机版发布包的 `ModLoader` 缺失单机版大部分钩子（`OnProjectLoaded`/`OnProjectDisposed`/`ProjectXmlLoad`/`ProjectXmlSave`/`OnEntityAdd`/`OnEntityRemove` 等），本适配基于联机版实际可用的钩子与 API 重写。

---

## 一、适配方案（基于联机版实际 API）

| 项目 | 联机版方案 |
|---|---|
| **初始化/卸载** | 每帧检测 `GameManager.Project`（静态属性）：世界加载 → `Initialize`，卸载 → `ClearXmlCache`（惰性，替代缺失的 OnProjectLoaded/Disposed） |
| **每帧驱动** | 双保险：override `SubsystemUpdate(Single dt)` 钩子 + 实现 `IUpdateable` 注册到 `SubsystemUpdate.AddUpdateable` |
| **实体追踪** | 每帧全量对比 `GameManager.Project.Entities` 与追踪表：新实体注册、消失实体清理（`SyncEntities`，替代缺失的 OnEntityAdd/Remove） |
| **状态存档** | 无 ProjectXmlSave 钩子 → 独立文件 `BreedingStates.xml`（世界目录下）：每 30 秒定期保存 + 世界卸载时保存；加载时 `LoadStatesFromFile` |
| **实体 ID** | `Entity.Id`(int) → `Entity.EntityId`(ushort，联机版属性) |
| **渲染** | `OnModelRendererDrawExtra(SubsystemModelsRenderer, ComponentModel, Camera, float?)` |
| **季节** | 无 `SubsystemSeasons` → 游戏天伪季节（每 30 天一季：0-29 春/30-59 夏/60-89 秋/90-119 冬），`BreedingSeasons` 按此生效 |
| **喂食/攻击力** | `OnEatPickable` / `OnMinerHit`（联机版签名一致） |

## 二、联机版 API 差异适配细节

1. **`BlocksManager.GetBlockIndex` → `BlocksManager.GetBlock("", 类名).BlockIndex`**（喂食物品解析）
2. **`ContentInfo.ContentSuffix` 不存在 → 用 `ContentInfo.ContentStream` 直接读取**（扩展配置合并）
3. **`ComponentModel.ModelScale` 不存在 → 移除视觉模型缩放**：体型仅缩放碰撞盒 `BoxSize`（幼崽碰撞变小，视觉大小保持原版）
4. **图鉴介绍钩子 / 骑乘拦截钩子（ScoreMount）不存在 → 已移除**（联机版图鉴不显示模组介绍，繁殖期/幼崽期不禁止上鞍）
5. **`ComponentFactors` 不存在 → 仇恨范围倍率修改已移除**

## 三、功能保留清单（联机版可用）

✅ 求偶判定（伪季节 + 喂食条件） ✅ 公体寻路/追求 ✅ 求偶竞争打斗
✅ 孕期倒计时 / 产仔（`SubsystemCreatureSpawn.SpawnCreature`） ✅ 幼崽成长（碰撞盒体型变化）
✅ 攻击力按阶段/性别修正（`OnMinerHit`） ✅ 喂食求偶（`OnEatPickable`）
✅ 区域密度限制 ✅ 性别确定性分配（跨会话稳定）
✅ 状态持久化（BreedingStates.xml 独立文件，活体生物） ✅ 头顶浮动文字（7 语言）

## 四、已知降级项

1. **Despawn 生物状态丢失**：生物远离卸载后重新生成时，性别稳定（确定性），孕期/成长/喂食重置为成年默认。
2. **季节为伪季节**：每 30 游戏天一季（非原版季节子系统）。
3. **禁止上鞍/骑乘拦截**：联机版无 `ScoreMount` 钩子。
4. **图鉴生物介绍**：联机版无对应钩子。
5. **仇恨范围倍率**：联机版无 `ComponentFactors`。
6. **视觉模型缩放**：幼崽视觉大小不随成长变化（碰撞盒仍变）。
7. **存档时机**：独立文件每 30 秒 + 卸载时保存（非随游戏存档即时同步，极端退出可能丢失最近 30 秒状态）。

## 五、编译与部署

### 编译要求
- **.NET 10 SDK**（联机版 DLL 基于 .NET 10 编译，需匹配）；
- 引用联机版 `Engine.dll` / `EntitySystem.dll` / `Survivalcraft.dll`（放到 `Quoted/` 或改 csproj HintPath）；
- `BreedingMod.csproj` 已改为 `TargetFramework=net10.0`。

```bash
dotnet build BreedingMod.csproj -c Release
```

### 部署
- 将 `bin/Release/net10.0/BreedingMod.dll` 与 `MOD/`（modinfo.json + Assets/）打包为 `.scmod`；
- 放入**联机版客户端与服务器**的 `Mods/` 目录（建议两端都加载）。

## 六、建议验证清单

- [ ] 客户端/服务器加载无报错
- [ ] 放置公母生物，季节到后求偶/竞争/配对
- [ ] 孕期倒计时产仔、幼崽碰撞盒成长
- [ ] 喂食求偶（需喂食物种喂食后才求偶）
- [ ] 重进世界后性别/孕期是否保留（BreedingStates.xml）
- [ ] 头顶浮动文字显示（客户端）
