# 动物繁殖系统 · 模组扩展接口指南（EXTENSION）

本文档面向**第三方模组开发者**（如计划制作疾病系统、草药系统、生态平衡类模组的开发者），说明如何接入本模组的机制、接口与配置。

> 目标场景：你想做一个"疾病系统"让生病的动物繁殖能力下降？做一个"草药系统"让玩家喂药/喂草影响动物状态？本模组已预留查询、操作、事件、配置四类接口。

---

## 一、区域繁殖密度限制（内置机制）

**机制**：以母体为中心、半径 `DensityRadius` 内的同繁殖群（含别名，如 Cow↔Bull）**成年个体数越多，配对效率越低**。

**公式**：

```
密度因子 = 1.0                                (个体数 ≤ DensityLimit)
密度因子 = max(1 - 超出数 × DensityPenaltyStep, 0)   (个体数 > DensityLimit)

配对效率 = 密度因子  →  作用于母体"相处计时"累加速度
```

**效果**：头顶显示 `求偶中(相处N秒)` 的 N 增长随密度变慢；密度因子降到 0 时完全停止配对。避免动物无限繁殖导致卡顿/生态失衡。

**参数（按物种配置，均可在 `BreedingConfig.json` 中调整）**：

| 参数 | 默认 | 说明 |
|------|------|------|
| `DensityEnabled` | `true` | 密度限制总开关 |
| `DensityRadius` | `32` | 统计半径（方块） |
| `DensityLimit` | `8` | 理想上限（低于此值效率 100%） |
| `DensityPenaltyStep` | `0.15` | 每超 1 只的效率降低量（0~1） |

**示例**：`DensityLimit=8, DensityPenaltyStep=0.15` → 区域内第 9 只成年个体时效率 85%，第 10 只 70%，…… 第 15 只时降到 0。

---

## 二、扩展接口（供其他模组调用）

### 2.1 引用方式

本模组的接口是 **`Game.SubsystemBreeding` 静态类 + `Game.SubsystemBreeding.BreedingEvents` 静态事件**。其他模组接入方式：

1. 在你的模组工程中**引用本模组 `BreedingMod.dll`**（SCPAK/SC 支持把依赖 DLL 一并打包进你的 `.scmod`）；
2. 或使用**反射**调用同名静态方法（不引 DLL 时）。

> 判断系统是否就绪：`SubsystemBreeding.Initialized`（true 表示世界已加载、繁殖系统运行中）。

### 2.2 状态查询 API（只读）

| 方法 | 返回 | 说明 |
|------|------|------|
| `GetState(Entity)` | `BreedingState` | 繁殖状态对象（性别/阶段/孕期/恢复期/相处计时等） |
| `GetGender(Entity)` | `BreedingGender?` | 性别（Male/Female），未追踪返回 null |
| `IsAdult(Entity)` | `bool` | 是否成年 |
| `IsInEstrus(Entity)` | `bool` | 是否求偶期（需喂食物种含已喂食判定） |
| `IsPregnant(Entity)` | `bool` | 是否怀孕 |
| `IsWeak(Entity)` | `bool` | 是否恢复期（配对/产仔后） |
| `IsFed(Entity)` | `bool` | 是否已喂食（条件性繁衍） |
| `GetGrowthProgress(Entity)` | `float` | 成长进度 0~1 |
| `GetDensityFactor(Entity, SpeciesConfig)` | `float` | 区域密度因子 0~1（1=密度达标） |

### 2.3 状态操作 API（可写，供疾病/草药等系统调用）

| 方法 | 说明 |
|------|------|
| `SetPregnant(Entity, float gestationSeconds)` | 设置母体怀孕（孕期秒数），可模拟异常孕期 |
| `SetWeak(Entity, float seconds)` | 设置恢复期，可让个体暂停求偶（如"生病休养"） |
| `SetFed(Entity, float seconds)` | 设置已喂食状态（如"喂草药后短暂发情"） |
| `CureBreedingState(Entity)` | 治愈：清空孕期/恢复期/相处计时/已喂食（疾病治愈用） |

所有操作 API 调用成功后都会触发 `BreedingEvents.StateChanged`。

### 2.4 事件（订阅即可获得通知）

| 事件 | 参数 | 触发时机 |
|------|------|---------|
| `BreedingEvents.MatingSuccess` | `(mother, father)` | 配对成功 |
| `BreedingEvents.Birth` | `(mother, cub)` | 产仔成功 |
| `BreedingEvents.Fed` | `(entity, fedSeconds)` | 个体被喂食触发求偶 |
| `BreedingEvents.StateChanged` | `(entity, state)` | 状态被 API 操作修改 |

---

## 三、配置扩展（第三方加物种/调参数/覆盖原版属性）

本模组支持**多源配置合并**：其他模组只需在自己的 `MOD/Assets/` 下放一份 `BreedingConfig.{你的ModId}.json`，即可：

- **追加新物种**的繁殖配置（默认行为）；
- **覆盖主配置已有物种**（提高原版属性）：在扩展配置根上开启 `"OverrideMain": true`。

### 3.1 追加新物种（默认）

示例（疾病模组的配置文件 `BreedingConfig.DiseaseMod.json`）：

