# 动物繁殖系统模组 (Breeding) v1.0.0

为 Survivalcraft 2 (2.4.0.0) 加入完整动物繁殖系统的独立模组，支持 **39 个物种**按物种独立配置繁殖参数。

> **文档索引**：
> - [SPECIES.md 生物图鉴](SPECIES.md) — 39 物种完整参数表
> - [CONFIG.md 配置文档](CONFIG.md) — 全部参数含义与配置方法
> - [EXTENSION.md 模组扩展接口](EXTENSION.md) — 供第三方模组接入（疾病/草药系统等）

---

## 一、核心特色

| 类别 | 物种 | 特点 |
|------|------|------|
| 掠食者 | 灰狼 / 郊狼 / 鬣狗 | 冬/春季求偶，群居，无需喂食 |
| 大型猫科 | 狮 / 虎 / 白虎 / 豹 / 美洲豹 | 夏/秋季求偶，攻击力极高 |
| 熊类 | 灰熊 / 棕熊 / 黑熊 / 北极熊 | 夏/冬季求偶，无需喂食 |
| 骑乘动物 | 马(5变种) / 驴 / 骆驼 / 驯鹿 / 鸵鸟 / 鹤鸵 | 繁殖期/幼崽期禁止上鞍骑乘 |
| 家畜 | 母牛(3色) / 公牛(4色) / 羊驼(3变种) | Cow↔Bull 互通繁殖，按权重生幼崽 |
| 野生草食 | 角马 / 野牛 / 驼鹿 / 犀牛 / 长颈鹿 / 斑马 / 野猪 | 群居或独居，无需喂食 |

**核心机制**：
- 公体在求偶期主动寻找母体，相处达标后配对
- 多公追同一母时互相攻击（竞争）
- 母体进入孕期，期满产仔后进入恢复期
- 幼崽期 → 成年，体型随成长度线性增长
- 公体通常比母体更大、攻击力更高
- **条件性繁殖**：14 种家畜/骑乘物种需喂食特定物品（高草/南瓜）才进入求偶期；其余 25 种季节到即自动求偶
- **状态持久化**：性别、孕期、成长阶段等跨存档稳定保存，退出重进世界不丢失
- **区域密度限制**：区域内同物种成年个体越多，配对效率越低，防止无限繁殖
- **图鉴介绍**：游戏图鉴中内置 39 种生物介绍与攻击力/体型/孕期等基础信息（7 种语言）

---

## 二、快速开始

1. 进入世界，用刷怪蛋放置任意物种（如灰狼 `Wolf_Gray`）；
2. 将季节调到该物种的求偶季节（灰狼为冬季），观察头顶浮动文字；
3. 头顶文字显示：性别、成长阶段、繁殖状态（求偶中/孕期中/恢复中/需喂食）、成长进度条；
4. 打开图鉴可查看每种生物的详细介绍与繁殖属性。

---

## 三、配置

所有参数在 `MOD/Assets/BreedingConfig.json` 中按物种独立配置，**退出世界重进即生效**，无需重新编译。

- **参数含义**：详见 [CONFIG.md](CONFIG.md)
- **时间规则**：1 游戏天 = 1200 现实秒；大型动物孕期 2 天、小型 1.5 天；成长期 = 孕期 × 3；恢复期 = 孕期 ÷ 2
- **模组设置**：游戏内 **设置 → 模组设置 → 动物繁殖系统显示设置** 可开关头顶悬浮文字（默认开启），实时生效（`MOD/modsettings.json`）
- **第三方模组接入**：其他模组在 `MOD/Assets/` 放一份 `BreedingConfig.{ModId}.json` 即可追加物种配置；开启 `OverrideMain: true` 可覆盖主配置物种（提高原版属性），详见 [CONFIG.md](CONFIG.md) 与 [EXTENSION.md](EXTENSION.md)
- **图鉴生物介绍**：`MOD/Assets/Lang/` 内置 7 种语言的 39 种生物介绍（`SpeciesDescription`）+ 动态基础信息，显示在游戏图鉴中

---

## 四、安装与构建

### 直接使用 Release

到仓库 [Releases 页面](https://gitee.com/YonRen/Survivalcraft2-AnimalBreeding/releases) 下载 `.scmod` 文件，放到游戏 `Mods/` 目录即可。

### 从源码构建

1. 环境：Visual Studio / Rider / .NET SDK，目标框架 .NET Framework 4.8；
2. 克隆仓库，把游戏目录的 `Engine.dll` / `EntitySystem.dll` / `Survivalcraft.dll` 复制到 `Quoted/`；
3. 编译 `BreedingMod.csproj` 生成 `BreedingMod.dll`；
4. 把 `MOD/` 目录（含 DLL 与 `Assets/`）打包为 `.scmod`。

---

## 五、仓库结构

```
├── README.md                      # 本文件
├── CONFIG.md                      # 配置参数详细文档
├── SPECIES.md                     # 生物图鉴（39 物种参数表）
├── EXTENSION.md                   # 模组扩展接口指南
├── BreedingModLoader.cs           # 模组入口，注册 Hook
├── Breeding/                      # 繁殖系统核心
│   ├── SubsystemBreeding.cs       # 核心逻辑
│   ├── BreedingState.cs           # 单只动物运行时状态 + 序列化
│   └── BreedingConfig.cs          # 配置加载与缓存
├── MOD/
│   ├── modinfo.json               # 模组元信息
│   ├── modsettings.json           # 模组设置（悬浮文字开关）
│   └── Assets/
│       ├── BreedingConfig.json    # 运行配置（灰狼块含注释模板）
│       └── Lang/                  # 7 种语言翻译（悬浮文字 + 图鉴介绍）
└── Quoted/                        # 游戏程序集引用（需自行放入）
```

---

## 六、许可

本模组基于 [LICENSE](LICENSE) 开源发布，欢迎二次开发与接入。
