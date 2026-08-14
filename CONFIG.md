# 繁殖系统配置文档 (CONFIG)

本模组所有参数都从 [MOD/Assets/BreedingConfig.json](MOD/Assets/BreedingConfig.json) 读取，**退出世界重进即生效**，无需重新编译。

配置文件使用 JSON 格式。全局只保留 `Enabled` 总开关，**其余所有参数都按物种独立配置**，这样不同生物可以有不同的孕期、体型、攻击力等。

> 以 `_` 开头的字段会被忽略，仅作说明用。所有字段名大小写不敏感。

---

## 一、配置结构

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter" ],
      "CubDurationDays": 3,
      "GestationSeconds": 30.0,
      ...
    }
  }
}
```

- `Enabled`（全局）：总开关，`false` 时繁殖系统完全不生效。
- `Species`（全局）：按实体模板名索引的物种字典，每个物种独立配置所有繁殖参数。

---

## 二、全局参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `true` | 全局总开关。`false` 时繁殖系统完全不生效，所有生物保持原版行为。 |

---

## 三、物种参数（每个物种独立配置）

以下参数都写在 `Species.模板名` 下，例如 `Species.Wolf_Gray`。

### 繁殖季节与成长

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `BreedingSeasons` | string[] | `["Winter"]` | 繁殖季节列表。可选值：`Summer` / `Autumn` / `Winter` / `Spring`。 |
| `CubDurationDays` | float | `3` | 幼崽期持续天数（游戏天）。到期后进阶成年。 |

### 时间参数（单位：现实秒）

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `GestationSeconds` | float | `30.0` | 孕期持续秒数。母体交配成功后此秒数分娩。 |
| `MatingRequiredProximitySeconds` | float | `10.0` | 交配所需相处时间。公母在 `MateRadius` 内持续相处此秒数后触发交配。 |
| `WeaknessSeconds` | float | `60.0` | 虚弱期持续秒数。交配后仅公体虚弱，分娩后母体虚弱。虚弱期间不发情。 |
| `RivalChaseTime` | float | `30.0` | 公体竞争时追击竞争对手的时长。多公追同一母狼时互相攻击的持续时间。 |

### 距离参数（单位：方块）

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `MateRadius` | float | `2.0` | 交配判定半径。公母在此距离内持续相处才算交配。 |
| `SeekRadius` | float | `20.0` | 公体寻找母体的搜索半径。公体发情时在此范围内寻找母体并走过去。 |
| `BirthSpawnOffset` | float | `1.5` | 分娩时幼崽在母体附近的随机偏移范围。 |

### 攻击力参数

攻击力公式：`最终攻击力 = 基础攻击力 × 阶段系数 × 性别系数`

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `CubAttackFactor` | float | `0.3` | 幼崽攻击力系数。 |
| `AdultAttackFactor` | float | `1.0` | 成年攻击力系数（基准）。 |
| `MaleAttackBonus` | float | `1.3` | 公体攻击力额外倍率（母体为 1.0）。 |

**示例**：
- 成年公狼：基础 × 1.0 × 1.3 = **1.3×**
- 成年母狼：基础 × 1.0 × 1.0 = **1.0×**
- 幼狼（公）：基础 × 0.3 × 1.3 = **0.39×**

### 体型参数

体型公式：`scale = CubBoxScale + (成年scale - CubBoxScale) × 成长进度`

同时作用于碰撞盒（BoxSize）和视觉模型（ModelScale）。

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `CubBoxScale` | float | `0.5` | 幼崽出生时体型缩放（相对原版）。 |
| `AdultMaleBoxScale` | float | `1.3` | 成年公体体型缩放（相对原版）。 |
| `AdultFemaleBoxScale` | float | `1.0` | 成年母体体型缩放（相对原版）。 |

### 仇恨与性别参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `EstrusChaseRangeMultiplier` | float | `2.0` | 发情期仇恨范围倍率（乘到 ChaseRange 上）。 |
| `CubMaleProbability` | float | `0.5` | 幼崽/自然生成个体的公体概率（0~1）。`0`=全母，`1`=全公，`0.5`=各半。 |

> 幼崽和怀孕母狼的仇恨范围固定为 0（不产生仇恨），不受此参数影响。

### 区域密度参数（繁殖效率限制）

控制"区域内生物越多、配对效率越低"，防止动物无限繁殖。机制：以母体为中心、`DensityRadius` 内同繁殖群（含别名，如 Cow↔Bull）成年个体数超过 `DensityLimit` 后，配对效率按 `DensityPenaltyStep` 逐级降低，作用于母体"相处计时"的累加速度（头顶 `求偶中(相处N秒)` 的 N 增长变慢）。

```
密度因子 = 1.0                              (个体数 ≤ DensityLimit)
密度因子 = max(1 - 超出数 × DensityPenaltyStep, 0)   (个体数 > DensityLimit)
```

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `DensityEnabled` | bool | `true` | 密度限制总开关。 |
| `DensityRadius` | float | `32` | 统计半径（方块）。 |
| `DensityLimit` | float | `8` | 理想上限（低于此值效率 100%）。 |
| `DensityPenaltyStep` | float | `0.15` | 每超 1 只的效率降低量（0~1）。 |

> **示例**：`DensityLimit=8, DensityPenaltyStep=0.15` → 第 9 只成年个体时效率 85%，第 10 只 70%，…… 第 15 只时降到 0（完全停止配对）。

### 交互拦截参数（可骑乘/可上鞍物种专用）

控制繁殖期间和幼崽期间是否禁止玩家对生物交互（上鞍 + 骑乘）。仅对可上鞍/可骑乘物种有意义（Horse/Donkey/Camel/Reindeer/Ostrich），对其他物种配置无害但无实际效果。

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `BlockInteractDuringBreeding` | bool | `true` | 繁殖期间（发情/怀孕/虚弱）是否禁止交互（上鞍+骑乘）。 |
| `BlockInteractDuringCub` | bool | `true` | 幼崽期是否禁止交互（上鞍+骑乘）。 |
| `ConsumeSaddleOnBlocked` | bool | `false` | 上鞍被拦截时是否仍消耗玩家手中的鞍。详见下方说明。 |

> **`ConsumeSaddleOnBlocked` 详细说明**：
> - `false`（默认）：希望退鞍。**但当前 mod API 无 `OnUse` hook，原版 `SubsystemSaddleBlockBehavior.OnUse` 在调用我们 hook 前已经 `RemoveActiveTool(1)` 扣鞍，因此实际行为是"鞍已扣 + 上鞍被撤销"，无法真正退鞍。** 真正退鞍需要等官方加 `OnUse` hook 或改用 Harmony patch。
> - `true`：鞍被扣掉但马没上鞍（作为惩罚，玩家会看到"该生物无法上鞍"日志）。
> - **两者的实际区别仅在于日志措辞**，鞍都会被扣。若要真正退鞍需官方支持。
> - **骑乘拦截无此问题**：`ScoreMount` hook 是干净的，被拦截时玩家根本无法骑上，无任何副作用。

### 物种别名与幼崽模板参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Aliases` | string[] | `[]` | 物种别名列表。当前模板可与此列表中的模板**互相交配**。需双向配置。 |
| `CubTemplateOverride` | string | `null` | 幼崽生成时使用的模板名。空或 null = 沿用母体模板（默认）。 |
| `CubTemplates` | object | `{}` | 幼崽模板权重表（优先级高于 `CubTemplateOverride`）。键=模板名，值=权重。例 `{"Cow":1,"Bull":1}` = 各 50%。 |