```jsonc
{
  "Species": {
    "MyNewCreature": {          // 追加你自己的新物种
      "BreedingSeasons": [ "Summer" ],
      "GestationSeconds": 2400.0,
      "CubDurationDays": 6,
      "DensityLimit": 4
    }
  }
}
```

### 3.2 覆盖主配置（OverrideMain=true，提高原版属性）

开启后，本扩展中与主配置**同名**的物种执行**字段级覆盖**：显式写了的字段覆盖主配置，未写的字段保留主配置原值。

示例（"强化原版生物"模组的配置文件 `BreedingConfig.BoostMod.json`）：

```jsonc
{
  "OverrideMain": true,          // 关键开关：允许覆盖主配置物种
  "Species": {
    "Wolf_Gray": {
      "GestationSeconds": 1200.0,   // 覆盖：孕期缩短为 1 天
      "CubDurationDays": 3,         // 覆盖：成长期缩短为 3 天
      "AdultMaleBoxScale": 1.5,     // 覆盖：公狼体型增大
      "MaleAttackBonus": 1.5        // 覆盖：公狼攻击力增强
      // 未写的字段(季节/密度/喂食/恢复期等)保留原版配置
    },
    "Horse_White": {
      "DensityLimit": 16,           // 覆盖：白马密度上限翻倍
      "DensityPenaltyStep": 0.1     // 覆盖：密度惩罚更温和
    }
  }
}
```

**覆盖规则**：
- 字段级合并：只覆盖显式写了的字段（数值/布尔/字符串与默认值不同才覆盖；集合写了非空列表才覆盖）；
- 覆盖后自动重新校验参数（`Normalize`），日志输出 `扩展配置 xxx 覆盖物种 'yyy'(OverrideMain=true)`；
- 多个扩展同时覆盖同一物种时，按文件名排序**后加载者生效**。

> 注意：扩展配置的 `Enabled` 字段始终被忽略（防止第三方关闭整个系统）；`OverrideMain` 只在扩展配置中生效。

### 3.3 自定义生物显示名（i18n）

悬浮文字第 1 行显示"性别 + 生物名"，生物名**优先取模组 i18n**：`Lang/{语言}.json` 的 `BreedingMod.Species.{模板名}`，找不到则回退原版 `creature.DisplayName`。

- 本模组已在 `zh-CN.json` 内置 39 种生物中文名（与 SPECIES.md 一致）；
- 第三方模组若需自定义/补充显示名，可在自己的 `MOD/Assets/Lang/` 下放同名键（或修改本模组语言文件）：

```jsonc
{
  "BreedingMod": {
    "Species": {
      "MyNewCreature": "我的新生物"
    }
  }
}
```

---

## 四、拓展示例

### 4.1 疾病系统（示意）

```csharp
// 1) 订阅产仔事件：新生个体 30% 概率患病
SubsystemBreeding.BreedingEvents.Birth += (mother, cub) =>
{
    if (Random.Shared.NextDouble() < 0.3)
        DiseaseSystem.Mark(cub, DiseaseKind.Cold);   // 你自研的疾病标记(用 Dictionary<Entity,...> 自己存)
};

// 2) 每帧/定时：患病个体抑制繁殖
//    在你自己模组的 Update 里：
foreach (var (entity, disease) in DiseaseSystem.All())
{
    if (disease.CausesWeakness)
        SubsystemBreeding.SetWeak(entity, 30f);      // 让患病个体 30 秒内不进入求偶
}

// 3) 治愈：喂药后调用
SubsystemBreeding.CureBreedingState(entity);         // 清空孕期/恢复期等，恢复健康个体繁殖能力

// 4) 查询密度压力：患病时再叠加环境惩罚
float density = SubsystemBreeding.GetDensityFactor(entity, species);
```

### 4.2 草药系统（示意）

```csharp
// 1) 监听喂食事件：识别"玩家喂的是草药"
SubsystemBreeding.BreedingEvents.Fed += (entity, fedSeconds) =>
{
    if (lastFeedItem == "HerbBlock")                 // 你自己跟踪玩家的喂食物品
        HerbSystem.ApplyBuff(entity, BuffKind.Fertility);
};

// 2) 喂"催情草"：直接让个体进入求偶(需喂食物种)
SubsystemBreeding.SetFed(entity, 600f);

// 3) 喂"安胎草"：延长孕期或直接完成孕期
SubsystemBreeding.SetPregnant(entity, 5f);           // 5 秒后产仔
```

---

## 五、注意事项

1. **线程**：事件在游戏主线程触发，订阅者无需加锁，但不要在事件内做耗时操作；
2. **生命周期**：`s_states` 随世界卸载清空，跨世界需重新获取实体；事件订阅建议在 `__ModInitialize` 注册，`ModDispose` 注销；
3. **只读优先**：查询 API 不修改状态，可安全调用；操作 API 会同步触发 `StateChanged`；
4. **接口稳定性**：以上 API 为本模组正式接口，后续版本保持兼容（新增只增不改）；
5. **更多参数**：完整物种参数含义见 [CONFIG.md](CONFIG.md)，生物清单见 [SPECIES.md](SPECIES.md)。
