using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统配置。对应 MOD/Assets/BreedingConfig.json(主配置)。
    /// 全局只保留总开关 Enabled，其余所有参数都按物种独立配置(Species)。
    /// 每个物种(Wolf_Gray 等)可自定义：孕期/体型/攻击力/交配半径/虚弱期等。
    ///
    /// 多源配置合并(方案B)：
    /// · 主配置 BreedingConfig.json — 决定 Enabled 总开关 + 自带物种
    /// · 扩展配置 BreedingConfig.{ModId}.json — 第三方模组自带，仅追加 Species
    /// · 同名模板：主配置优先；扩展之间按文件名排序，先到先得
    /// · 扩展配置中的 Enabled 字段被忽略(防止第三方关闭整个系统)
    /// </summary>
    public class BreedingConfig
    {
        /// <summary>全局总开关。false 时繁殖系统完全不生效。仅主配置 BreedingConfig.json 的值生效。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 扩展配置覆盖开关(仅扩展配置 BreedingConfig.{ModId}.json 有意义；主配置中此值被忽略)。
        /// true = 本扩展配置中的同名物种**直接覆盖**主配置(字段级合并：扩展里显式写了的字段覆盖主配置，
        ///        未写的字段保留主配置原值)，可用于第三方模组"提高原版属性"(如增强原版生物的繁殖/体型/攻击力)。
        /// false = 默认，主配置优先，同名冲突跳过。
        /// 注意：覆盖模式会改变主配置，请在文档/发布说明中告知玩家。
        /// </summary>
        public bool OverrideMain { get; set; } = false;

        /// <summary>按实体模板名索引的物种配置。每个物种独立设置所有繁殖参数。</summary>
        public Dictionary<string, SpeciesConfig> Species { get; set; } = new();

        // ==================== 加载与缓存 ====================

        public static BreedingConfig Current { get; private set; }

        /// <summary>默认物种配置参照(用于字段级覆盖合并时判断扩展配置是否显式设置了某字段)。</summary>
        static readonly SpeciesConfig s_defaultSpecies = new();

        /// <summary>
        /// 加载并合并所有 BreedingConfig*.json。
        /// 1) 先加载主配置 BreedingConfig.json(决定 Enabled + 主物种)
        /// 2) 再按文件名排序加载扩展配置 BreedingConfig.{ModId}.json(仅追加 Species)
        /// 同名模板主配置永远优先；扩展之间先到先得，冲突打 Warning 跳过。
        /// </summary>
        public static BreedingConfig Load()
        {
            try
            {
                JsonSerializerOptions opts = new()
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                // 1) 主配置 BreedingConfig.json — 决定 Enabled
                string mainJson = ContentManager.Get<string>("BreedingConfig", ".json");
                BreedingConfig cfg;
                if (string.IsNullOrEmpty(mainJson))
                {
                    Log.Warning("[Breeding] 主配置 BreedingConfig.json 内容为空，繁殖系统将禁用");
                    cfg = new BreedingConfig { Enabled = false };
                }
                else
                {
                    cfg = JsonSerializer.Deserialize<BreedingConfig>(mainJson, opts) ?? new BreedingConfig();
                }
                cfg.Species ??= new Dictionary<string, SpeciesConfig>();
                foreach (KeyValuePair<string, SpeciesConfig> kv in cfg.Species)
                {
                    kv.Value?.Normalize();
                    kv.Value?.SetSpeciesName(kv.Key);
                }
                Log.Information($"[Breeding] 主配置加载完成，物种数={cfg.Species.Count}，Enabled={cfg.Enabled}");

                // 2) 扩展配置 BreedingConfig.{ModId}.json — 仅追加 Species
                List<ContentInfo> extensions = ListExtensionConfigs();
                Log.Information($"[Breeding] 发现 {extensions.Count} 个扩展配置文件");
                foreach (ContentInfo ext in extensions)
                {
                    MergeExtension(cfg, ext, opts);
                }

                Current = cfg;
                Log.Information($"[Breeding] 全部配置合并完成，总物种数={cfg.Species.Count}，Enabled={cfg.Enabled}");
                return Current;
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 配置加载失败: " + e.Message);
                Current = new BreedingConfig { Enabled = false };
                return Current;
            }
        }

        /// <summary>
        /// 列出所有扩展配置文件(BreedingConfig.{ModId}.json)。
        /// 主配置 BreedingConfig.json 被排除。按 Filename 排序，保证合并顺序稳定。
        /// </summary>
        static List<ContentInfo> ListExtensionConfigs()
        {
            List<ContentInfo> result = new();
            foreach (ContentInfo info in ContentManager.List())
            {
                if (info == null || info.Filename == null) continue;
                // 必须以 .json 结尾
                if (!info.Filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                // 文件名去后缀后必须等于 BreedingConfig 或 BreedingConfig.{ModId}
                string stem = info.Filename.Substring(0, info.Filename.Length - ".json".Length);
                if (stem.Equals("BreedingConfig", StringComparison.OrdinalIgnoreCase)) continue; // 主配置跳过
                if (!stem.StartsWith("BreedingConfig.", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(info);
            }
            result.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// 合并单个扩展配置到主配置。
        /// · 扩展配置的 Enabled 被忽略(仅主配置可控制总开关)
        /// · Species 同名模板：主配置已有则跳过并 Warning，否则追加
        /// 用 ContentManager.Get<string> 读取(走标准 IContentReader 流程，比 Duplicate() 更可靠)。
        /// </summary>
        static void MergeExtension(BreedingConfig main, ContentInfo extInfo, JsonSerializerOptions opts)
        {
            try
            {
                // 用 Get<string> 读取，throwOnNotFound=false 避免抛异常
                string json = ContentManager.Get<string>(extInfo.ContentPath, extInfo.ContentSuffix, false);
                if (string.IsNullOrEmpty(json))
                {
                    Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 内容为空或读取失败，跳过");
                    return;
                }
                BreedingConfig ext = JsonSerializer.Deserialize<BreedingConfig>(json, opts);
                if (ext?.Species == null || ext.Species.Count == 0)
                {
                    Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 无 Species 条目，跳过");
                    return;
                }
                int added = 0, skipped = 0, overridden = 0;
                foreach (KeyValuePair<string, SpeciesConfig> kv in ext.Species)
                {
                    if (kv.Value == null) continue;

                    if (main.Species.ContainsKey(kv.Key))
                    {
                        if (!ext.OverrideMain)
                        {
                            Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 的物种 '{kv.Key}' 与主配置/先加载的扩展冲突，跳过(如要覆盖请在扩展配置根上设 OverrideMain=true)");
                            skipped++;
                            continue;
                        }

                        // 覆盖模式：字段级合并——扩展里显式写了的字段覆盖主配置，未写的保留主配置原值
                        SpeciesConfig existing = main.Species[kv.Key];
                        ApplySpeciesOverrides(existing, kv.Value);
                        existing.Normalize();
                        existing.SetSpeciesName(kv.Key);
                        overridden++;
                        Log.Information($"[Breeding] 扩展配置 {extInfo.Filename} 覆盖物种 '{kv.Key}'(OverrideMain=true)");
                        continue;
                    }

                    kv.Value.Normalize();
                    kv.Value.SetSpeciesName(kv.Key);
                    main.Species[kv.Key] = kv.Value;
                    added++;
                }
                Log.Information($"[Breeding] 扩展配置 {extInfo.Filename} 合并完成：新增 {added} 个物种，覆盖 {overridden} 个，跳过 {skipped} 个冲突");
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] 扩展配置 {extInfo.Filename} 解析失败: {e.Message}");
            }
        }

        /// <summary>
        /// 字段级覆盖合并(OverrideMain=true 时调用)：把 source 中**显式设置过**的字段覆盖到 target，
        /// 未显式设置的字段保留 target(主配置)原值。实现：用默认构造实例作参照，
        /// 扩展反序列化后与默认值不同的属性视为"显式设置"。
        /// </summary>
        static void ApplySpeciesOverrides(SpeciesConfig target, SpeciesConfig source)
        {
            if (target == null || source == null) return;
            try
            {
                foreach (System.Reflection.PropertyInfo prop in typeof(SpeciesConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    // 只处理公共可写属性
                    if (!prop.CanWrite || prop.SetMethod == null || !prop.SetMethod.IsPublic) continue;
                    // 跳过 [JsonIgnore] 运行时属性(只在代码内赋值)
                    if (Attribute.GetCustomAttribute(prop, typeof(JsonIgnoreAttribute)) != null) continue;

                    object srcVal = prop.GetValue(source);
                    if (srcVal == null) continue;

                    // 集合/字典类型(List/Dictionary)：非空才覆盖(深拷贝，避免共享引用)
                    if (srcVal is System.Collections.IEnumerable && srcVal is not string)
                    {
                        bool hasItems = false;
                        foreach (object _ in (System.Collections.IEnumerable)srcVal) { hasItems = true; break; }
                        if (!hasItems) continue;
                        if (srcVal is List<string> list) prop.SetValue(target, new List<string>(list));
                        else if (srcVal is Dictionary<string, float> dict) prop.SetValue(target, new Dictionary<string, float>(dict));
                        continue;
                    }

                    // 值类型/字符串：与默认构造值不同才覆盖(默认值 = 未在 JSON 中显式写出)
                    object defVal = prop.GetValue(s_defaultSpecies);
                    if (srcVal.Equals(defVal)) continue;
                    prop.SetValue(target, srcVal);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] ApplySpeciesOverrides 字段级合并失败: {e.Message}");
            }
        }

        public SpeciesConfig GetSpecies(string templateName)
        {
            if (string.IsNullOrEmpty(templateName)) return null;
            return Species.TryGetValue(templateName, out SpeciesConfig s) ? s : null;
        }
    }

    /// <summary>
    /// 单物种繁殖配置。所有繁殖参数都按物种独立设置。
    /// 这样不同生物可以有不同的孕期/体型/攻击力/交配半径等。
    /// </summary>
    public class SpeciesConfig
    {
        // ==================== 繁殖季节与成长 ====================

        /// <summary>1~2 个繁殖季节。可选: Summer / Autumn / Winter / Spring。</summary>
        public List<string> BreedingSeasons { get; set; } = new();

        /// <summary>幼崽期持续天数(游戏天)。到期后进阶成年。</summary>
        public float CubDurationDays { get; set; } = 3f;

        // ==================== 时间参数(现实秒) ====================

        /// <summary>孕期持续秒数。母体交配成功后此秒数分娩。</summary>
        public float GestationSeconds { get; set; } = 30.0f;

        /// <summary>交配所需相处时间。公母在 MateRadius 内持续相处此秒数后触发交配。</summary>
        public float MatingRequiredProximitySeconds { get; set; } = 10.0f;

        /// <summary>虚弱期持续秒数。交配后仅公体虚弱，分娩后母体虚弱。虚弱期间不发情。</summary>
        public float WeaknessSeconds { get; set; } = 60.0f;

        /// <summary>公体竞争时追击竞争对手的时长(现实秒)。</summary>
        public float RivalChaseTime { get; set; } = 30.0f;

        // ==================== 距离参数(方块) ====================

        /// <summary>交配判定半径。公母在此距离内持续相处才算交配。</summary>
        public float MateRadius { get; set; } = 2.0f;

        /// <summary>公体寻找母体的搜索半径。公体发情时在此范围内寻找母体并走过去。</summary>
        public float SeekRadius { get; set; } = 20.0f;

        /// <summary>分娩时幼崽在母体附近的随机偏移范围(方块)。</summary>
        public float BirthSpawnOffset { get; set; } = 1.5f;

        // ==================== 攻击力参数 ====================

        /// <summary>幼崽攻击力系数(与成年基准相乘)。</summary>
        public float CubAttackFactor { get; set; } = 0.3f;

        /// <summary>成年攻击力系数(基准1.0)。</summary>
        public float AdultAttackFactor { get; set; } = 1.0f;

        /// <summary>公体攻击力额外倍率(母体为1.0)。公=Adult×MaleBonus，母=Adult×1.0。</summary>
        public float MaleAttackBonus { get; set; } = 1.3f;

        // ==================== 体型参数 ====================

        /// <summary>幼崽出生时的体型缩放(相对原版模板 BoxSize/ModelScale)。</summary>
        public float CubBoxScale { get; set; } = 0.5f;

        /// <summary>成年公体体型缩放(相对原版)。</summary>
        public float AdultMaleBoxScale { get; set; } = 1.3f;

        /// <summary>成年母体体型缩放(相对原版)。</summary>
        public float AdultFemaleBoxScale { get; set; } = 1.0f;

        // ==================== 仇恨与性别参数 ====================

        /// <summary>发情期仇恨范围倍率(乘到 ChaseRange factor 上)。</summary>
        public float EstrusChaseRangeMultiplier { get; set; } = 2.0f;

        /// <summary>幼崽/自然生成个体的公体概率(0~1)。</summary>
        public float CubMaleProbability { get; set; } = 0.5f;

        // ==================== 区域密度参数(繁殖效率限制) ====================

        /// <summary>区域繁殖密度限制开关。true=开启，区域内同繁殖群个体越多、配对效率越低。</summary>
        public bool DensityEnabled { get; set; } = true;

        /// <summary>密度统计半径(方块)。以母体为中心统计此半径内的同繁殖群(含别名)成年个体数。</summary>
        public float DensityRadius { get; set; } = 32f;

        /// <summary>区域理想上限。同繁殖群个体数不超过此值时配对效率 100%。</summary>
        public float DensityLimit { get; set; } = 8f;

        /// <summary>
        /// 超过上限后每多一只的效率降低量(0~1)。
        /// 例: 0.15 = 每多一只配对效率 -15%，降到 0 封底(完全停止配对)。
        /// 效率作用于母体"相处计时"的累加速度：密度越高，计时增长越慢。
        /// </summary>
        public float DensityPenaltyStep { get; set; } = 0.15f;

        // ==================== 交互拦截(繁殖期/幼崽期禁止上鞍骑乘) ====================

        /// <summary>
        /// 繁殖期间(发情/怀孕/虚弱)是否禁止交互(上鞍+骑乘)。默认 true。
        /// 仅对可上鞍/可骑乘物种(Horse/Donkey/Camel/Reindeer/Ostrich)有意义。
        /// </summary>
        public bool BlockInteractDuringBreeding { get; set; } = true;

        /// <summary>
        /// 幼崽期是否禁止交互(上鞍+骑乘)。默认 true。
        /// 仅对可上鞍/可骑乘物种(Horse/Donkey/Camel/Reindeer/Ostrich)有意义。
        /// </summary>
        public bool BlockInteractDuringCub { get; set; } = true;

        /// <summary>
        /// 上鞍被拦截时是否仍消耗玩家手中的鞍。默认 false(不消耗，鞍退回)。
        /// true = 鞍被扣掉但马没上鞍(作为惩罚，玩家会看到"该生物无法上鞍"提示)。
        /// false = 鞍退回玩家背包，相当于上鞍操作完全取消。
        /// 注:原版 OnUse 在调用我们的 hook 之前不会扣鞍，所以此选项可控。
        /// </summary>
        public bool ConsumeSaddleOnBlocked { get; set; } = false;

        // ==================== 物种别名与幼崽模板 ====================

        /// <summary>
        /// 物种别名列表。当前模板可与此列表中的模板互相交配。
        /// 例: Cow 配 Aliases=["Bull"]，则 Cow(母)可和 Bull(公)交配；
        /// 反之 Bull 也需配 Aliases=["Cow"] 才能双向识别。幼崽模板由各自 CubTemplateOverride 决定。
        /// </summary>
        public List<string> Aliases { get; set; } = new();

        /// <summary>
        /// 幼崽生成时使用的模板名。空或 null = 沿用母体模板(默认)。
        /// 例: Cow 配 CubTemplateOverride="Cow" 可保证母牛只生小母牛(Cow 模板)，不会生 Bull；
        /// 不配则母牛生母牛、母公牛生公牛(沿用母体)。
        /// </summary>
        public string CubTemplateOverride { get; set; }

        /// <summary>
        /// 幼崽模板权重表(优先级高于 CubTemplateOverride)。
        /// 键=模板名，值=权重(非百分比，按相对比例计算)。
        /// 例: {"Cow": 1, "Bull": 1} 表示 50% 生 Cow，50% 生 Bull。
        /// 空/null = 回退到 CubTemplateOverride 或沿用母体。
        /// </summary>
        public Dictionary<string, float> CubTemplates { get; set; } = new();

        // ==================== 条件性繁衍(喂食发情) ====================

        /// <summary>
        /// 是否启用条件性繁衍。true 时，生物在繁殖季节内还必须被玩家喂食 FeedItem 指定的物品后才会发情。
        /// 默认 false(到季节自动发情)。建议对有养殖价值的物种开启。
        /// 注意: FeedItem 必须是该生物原版会吃的食物(匹配其 FoodFactors)，否则生物不会去吃，喂食钩子不会触发。
        /// </summary>
        public bool RequireFeeding { get; set; } = false;

        /// <summary>
        /// 喂食发情所需的物品(方块类名)。支持 "类名" 或 "类名:数据" 格式。
        /// 例: "RawMeatBlock"(生肉，狼吃) / "TallGrassBlock"(高草，牛马吃) / "PumpkinBlock"(南瓜，鸵鸟吃)。
        /// 留空或 null = 接受该生物原版会吃的任何食物(任何触发 OnEatPickable 的事件都算喂食)。
        /// 物品被生物吃掉后，该个体进入"已喂食"状态，持续 FedDurationSeconds 秒，期间可发情交配。
        /// </summary>
        public string FeedItem { get; set; }

        /// <summary>
        /// "已喂食"状态持续秒数(现实秒)。喂食后此秒数内可发情，到期后需再次喂食。
        /// 默认 600 秒(10 分钟)。设为 0 时自动回退为 600。
        /// </summary>
        public float FedDurationSeconds { get; set; } = 600.0f;

        // ==================== 运行时(不序列化) ====================

        [JsonIgnore]
        public HashSet<Season> ParsedSeasons { get; private set; } = new();

        /// <summary>
        /// 已解析的喂食物品方块索引。null = FeedItem 为空(接受任何食物)；>=0 = 指定方块索引；-1 = 解析失败。
        /// 由 Normalize() 在配置加载时解析。
        /// </summary>
        [JsonIgnore]
        public int? ParsedFeedBlockIndex { get; private set; }

        /// <summary>已解析的喂食物品方块数据约束。null = 不约束数据；>=0 = 必须匹配此数据值。</summary>
        [JsonIgnore]
        public int? ParsedFeedBlockData { get; private set; }

        /// <summary>
        /// 解析后的别名集合(含自身)，用于交配匹配。
        /// 例: Cow 的 MatingSet = {Cow, Bull}；Bull 的 MatingSet = {Bull, Cow}。
        /// 两个个体 MatingSet 有交集即可交配。
        /// </summary>
        [JsonIgnore]
        public HashSet<string> MatingSet { get; private set; } = new();

        public void Normalize()
        {
            BreedingSeasons ??= new List<string>();
            ParsedSeasons = new HashSet<Season>();
            foreach (string s in BreedingSeasons)
            {
                if (Enum.TryParse(s, ignoreCase: true, out Season season))
                {
                    ParsedSeasons.Add(season);
                }
                else
                {
                    Log.Warning($"[Breeding] 未知季节字符串: {s}，已忽略");
                }
            }
            if (CubDurationDays <= 0f) CubDurationDays = 3f;
            if (GestationSeconds <= 0f) GestationSeconds = 30f;
            if (MatingRequiredProximitySeconds <= 0f) MatingRequiredProximitySeconds = 10f;
            if (WeaknessSeconds < 0f) WeaknessSeconds = 60f;
            if (MateRadius <= 0f) MateRadius = 2f;
            if (SeekRadius <= 0f) SeekRadius = 20f;

            // 构建交配集合(含自身+别名)
            MatingSet = new HashSet<string>(StringComparer.Ordinal) { /* 自身名由外部 SetSpeciesName 填入 */ };
            Aliases ??= new List<string>();
            foreach (string alias in Aliases)
            {
                if (!string.IsNullOrEmpty(alias))
                {
                    MatingSet.Add(alias);
                }
            }
            CubTemplateOverride = string.IsNullOrEmpty(CubTemplateOverride) ? null : CubTemplateOverride;
            CubTemplates ??= new Dictionary<string, float>();
            // 移除权重<=0 或空模板名的条目
            var keysToRemove = new List<string>();
            foreach (var kv in CubTemplates)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0f) keysToRemove.Add(kv.Key);
            }
            foreach (var k in keysToRemove) CubTemplates.Remove(k);

            // 条件性繁衍参数校验
            if (FedDurationSeconds <= 0f) FedDurationSeconds = 600f;

            // 区域密度参数校验
            if (DensityRadius <= 0f) DensityRadius = 32f;
            if (DensityLimit <= 0f) DensityLimit = 8f;
            if (DensityPenaltyStep < 0f) DensityPenaltyStep = 0.15f;
            if (DensityPenaltyStep > 1f) DensityPenaltyStep = 1f;
            FeedItem = string.IsNullOrEmpty(FeedItem) ? null : FeedItem.Trim();
            ParsedFeedBlockIndex = null;
            ParsedFeedBlockData = null;
            if (FeedItem != null)
            {
                // 支持 "类名" 或 "类名:数据" 格式
                string[] parts = FeedItem.Split(':');
                string blockName = parts[0];
                int blockIdx = BlocksManager.GetBlockIndex(blockName, false);
                if (blockIdx < 0)
                {
                    Log.Warning($"[Breeding] FeedItem '{FeedItem}' 无法解析为方块类名，该物种喂食发情将无法匹配任何物品: {blockName}");
                    ParsedFeedBlockIndex = -1;
                }
                else
                {
                    ParsedFeedBlockIndex = blockIdx;
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int data))
                    {
                        ParsedFeedBlockData = data;
                    }
                }
            }
        }

        /// <summary>由 BreedingConfig.Normalize 阶段调用，把当前物种名加入 MatingSet。</summary>
        internal void SetSpeciesName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                MatingSet.Add(name);
            }
        }
    }
}