> **`Aliases` 用法**：让两个不同模板互相交配。例如 `Cow` 配 `Aliases=["Bull"]`、`Bull` 配 `Aliases=["Cow"]`，则母牛可和公牛交配。**必须双向配置**，否则只有一方识别。
>
> **`CubTemplateOverride` 用法**：控制幼崽用什么模板。默认沿用母体（母牛生小母牛，母公牛生小公牛）。若想让母牛只生 `Cow` 模板幼崽，配 `CubTemplateOverride="Cow"`。
>
> **`CubTemplates` 用法**：按权重随机选幼崽模板，优先级高于 `CubTemplateOverride`。例 `{"Cow":1,"Bull":1}` 表示母牛 50% 生小母牛、50% 生小公牛。空或未配则回退到 `CubTemplateOverride` 或沿用母体。

### 条件性繁衍参数（喂食发情）

控制生物在繁殖季节内是否还需要玩家**喂食特定物品**才会发情。开启后，到季节的生物头顶会显示"需喂食"，玩家把对应物品扔到地上被它吃掉后，才会进入发情状态。

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `RequireFeeding` | bool | `false` | 是否启用条件性繁衍。`true` 时，生物在繁殖季节内还需被喂食 `FeedItem` 物品后才发情。 |
| `FeedItem` | string | `null` | 喂食物品（方块类名）。支持 `"类名"` 或 `"类名:数据"` 格式。留空 = 接受该生物原版会吃的任何食物。 |
| `FedDurationSeconds` | float | `600` | 已喂食状态持续秒数（现实秒）。喂食后此秒数内可发情，到期需再次喂食。 |

