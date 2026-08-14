# 动物繁殖系统 · 联机版（SurvivalcraftNet）适配说明

本分支（`net`）为 [SurvivalcraftNet 联机版](https://gitee.com/SC-SPM/SurvivalcraftNet) 适配版本，基于 `main` 分支（单机版 v1.0.0）改造。

> ⚠️ 联机版 API 与单机版差异较大，本适配已处理主要差异，**建议在联机版编译环境验证后使用**。

---

## 一、与单机版的差异与适配方案

| 项目 | 单机版 | 联机版（本适配） |
|---|---|---|
| **每帧驱动** | `OnFactorsUpdate` 钩子（对每只生物调用） | 模组实现 `IUpdateable`，在 `OnProjectLoaded` 注册到 `SubsystemUpdate`，每帧遍历所有追踪生物（`SubsystemBreeding.Update`） |
| **浮动文字渲染** | `OnModelDrawExtra` 钩子 | `OnModelRendererDrawExtra`（签名含 `alphaThreshold`，无 `skip`） |
| **活体生物状态存档** | `ProjectXmlSave` + `OnProjectXmlSaved` | `ProjectXmlSave`（唯一保存点，写入 `BreedingModStates` 节点） |
| **Despawn 生物状态** | `SpawnEntityData.Data`（随区块保存） | 联机版无此机制 → **状态不随实体保存**，重新生成时按 EntityId 确定性分配性别（孕期/成长重置） |
| **季节系统** | `SubsystemSeasons`（原版季节） | 联机版无季节子系统 → **伪季节**：按游戏天每 30 天一季（0-29 春 / 30-59 夏 / 60-89 秋 / 90-119 冬），`BreedingSeasons` 配置按此生效 |
| **骑乘拦截** | `ScoreMount` 钩子 | 联机版未提供 → **已移除**（繁殖期/幼崽期禁止上鞍功能在联机版不可用） |
| **图鉴介绍** | `LoadCreatureInfoInBestiaryScreen` / `UpdateCreaturePropertiesInBestiaryDescriptionScreen` | 联机版未提供 → **已移除**（图鉴介绍在联机版不显示） |
| **仇恨范围修改** | `ComponentFactors.OtherFactors["ChaseRange"]` | 联机版无 `ComponentFactors` → **已移除**（如需可自行用 `ComponentChaseBehavior.m_dayChaseRange` 扩展） |

## 二、功能保留清单（联机版可用）

✅ 求偶判定（伪季节 + 喂食条件） ✅ 公体寻路/追求 ✅ 求偶竞争打斗
✅ 孕期倒计时 / 产仔（`SubsystemCreatureSpawn.SpawnCreature`） ✅ 幼崽成长与体型变化
✅ 攻击力按阶段/性别修正（`OnMinerHit`） ✅ 喂食求偶（`OnEatPickable`）
✅ 区域密度限制 ✅ 性别确定性分配（跨会话稳定）
✅ 活体生物状态持久化（Project.xml） ✅ 头顶浮动文字（7 语言）

## 三、已知降级项

1. **Despawn 生物状态丢失**：远离后卸载的生物重新生成时，性别稳定（确定性），但孕期/成长/喂食状态重置为成年默认。待联机版提供实体级存档钩子后可恢复。
2. **季节为伪季节**：每 30 游戏天一季（非原版月份机制），仅影响求偶判定。
3. **禁止上鞍/骑乘拦截**：联机版无 `ScoreMount` 钩子，繁殖期/幼崽期仍可上鞍。
4. **图鉴生物介绍**：联机版无对应钩子，图鉴显示原版介绍（不含模组介绍/Stats）。
5. **仇恨范围倍率**：联机版无 `ComponentFactors`，发情期仇恨范围不会增大。

## 四、编译与部署

1. 引用 **联机版** 的 `Engine.dll` / `EntitySystem.dll` / `Survivalcraft.dll`（来自 SurvivalcraftNet 发布包）；
2. 编译 `BreedingMod.csproj`；
3. 打包 `.scmod` 放入联机版客户端与服务器 `Mods/` 目录（客户端/服务器均需加载）。

> 注意：联机版 `Season` 枚举位于游戏程序集中，若你的联机版程序集不含 `Season` 类型，请将 `BreedingConfig.cs` 中的季节判定改为字符串比较（可联系我们适配）。

## 五、建议验证清单

- [ ] 放置公母生物，季节到后是否求偶/竞争/配对
- [ ] 孕期倒计时是否产仔、幼崽是否成长
- [ ] 喂食求偶（需喂食物种喂食后才求偶）
- [ ] 活体生物退出世界重进后性别/孕期是否保留
- [ ] 头顶浮动文字是否显示（客户端）
- [ ] 服务器端是否正常加载（无报错）