> **⚠️ 重要约束**：`FeedItem` 必须是该生物**原版会吃的食物**（匹配其 `FoodFactors`），否则生物不会去吃它，喂食钩子不会触发，永远无法发情。
>
> **常见食物方块类名**（来自原版 `FoodFactors` 配置）：
>
> | 食物类名 | 食物类型 | 谁吃 |
> |---------|---------|------|
> | `RawMeatBlock` / `CookedMeatBlock` / `RawBirdBlock` / `CookedBirdBlock` | Meat | 狼 |
> | `RawFishBlock` / `CookedFishBlock` | Fish | 狼 |
> | `TallGrassBlock` / `RyeBlock` / `CottonBlock` / 各色花 | Grass | 牛、公牛、马、驴、骆驼、驯鹿、羊驼、角马、野牛 |
> | `BreadBlock` | Bread | 马、驴、骆驼 |
> | `PumpkinBlock` | Fruit | 鸵鸟 |
>
> **喂食流程**：玩家把物品扔到生物附近 → 生物寻路走过去吃掉（原版行为，约 4~5 秒）→ 触发 `OnEatPickable` 钩子 → 标记该个体为"已喂食" → 持续 `FedDurationSeconds` 秒内可发情交配。
>
> **头顶状态显示**：开启条件性繁衍后，在季节内未喂食的生物头顶显示"需喂食"，喂食后正常显示"发情中"。
>
> **示例**（灰狼默认已关闭喂食；如需让灰狼改回"需喂生肉才发情"）：
> ```json
> "Wolf_Gray": {
>   "RequireFeeding": true,
>   "FeedItem": "RawMeatBlock",
>   "FedDurationSeconds": 600.0
> }
> ```
>
> **示例**（接受任何狼会吃的食物：生肉/熟肉/生鱼/熟鱼都行）：
> ```json
> "Wolf_Gray": {
>   "RequireFeeding": true,
>   "FeedItem": null,
>   "FedDurationSeconds": 600.0
> }
> ```

---

## 四、已支持的物种

全部 **39 种已支持物种** 的清单与参数说明已迁移至 **[SPECIES.md](SPECIES.md) 生物图鉴**（含中文名、类别、体型、时间参数、体型/攻击力倍率、可骑可鞍、喂食物品、备注等完整信息）。

> 模板名必须与 `Database.xml` 中的生物模板名完全一致。本文件仅描述参数含义与配置方法，具体每个物种的取值请查阅 SPECIES.md。

---

## 五、添加新物种

只需在 `Species` 下添加对应模板名条目，代码无需改动。例如同时配置灰狼和马：

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter" ],
      "CubDurationDays": 4.5,
      "GestationSeconds": 1800.0,
      "AdultMaleBoxScale": 1.3,
      "MaleAttackBonus": 1.3
    },
    "Horse_White": {
      "BreedingSeasons": [ "Spring" ],
      "CubDurationDays": 6,
      "GestationSeconds": 2400.0,
      "AdultMaleBoxScale": 1.1,
      "MaleAttackBonus": 1.2,
      "CubAttackFactor": 0.2,
      "AdultAttackFactor": 0.5
    }
  }
}
```

> 模板名必须与 `Database.xml` 中的生物模板名完全一致（如 `Wolf_Gray`、`Horse_White`、`Hyena` 等）。
> 每个物种可以只写需要修改的参数，未写的会用默认值。

---

## 六、完整配置示例

```json
{
  "Enabled": true,
  "Species": {
    "Wolf_Gray": {
      "BreedingSeasons": [ "Winter" ],
      "CubDurationDays": 3,
      "GestationSeconds": 30.0,
      "MatingRequiredProximitySeconds": 10.0,
      "WeaknessSeconds": 60.0,
      "RivalChaseTime": 30.0,
      "MateRadius": 2.0,
      "SeekRadius": 20.0,
      "BirthSpawnOffset": 1.5,
      "CubAttackFactor": 0.3,
      "AdultAttackFactor": 1.0,
      "MaleAttackBonus": 1.3,
      "CubBoxScale": 0.5,
      "AdultMaleBoxScale": 1.3,
      "AdultFemaleBoxScale": 1.0,
      "EstrusChaseRangeMultiplier": 2.0,
      "CubMaleProbability": 0.5
    }
  }
}
```

---

## 七、配置加载机制

- **加载时机**：`OnProjectLoaded` 钩子中调用 `BreedingConfig.Load()`，即世界加载完成时。
- **加载方式（多源合并）**：
  1. 先通过 `ContentManager.Get<string>("BreedingConfig", ".json")` 读取**主配置** `MOD/Assets/BreedingConfig.json`，确定 `Enabled` 总开关。
  2. 再遍历 `ContentManager.List()`，找出所有**扩展配置** `BreedingConfig.{ModId}.json`，按文件名排序后逐个合并 `Species`。
- **缓存**：合并后存入 `BreedingConfig.Current` 静态属性，运行时直接读取。
- **重载**：修改任意配置后**退出世界重进**即可重新加载，无需重启游戏。
- **容错**：
  - 主配置为空 → 繁殖系统禁用（`Enabled=false`）。
  - 主配置解析失败 → 繁殖系统禁用，日志输出警告。
  - 扩展配置为空/无 Species/解析失败 → 跳过该扩展，不影响其他配置。
  - 未知季节字符串 → 忽略并日志警告。
  - `CubDurationDays <= 0` → 自动改为 3 天。
  - `Species` 为 null → 自动初始化为空字典。

---

## 八、第三方模组接入（多源配置）

本模组支持**多源配置合并**：其他模组可以自带一份繁殖配置文件，无需修改本模组代码、无需手动合并配置。

### 文件命名规则

| 类型 | 文件名 | 作用 |
|------|--------|------|
| 主配置 | `BreedingConfig.json` | 决定 `Enabled` 总开关 + 自带物种。仅本模组提供。 |
| 扩展配置 | `BreedingConfig.{ModId}.json` | 第三方模组自带，**仅追加 `Species`**。`{ModId}` 建议用模组唯一标识，避免重名。 |

> 例：`BreedingConfig.CowMod.json`、`BreedingConfig.HyenaPack.json`

### 合并规则

1. **先加载主配置** `BreedingConfig.json`，确定 `Enabled` 和主物种。
2. **再按文件名排序加载扩展配置**（顺序稳定，便于排查冲突）。
3. **同名模板冲突**：默认主配置优先，扩展冲突打 `Warning` 日志并跳过；若扩展配置根上开启 `"OverrideMain": true`，则改为**字段级覆盖**（见下方"覆盖模式"）。
4. **扩展配置中的 `Enabled` 字段被忽略**（防止第三方模组意外关闭整个繁殖系统）；`OverrideMain` 生效。
5. **扩展配置可省略所有非 Species 字段**，只写 `Species` 即可。

### 覆盖模式（OverrideMain=true，第三方提高原版属性）

默认扩展配置**只能追加新物种**。若第三方模组想**增强原版物种属性**（如让原版狼繁殖更快、体型更大、攻击力更高），在扩展配置根上开启：

```jsonc
{
  "OverrideMain": true,   // 开启后：本扩展中与主配置同名的物种，显式写了的字段直接覆盖主配置
  "Species": {
    "Wolf_Gray": {
      "GestationSeconds": 1200.0,   // 覆盖：孕期 1.5天 → 1天
      "CubDurationDays": 3,         // 覆盖：成长期 4.5天 → 3天
      "AdultMaleBoxScale": 1.5,     // 覆盖：公狼体型 1.3× → 1.5×
      "MaleAttackBonus": 1.5        // 覆盖：公狼攻击力 1.3× → 1.5×
      // 未写的字段(季节/密度/喂食等)保留主配置原值
    }
  }
}
```

**覆盖规则**：
- **字段级合并**：扩展里显式写了的字段覆盖主配置，**未写的字段保留主配置原值**（不是整块替换）；
- 集合字段（`BreedingSeasons`/`Aliases`/`CubTemplates`）：扩展写了非空列表则整体覆盖；
- 覆盖后会重新执行参数校验（`Normalize`），并输出日志 `扩展配置 xxx 覆盖物种 'Wolf_Gray'(OverrideMain=true)`。

> ⚠️ 覆盖模式会**改变主配置的物种参数**，属于"模组间增强"行为。建议在模组发布说明中告知玩家与繁殖模组的兼容方式；多个扩展同时开启 `OverrideMain` 覆盖同一物种时，按文件名排序**后加载者生效**。

### 第三方模组接入步骤

1. **确认生物满足前提**：
   - 模板已注册到 `DatabaseManager`（即 `entity.ValuesDictionary.DatabaseObject?.Name` 能拿到模板名）。
   - 生物有 `ComponentCreature` / `ComponentBody` / `ComponentSpawn` / `ComponentModel` / `ComponentFactors` 组件。
2. **在第三方模组的 `MOD/Assets/` 下**放一份 `BreedingConfig.{你的模组Id}.json`：
   ```json
   {
     "Species": {
       "Cow": {
         "BreedingSeasons": [ "Spring", "Summer" ],
         "CubDurationDays": 2,
         "GestationSeconds": 60.0,
         "AdultMaleBoxScale": 1.1,
         "MaleAttackBonus": 1.0,
         "CubAttackFactor": 0.2,
         "AdultAttackFactor": 0.5
       },
       "Bull": {
         "BreedingSeasons": [ "Autumn" ],
         "CubDurationDays": 4
       }
     }
   }
   ```
3. **打包发布**：用户同时安装本繁殖模组和你的模组即可，配置会自动合并。
4. **冲突排查**：游戏日志会输出每个扩展配置的合并结果，例如：
   ```
   [Breeding] 主配置加载完成，物种数=6，Enabled=True
   [Breeding] 发现 1 个扩展配置文件
   [Breeding] 扩展配置 BreedingConfig.CowMod.json 合并完成：新增 2 个物种，跳过 0 个冲突
   [Breeding] 全部配置合并完成，总物种数=8，Enabled=True
   ```
   若有冲突，会看到：
   ```
   [Breeding] 扩展配置 BreedingConfig.CowMod.json 的物种 'Wolf_Gray' 与主配置/先加载的扩展冲突，跳过
   ```

### 注意事项

- **默认只能追加新物种**：扩展配置不能修改主配置已有物种的参数；开启 `"OverrideMain": true` 后可字段级覆盖主配置物种（见上方"覆盖模式"）。
- **`{ModId}` 不要用 `Wolf_Gray` 这种模板名**，建议用模组包名/作者名，避免和别人的扩展重名导致排序混乱。
- **不写 `Enabled` 字段**：扩展配置写了也会被忽略，总开关只认主配置。
- **可向后兼容**：旧版本只读 `BreedingConfig.json`，扩展文件会被忽略，不会报错。

---

## 九、参数与代码对应关系

| 配置参数 | 代码位置 | 用途 |
|---------|---------|------|
| `Enabled` | `OnFactorsUpdate` / `OnEntityAdd` 等 | 全局开关 |
| `BreedingSeasons` | `OnFactorsUpdate` 发情判定 | 繁殖季节 |
| `CubDurationDays` | `UpdateGrowth` / `GetGrowthProgress` | 幼崽期天数 |
| `GestationSeconds` | `UpdateFemale` 交配成功时 | 设置孕期倒计时 |
| `MatingRequiredProximitySeconds` | `UpdateFemale` | 交配所需相处时间 |
| `WeaknessSeconds` | `UpdateFemale` 交配/分娩时 | 虚弱期时长 |
| `RivalChaseTime` | `UpdateMale` 竞争时 | 公体竞争追击时长 |
| `MateRadius` | `FindNearbyEstrusMale` | 交配判定半径 |
| `SeekRadius` | `UpdateMale` / `FindRival` | 公体寻路搜索半径 |
| `BirthSpawnOffset` | `GiveBirth` | 分娩幼崽偏移范围 |
| `CubAttackFactor` | `OnMinerHit` | 幼崽攻击力系数 |
| `AdultAttackFactor` | `OnMinerHit` | 成年攻击力系数 |
| `MaleAttackBonus` | `OnMinerHit` | 公体攻击力额外倍率 |
| `CubBoxScale` | `ApplyBoxSizeByGrowth` | 幼崽出生体型缩放 |
| `AdultMaleBoxScale` | `ApplyBoxSizeByGrowth` | 成年公体体型缩放 |
| `AdultFemaleBoxScale` | `ApplyBoxSizeByGrowth` | 成年母体体型缩放 |
| `EstrusChaseRangeMultiplier` | `ApplyChaseRangeFactor` | 发情期仇恨范围倍率 |
| `CubMaleProbability` | `OnEntityAdd` / `GiveBirth` | 公体生成概率 |
| `RequireFeeding` | `OnFactorsUpdate` 发情判定 | 是否需要喂食才发情 |
| `FeedItem` | `Normalize` 解析 / `OnEatPickable` 匹配 | 喂食物品方块类名 |
| `FedDurationSeconds` | `OnEatPickable` 喂食成功时 | 已喂食状态持续秒数 |
