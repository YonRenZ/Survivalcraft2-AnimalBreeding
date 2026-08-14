using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Engine;
using GameEntitySystem;
using Game;
using XmlUtilities;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统核心管理器(简化版)。
    /// 机制：
    /// 1. 求偶期(IsInEstrus)：成年 + 当前季节在物种 BreedingSeasons 内 + 不在恢复期 +
    ///    条件性繁衍判定——需喂食物种(RequireFeeding=true)必须已喂食(FedRemainingSeconds>0)。
    ///    未喂食的需喂食物种不会进入求偶期，因此不参与寻路与求偶竞争。
    /// 2. 公狼寻路：求偶公狼在 SeekRadius 内寻找求偶母狼，设路径走向她。
    /// 3. 交配：母狼发情 + MateRadius 内有发情公狼 → 累加相处计时。
    ///    相处达 MatingRequiredProximitySeconds 秒 → 交配：母狼怀孕，双方进入虚弱期。
    /// 4. 分娩：孕期倒计时到 0 → 在母体附近生成幼崽。分娩后母狼进入虚弱期。
    /// 5. 成长：幼崽期 CubDurationDays 天后进阶成年。成长度 0→1 期间体型(BoxSize+ModelScale)线性增长。
    /// 6. 体型：原版BoxSize/ModelScale × scale。scale = lerp(CubBoxScale, 成年scale, 成长度)。
    ///    成年scale：公=AdultMaleBoxScale，母=AdultFemaleBoxScale。
    /// 7. 攻击力：幼崽×CubAttackFactor / 成年×AdultAttackFactor / 公额外×MaleAttackBonus。
    /// 8. 仇恨：幼崽/怀孕母狼 ChaseRange=0(不产生仇恨)；发情期 ×EstrusChaseRangeMultiplier。
    /// </summary>
    public static class SubsystemBreeding
    {
        // ==================== 运行时状态 ====================

        static readonly Dictionary<Entity, BreedingState> s_states = new();

        /// <summary>
        /// 上鞍撤销待恢复队列。
        /// 当原马(处于禁止交互状态)被 RemoveEntity 时(原版上鞍流程会先移除原马再 AddEntity Saddled马)，
        /// 把它的状态+位置暂存到此队列。后续 OnEntityAdd 收到 *_Saddled 实体时按位置+时间窗口匹配，
        /// 匹配成功则撤销上鞍(删 Saddled + 重建原马 + 恢复状态)。
        /// 队列项超过 5 秒未匹配自动清理。
        /// </summary>
        static readonly List<PendingSaddleRevert> s_pendingReverts = new();

        /// <summary>
        /// ProjectXmlLoad 缓存的活体生物状态(EntityId → Base64 JSON)。
        /// 活着的生物(在视野内、未被 Despawn)通过 Project.LoadEntities 恢复，不走 OnReadSpawnData，
        /// 其繁殖状态只存在于内存 s_states，退出世界时会丢失。
        /// 此缓存由 ProjectXmlLoad 钩子从 Project.xml 的 &lt;BreedingModStates&gt; 节点读取，
        /// 在 Initialize backfill 阶段按 EntityId 恢复，backfill 完成后清空。
        /// </summary>
        static readonly Dictionary<int, string> s_xmlCachedStates = new();

        /// <summary>
        /// 当前世界目录路径(Initialize 时缓存，用于 OnProjectDisposed 时保存到单独文件)。
        /// 作为 ProjectXmlSave/OnProjectXmlSaved 钩子不可用时的备选保存路径。
        /// </summary>
        static string s_worldDirectory;

        // ==================== 缓存的子系统 ====================

        static Project s_project;
        static SubsystemCreatureSpawn s_creatureSpawn;
        static SubsystemBodies s_bodies;
        static SubsystemTimeOfDay s_timeOfDay;
        static SubsystemTime s_time;
        static SubsystemModelsRenderer s_modelsRenderer;
        static Random s_random = new();
        static bool s_initialized;

        /// <summary>当前 Project 实例(联机版用于注册/注销 IUpdateable 每帧更新)。</summary>
        public static Project ProjectInstance => s_project;

        /// <summary>渲染钩子(OnModelDrawExtra)用它获取 FontBatch 入队悬浮文字。</summary>
        public static SubsystemModelsRenderer ModelsRenderer => s_modelsRenderer;

        /// <summary>体型更新节流计数器(每 60 帧更新一次体型，避免每帧写 BoxSize)。</summary>
        static long s_debugFrameCounter;

        /// <summary>
        /// 由 BreedingModLoader.OnProjectLoaded 调用，缓存子系统引用并加载配置。
        /// 注意：ModLoader 是单例，静态字段跨世界保留，必须在此清空旧世界的残留状态。
        /// </summary>
        public static void Initialize(Project project)
        {
            // 保存 OnReadSpawnData 已缓存的本世界存档状态。
            // OnReadSpawnData 在 Initialize 之前被引擎调用(SubsystemCreatureSpawn.LoadSpawnsData 阶段)，
            // 此时已把反序列化的存档状态(性别/出生日/成长阶段等)缓存到 s_states。
            // 下面 Clear 会清空旧世界残留，所以先保存本世界的存档状态，Clear 后只恢复属于当前项目实体的状态。
            Dictionary<Entity, BreedingState> cachedFromSpawn = s_states.Count > 0
                ? new Dictionary<Entity, BreedingState>(s_states)
                : null;

            // 清空旧世界残留(静态字段跨世界保留，不清空会导致旧 Entity 引用泄漏)
            s_states.Clear();
            s_pendingReverts.Clear();
            s_initialized = false;

            s_project = project;
            s_creatureSpawn = project.FindSubsystem<SubsystemCreatureSpawn>(true);
            s_bodies = project.FindSubsystem<SubsystemBodies>(true);
            s_timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            s_time = project.FindSubsystem<SubsystemTime>(true);
            s_modelsRenderer = project.FindSubsystem<SubsystemModelsRenderer>(true);

            BreedingConfig.Load();
            BreedingConfig cfg = BreedingConfig.Current;

            // 缓存世界目录路径(用于 OnProjectDisposed 时保存到单独文件)
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            s_worldDirectory = gameInfo?.DirectoryName;

            if (cfg?.Enabled == true)
            {
                Log.Information($"[Breeding] 初始化完成，追踪物种数={cfg.Species.Count}");
            }
            else
            {
                Log.Warning("[Breeding] 配置禁用或加载失败，繁殖系统不生效");
            }
            s_initialized = true;

            // 恢复 OnReadSpawnData 缓存的本世界存档状态(仅限当前项目的实体，过滤旧世界残留)。
            // 注意：XML 缓存(s_xmlCachedStates)里的状态是"退出世界时写入的最新权威状态"，
            // 若实体在 XML 缓存中有条目，这里跳过，统一交给 backfill 情况2(优先)处理，
            // 避免 OnReadSpawnData 的旧数据抢先写入 s_states 造成性别回退。
            if (cachedFromSpawn != null && project.Entities != null)
            {
                foreach (Entity e in project.Entities)
                {
                    if (cachedFromSpawn.TryGetValue(e, out BreedingState s))
                    {
                        if (s_xmlCachedStates.ContainsKey(e.EntityId))
                        {
                            continue;
                        }
                        s_states[e] = s;
                    }
                }
            }

            // 备选加载：如果 ProjectXmlLoad 钩子未被调用(旧版 DLL 可能不支持)，
            // s_xmlCachedStates 为空。此时直接从 Project.xml 文件读取 BreedingModStates 节点。
            if (s_xmlCachedStates.Count == 0 && cfg?.Enabled == true)
            {
                try
                {
                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.DirectoryName))
                    {
                        string projectXmlPath = Storage.CombinePaths(gameInfo.DirectoryName, "Project.xml");
                        if (Storage.FileExists(projectXmlPath))
                        {
                            Log.Information("[Breeding] ProjectXmlLoad 钩子未缓存数据，尝试直接读取 Project.xml 文件");
                            using (System.IO.Stream stream = Storage.OpenFile(projectXmlPath, OpenFileMode.Read))
                            {
                                XElement projectNode = XmlUtils.LoadXmlFromStream(stream, null, true);
                                LoadXmlStates(projectNode);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[Breeding] 直接读取 Project.xml 失败: {e.Message}");
                }
            }

            // 备选加载2：如果 Project.xml 中也没有 BreedingModStates(保存钩子未被调用)，
            // 尝试从单独文件 BreedingStates.xml 读取(由 OnProjectDisposed 备选保存)。
            if (s_xmlCachedStates.Count == 0 && cfg?.Enabled == true)
            {
                LoadStatesFromFile();
            }

            // 补注册在 Initialize 之前已 AddEntity 的实体
            // (Project.LoadEntities → OnEntityAdd 在 OnProjectLoaded/Initialize 之前触发，
            //  此时 s_initialized=false 导致 OnEntityAdd 跳过。这里遍历补建)
            // 两种情况：
            //   1. s_states 已有缓存(OnReadSpawnData 在 Initialize 前缓存)：校验模板名 + 补应用体型
            //   2. s_xmlCachedStates 有缓存(活着的生物，Project.xml 持久化)：反序列化 + 校验 + 应用体型
            //   3. 无任何存档(新生物/首次生成)：按自然生成成体初始化
            if (cfg?.Enabled == true && project.Entities != null)
            {
                int backfilled = 0;
                int hit1 = 0, hit2 = 0, hit3 = 0; // 诊断：各情况命中次数
                foreach (Entity existing in project.Entities)
                {
                    ComponentCreature creature = existing.FindComponent<ComponentCreature>();
                    if (creature == null) continue;
                    string tn = existing.ValuesDictionary.DatabaseObject?.Name;
                    if (string.IsNullOrEmpty(tn)) continue;
                    string normTn = NormalizeTemplateName(tn);
                    SpeciesConfig sp = cfg.GetSpecies(normTn);
                    if (sp == null) continue;

                    // ========== 恢复优先级(重要，勿乱改) ==========
                    // 情况2(XML缓存) 优先于 情况1(OnReadSpawnData缓存)：
                    //   · s_xmlCachedStates 来自 <BreedingModStates>，是"上次退出世界时"写入的最新权威状态；
                    //   · s_states 里的 OnReadSpawnData 旧数据可能来自更早的 Despawn 时刻(性别已被
                    //     随机重掷/过期)，且旧会话残留还会污染 cachedFromSpawn。
                    //   若情况1 优先，会出现"XML 缓存有正确性别却用不上"→ 重进世界性别回退。

                    // 情况2：从 Project.xml 的 <BreedingModStates> 恢复(活着的生物，权威存档)
                    if (s_xmlCachedStates.TryGetValue(existing.EntityId, out string xmlData))
                    {
                        BreedingState xmlState = BreedingState.Deserialize(xmlData);
                        if (xmlState != null
                            && string.Equals(xmlState.TemplateName, normTn, StringComparison.Ordinal))
                        {
                            s_states[existing] = xmlState;
                            CacheAndApplyBoxSize(existing, xmlState, cfg);
                            backfilled++;
                            hit2++;
                            continue;
                        }
                        // 反序列化失败或模板名不匹配 → 落入情况1/情况3
                    }

                    // 情况1：OnReadSpawnData 已缓存存档状态(仅当 XML 缓存无权威条目时使用) → 校验模板名 + 补应用体型
                    if (s_states.TryGetValue(existing, out BreedingState cached))
                    {
                        if (!string.Equals(cached.TemplateName, normTn, StringComparison.Ordinal))
                        {
                            // 模板名不匹配：丢弃旧状态，落入情况3确定性分配
                            s_states.Remove(existing);
                        }
                        else
                        {
                            CacheAndApplyBoxSize(existing, cached, cfg); // 补缓存 OriginalBoxSize + 应用体型
                            backfilled++;
                            hit1++;
                            continue;
                        }
                    }

                    // 情况3：无任何存档(新生物/首次生成) → 按自然生成成体初始化。
                    // 性别用 EntityId 确定性分配：即使缓存全部丢失，同一只生物重进世界性别也不变。
                    BreedingState st = new()
                    {
                        TemplateName = normTn,
                        Gender = RollGender(existing, sp),
                        Stage = GrowthStage.Adult,
                        BirthDay = s_timeOfDay.Day,
                        PregnancyRemainingSeconds = -1f,
                        WeaknessRemainingSeconds = -1f
                    };
                    s_states[existing] = st;
                    CacheAndApplyBoxSize(existing, st, cfg);
                    backfilled++;
                    hit3++;
                }
                Log.Information($"[Breeding][读档] backfill 完成: 总数={backfilled}, 情况1(OnReadSpawnData)={hit1}, 情况2(XML)={hit2}, 情况3(确定性)={hit3}, xmlCached={s_xmlCachedStates.Count}");

                // 诊断：XML 缓存中有存档但当前项目找不到对应 EntityId 实体(孤儿条目)
                if (s_xmlCachedStates.Count > 0)
                {
                    List<int> orphanIds = s_xmlCachedStates.Keys
                        .Where(id => !project.Entities.Any(e => e.EntityId == id))
                        .ToList();
                    if (orphanIds.Count > 0)
                    {
                        Log.Warning($"[Breeding][诊断] XML 缓存孤儿条目(存档有但当前项目无此 EntityId 的实体): EntityId=[{string.Join(",", orphanIds)}]，对应性别=[{string.Join(",", orphanIds.Select(id => GenderOfSerialized(s_xmlCachedStates[id])))}]。可能原因: 该生物保存时活着、重载时已被 Despawn 或 EntityId 发生变化");
                    }
                }

            }

            // backfill 完成，XML 缓存不再需要
            s_xmlCachedStates.Clear();
        }

        // ==================== Project.xml 持久化(活着的生物状态) ====================

        /// <summary>
        /// ProjectXmlLoad 钩子：世界加载时从 Project.xml 读取活体生物的繁殖状态。
        /// 活着的生物(在视野内、未被 Despawn)通过 Project.LoadEntities 恢复，不走 OnReadSpawnData，
        /// 其繁殖状态需通过 Project.xml 的 &lt;BreedingModStates&gt; 节点持久化。
        /// 此方法在 ProjectData 构造(实体创建)之前触发，数据缓存到 s_xmlCachedStates，
        /// 供 Initialize backfill 按 EntityId 恢复。
        /// </summary>
        public static void LoadXmlStates(XElement projectNode)
        {
            s_xmlCachedStates.Clear();
            if (projectNode == null)
            {
                Log.Warning("[Breeding] LoadXmlStates: projectNode 为 null");
                return;
            }

            XElement statesNode = projectNode.Element("BreedingModStates");
            if (statesNode == null)
            {
                Log.Information("[Breeding] LoadXmlStates: Project.xml 中无 BreedingModStates 节点(首次进入或上次保存失败)");
                return;
            }

            int count = 0;
            foreach (XElement stateEl in statesNode.Elements("State"))
            {
                int entityId = XmlUtils.GetAttributeValue(stateEl, "EntityId", 0);
                string data = XmlUtils.GetAttributeValue(stateEl, "Data", string.Empty);
                if (entityId != 0 && !string.IsNullOrEmpty(data))
                {
                    s_xmlCachedStates[entityId] = data;
                    count++;
                }
            }
            Log.Information($"[Breeding][读档] LoadXmlStates: 从 Project.xml 读取 {count} 个活体生物状态");
        }

        /// <summary>
        /// OnProjectXmlSaved 钩子：世界保存时把活着的生物的繁殖状态写入 Project.xml。
        /// 被 Despawn 的生物已通过 OnSaveSpawnData → SpawnEntityData.Data → SubsystemSpawn.Save 保存，
        /// 不在此处理。此处只处理 s_states 中仍然存活的生物(未被 Despawn)。
        /// </summary>
        public static void SaveXmlStates(XElement projectNode)
        {
            try
            {
                if (projectNode == null)
                {
                    Log.Warning("[Breeding] SaveXmlStates: projectNode 为 null");
                    return;
                }

                // 移除旧节点(避免重复，ProjectXmlSave 和 OnProjectXmlSaved 都会调用此方法)
                projectNode.Element("BreedingModStates")?.Remove();

                // 快照拷贝：InternalSaveProject 在后台线程(Task.Run)执行本方法，
                // 主线程可能同时增删 s_states，直接迭代会抛 InvalidOperationException，
                // 导致整个 Project.xml 保存失败。先拷贝快照再遍历。
                KeyValuePair<Entity, BreedingState>[] snapshot = s_states.ToArray();


                if (snapshot.Length == 0) return;

                XElement statesNode = new("BreedingModStates");
                foreach (KeyValuePair<Entity, BreedingState> kv in snapshot)
                {
                    Entity entity = kv.Key;
                    BreedingState state = kv.Value;
                    if (entity == null || state == null) continue;

                    XElement stateEl = new("State");
                    XmlUtils.SetAttributeValue(stateEl, "EntityId", entity.EntityId);
                    XmlUtils.SetAttributeValue(stateEl, "Data", state.Serialize());
                    statesNode.Add(stateEl);
                }

                if (statesNode.HasElements)
                {
                    projectNode.Add(statesNode);
                    Log.Information($"[Breeding] SaveXmlStates: 写入 {statesNode.Elements().Count()} 个活体生物状态到 Project.xml");
                }
                else
                {
                    Log.Warning("[Breeding] SaveXmlStates: s_states 非空但无有效条目可写入");
                }
            }
            catch (Exception e)
            {
                // 繁殖状态保存失败绝不允许拖垮整个世界保存(OnProjectXmlSaved 抛异常会导致 Project.xml 不落盘)
                Log.Warning($"[Breeding] SaveXmlStates 失败(不影响世界本体保存): {e.Message}");
            }
        }

        /// <summary>OnProjectDisposed 钩子：世界卸载时保存活体状态到单独文件 + 清空缓存。</summary>
        public static void ClearXmlCache()
        {
            // 备选保存：如果 ProjectXmlSave/OnProjectXmlSaved 钩子未被调用(旧版 DLL)，
            // 在此把活体生物状态保存到单独文件 BreedingStates.xml。
            // OnProjectDisposed 在 Project.Dispose() 之后触发，但 s_states 仍保留数据
            // (entity.EntityId 是 int 字段不受 Dispose 影响，state.Serialize() 不依赖 Entity)。
            SaveStatesToFile();
            s_xmlCachedStates.Clear();

            // 关键修复：卸载世界时必须清空 s_states 和上鞍暂存队列！
            // 静态字段跨世界保留，若不清空，下个会话 Initialize 时 cachedFromSpawn 会带着
            // 上个会话已 Dispose 的旧 Entity 引用(且旧性别可能已被随机重掷)，导致：
            //   1) s_states 出现重复 EntityId(如 [16,19,23,23,16,19])；
            //   2) backfill 情况1(旧数据)抢先于情况2(权威 XML 缓存)，恢复出错误性别。
            s_states.Clear();
            s_pendingReverts.Clear();
            // 同时复位 s_initialized：否则下个会话加载阶段(OnProjectLoaded 之前)OnEntityAdd
            // 会因 s_initialized 残留 true 而抢先按 RollGender 注册状态，虽然会被 backfill 情况2
            // 覆盖，但违反"加载期 OnEntityAdd 跳过、全部交给 backfill"的设计意图。
            s_initialized = false;
            Log.Information("[Breeding][存档] ClearXmlCache: 完成(已清空 s_states/s_pendingReverts，s_initialized 已复位)");
        }

        /// <summary>
        /// 把 s_states 保存到单独文件 BreedingStates.xml(备选保存方案；联机版主保存通道)。
        /// 文件路径：{世界目录}/BreedingStates.xml
        /// </summary>
        public static void SaveStatesToFile()
        {
            try
            {
                if (string.IsNullOrEmpty(s_worldDirectory))
                {
                    return;
                }

                // 快照拷贝，避免与主线程增删 s_states 冲突(OnProjectDisposed 时游戏可能仍在收尾)
                KeyValuePair<Entity, BreedingState>[] snapshot = s_states.ToArray();
                if (snapshot.Length == 0)
                {
                    return;
                }

                XElement root = new("BreedingStates");
                List<Entity> entities = new();
                int count = 0;
                foreach (KeyValuePair<Entity, BreedingState> kv in snapshot)
                {
                    if (kv.Key == null || kv.Value == null) continue;
                    XElement el = new("State");
                    XmlUtils.SetAttributeValue(el, "EntityId", kv.Key.EntityId);
                    XmlUtils.SetAttributeValue(el, "Data", kv.Value.Serialize());
                    root.Add(el);
                    entities.Add(kv.Key);
                    count++;
                }

                if (count > 0)
                {
                    string path = Storage.CombinePaths(s_worldDirectory, "BreedingStates.xml");
                    using (System.IO.Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    {
                        XmlUtils.SaveXmlToStream(root, stream, null, true);
                    }
                    Log.Information($"[Breeding] SaveStatesToFile: 保存 {count} 个状态到 BreedingStates.xml，实体ID列表=[{string.Join(",", entities.Select(e => e.EntityId.ToString()))}]");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] SaveStatesToFile 失败: {e.Message}");
            }
        }

        /// <summary>
        /// 从单独文件 BreedingStates.xml 读取活体状态(备选加载方案)。
        /// 读取到 s_xmlCachedStates，供 backfill 使用。
        /// </summary>
        static void LoadStatesFromFile()
        {
            if (string.IsNullOrEmpty(s_worldDirectory)) return;

            try
            {
                string path = Storage.CombinePaths(s_worldDirectory, "BreedingStates.xml");
                if (!Storage.FileExists(path))
                {
                    Log.Information("[Breeding] LoadStatesFromFile: BreedingStates.xml 不存在");
                    return;
                }

                using (System.IO.Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                {
                    XElement root = XmlUtils.LoadXmlFromStream(stream, null, true);
                    int count = 0;
                    foreach (XElement el in root.Elements("State"))
                    {
                        int entityId = XmlUtils.GetAttributeValue(el, "EntityId", 0);
                        string data = XmlUtils.GetAttributeValue(el, "Data", string.Empty);
                        if (entityId != 0 && !string.IsNullOrEmpty(data))
                        {
                            s_xmlCachedStates[entityId] = data;
                            count++;
                        }
                    }
                    Log.Information($"[Breeding][读档] LoadStatesFromFile: 从 BreedingStates.xml 读取 {count} 个状态");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] LoadStatesFromFile 失败: {e.Message}");
            }
        }

        // ==================== 实体生命周期钩子 ====================

        public static void OnEntityAdd(Entity entity)
        {
            if (!s_initialized || entity == null) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            if (string.IsNullOrEmpty(templateName)) return;

            string normalizedTemplate = NormalizeTemplateName(templateName);

            // 上鞍撤销：如果新增的是 *_Saddled 实体且待恢复队列有匹配项 → 撤销上鞍
            if (templateName.EndsWith("_Saddled", StringComparison.Ordinal))
            {
                if (TryConsumePendingRevert(entity, templateName, out PendingSaddleRevert revert))
                {
                    RevertSaddling(entity, revert, cfg);
                    return; // 撤销后该 Saddled 实体已被删除，不再处理
                }
                // 无匹配项 = 正常上鞍(原马不处于禁止状态)，继续按带鞍模板注册
            }

            // 归一化模板名：带鞍的马/驴/骆驼等(*_Saddled)去掉后缀后查找配置
            // 这样带鞍和不带鞍的同类可互通交配，幼崽不带鞍(用 base 模板生成)
            SpeciesConfig species = cfg.GetSpecies(normalizedTemplate);
            if (species == null) return;

            if (s_states.ContainsKey(entity))
            {
                // OnReadSpawnData 已恢复存档状态，这里保留不覆盖
                return;
            }

            // 自然生成的成体：默认成年。性别用 EntityId 确定性分配(见 RollGender)，
            // 保证同一实体无论何时被追踪/恢复，性别都一致，不随重进世界/Despawn 循环变化。
            // TemplateName 存归一化后的名字(不带 _Saddled)，便于交配匹配和体型查找
            BreedingState state = new()
            {
                TemplateName = normalizedTemplate,
                Gender = RollGender(entity, species),
                Stage = GrowthStage.Adult,
                BirthDay = s_timeOfDay.Day,
                PregnancyRemainingSeconds = -1f,
                WeaknessRemainingSeconds = -1f
            };
            s_states[entity] = state;

            // 缓存原版 BoxSize/ModelScale 并应用成年体型
            CacheAndApplyBoxSize(entity, state, cfg);
        }

        /// <summary>
        /// 归一化模板名：去掉 _Saddled 后缀。
        /// 例: "Horse_Black_Saddled" → "Horse_Black"
        /// 非带鞍模板原样返回。
        /// </summary>
        static string NormalizeTemplateName(string templateName)
        {
            if (string.IsNullOrEmpty(templateName)) return templateName;
            const string suffix = "_Saddled";
            if (templateName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return templateName.Substring(0, templateName.Length - suffix.Length);
            }
            return templateName;
        }

        // ==================== 确定性性别分配(防退出重进性别变化) ====================

        /// <summary>
        /// 以实体 Id(EntityId) 为种子确定性分配性别，替代 Random 随机兜底。
        ///
        /// 背景：性别变化问题的根因是"缓存缺失时用 Random 兜底"——每次世界重载/Despawn-Respawn
        /// 循环中，只要某只生物的状态缓存没命中(保存钩子未触发、保存线程竞争、模板不匹配、
        /// 首次被本模组追踪等)，就会重新随机掷一次性别，约 50% 概率翻转已有生物的性别。
        ///
        /// SC 中 EntityId 是每只生物的稳定身份：
        ///   · Despawn 时写入 SpawnEntityData.EntityId，Respawn 时 CreateEntity(valuesDictionary, EntityId) 恢复；
        ///   · 世界保存时写入 Project.xml Entities/@Id，重载时按 Id 重建实体。
        /// 因此同一只生物无论走哪条恢复路径、无论缓存是否丢失，EntityId 都保持不变。
        /// 用 EntityId 的稳定哈希作性别种子后，性别成为该生物的不变量——
        /// 彻底杜绝"退出重进世界性别变化"，即使所有存档缓存全部丢失也保持稳定。
        /// 统计意义上仍按 CubMaleProbability 分布(0~1)。
        /// </summary>
        static BreedingGender RollGender(Entity entity, SpeciesConfig species)
        {
            float maleProbability = Math.Clamp(species?.CubMaleProbability ?? 0.5f, 0f, 1f);
            if (entity == null)
            {
                // 理论不可达：所有调用点实体都已 AddEntity 并分配了 Id
                BreedingGender fallback = s_random.Bool(maleProbability) ? BreedingGender.Male : BreedingGender.Female;
                return fallback;
            }

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            uint hash = StableHash(entity.EntityId, templateName);
            // 取哈希高 24 位映射到 [0,1)，按概率阈值判定公/母
            uint bucket = (hash >> 8) & 0xFFFFFFu;
            float normalized = bucket / 16777215f;
            BreedingGender result = normalized < maleProbability ? BreedingGender.Male : BreedingGender.Female;
            return result;
        }

        /// <summary>
        /// 稳定的 32 位哈希：混合 EntityId 与模板名，再做 lowbias32 风格 finalizer，
        /// 保证连续 EntityId 也能得到均匀分布(不会出现 ID 相邻性别扎堆的规律)。
        /// </summary>
        static uint StableHash(int entityId, string templateName)
        {
            unchecked
            {
                uint h = (uint)entityId;
                if (!string.IsNullOrEmpty(templateName))
                {
                    foreach (char c in templateName)
                    {
                        h = h * 31u + c;
                    }
                }
                h ^= h >> 16;
                h *= 0x7feb352du;
                h ^= h >> 15;
                h *= 0x846ca68bu;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>
        /// 反序列化存档串并返回性别字符串(用于日志；失败返回 "?")。
        /// </summary>
        static string GenderOfSerialized(string data)
        {
            try
            {
                BreedingState st = BreedingState.Deserialize(data);
                return st != null ? st.Gender.ToString() : "反序列化失败";
            }
            catch
            {
                return "异常";
            }
        }

        public static void OnEntityRemove(Entity entity)
        {
            if (entity == null) return;

            bool hadState = s_states.TryGetValue(entity, out BreedingState state);

            // 上鞍撤销暂存：仅当被移除的是"活的、处于禁止交互状态、配置了交互拦截的可骑乘物种"时暂存。
            // 过滤条件说明：
            //   1. 物种必须配置了 BlockInteractDuringBreeding 或 BlockInteractDuringCub（否则上鞍不会被拦截，无需暂存）
            //   2. 实体必须处于禁止交互状态（繁殖期或幼崽期）
            //   3. 实体必须是活的（Health 为 null 或 > 0），排除死亡移除（被打死/烧死等不会是上鞍）
            if (s_initialized
                && hadState
                && s_time != null)
            {
                BreedingConfig cfg = BreedingConfig.Current;
                SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
                if (species != null
                    && (species.BlockInteractDuringBreeding || species.BlockInteractDuringCub)
                    && IsInteractBlocked(state, species)
                    && IsAlive(entity))
                {
                    ComponentBody body = entity.FindComponent<ComponentBody>();
                    if (body != null)
                    {
                        s_pendingReverts.Add(new PendingSaddleRevert
                        {
                            OriginalTemplate = state.TemplateName,
                            Position = body.Position,
                            Rotation = body.Rotation,
                            Velocity = body.Velocity,
                            State = state,
                            QueuedAtSeconds = (float)s_time.GameTime
                        });
                        s_states.Remove(entity);
                        return;
                    }
                }
            }

            if (hadState)
            {
                s_states.Remove(entity);
            }
        }

        /// <summary>
        /// 判断实体是否存活(用于区分上鞍移除 vs 死亡移除)。
        /// 上鞍时原版检查 componentHealth == null || health > 0f，所以上鞍的实体是活的。
        /// 死亡移除时 Health <= 0 或 DeathTime 有值。
        /// </summary>
        static bool IsAlive(Entity entity)
        {
            if (entity == null) return false;
            ComponentHealth health = entity.FindComponent<ComponentHealth>();
            if (health == null) return true; // 无血量组件 = 不会死亡 = 视为活
            if (health.DeathTime.HasValue) return false; // 已死亡
            return health.Health > 0f;
        }

        // ==================== 上鞍撤销(无 hook，用 OnEntityAdd 撤销法) ====================

        /// <summary>
        /// 判断当前状态是否禁止交互(上鞍+骑乘)。
        /// 繁殖期(发情/怀孕/虚弱) 或 幼崽期，按物种配置决定。
        /// </summary>
        static bool IsInteractBlocked(BreedingState state, SpeciesConfig species)
        {
            if (state == null || species == null) return false;
            if (state.Stage == GrowthStage.Cub && species.BlockInteractDuringCub) return true;
            if (species.BlockInteractDuringBreeding && IsInBreedingState(state)) return true;
            return false;
        }

        /// <summary>是否处于繁殖期(发情/怀孕/虚弱)。</summary>
        static bool IsInBreedingState(BreedingState state)
        {
            if (state == null) return false;
            if (state.IsInEstrus) return true;
            if (state.PregnancyRemainingSeconds > 0f) return true;
            if (state.IsWeak) return true;
            return false;
        }

        /// <summary>
        /// 尝试从待恢复队列消费一个匹配项。
        /// 匹配条件：Saddled 实体位置与暂存位置距离 ≤ 2 格，且暂存时间 ≤ 5 秒。
        /// 匹配后从队列移除。返回 true 表示找到匹配项。
        /// </summary>
        static bool TryConsumePendingRevert(Entity saddledEntity, string saddledTemplate, out PendingSaddleRevert matched)
        {
            matched = null;
            if (s_pendingReverts.Count == 0 || s_time == null) return false;

            // saddledTemplate 形如 "Horse_White_Saddled"，去掉 _Saddled 后缀得到原模板 "Horse_White"
            string expectedOriginal = saddledTemplate.Substring(0, saddledTemplate.Length - "_Saddled".Length);

            ComponentBody body = saddledEntity.FindComponent<ComponentBody>();
            if (body == null) return false;
            Vector3 pos = body.Position;

            float now = (float)s_time.GameTime;
            for (int i = s_pendingReverts.Count - 1; i >= 0; i--)
            {
                PendingSaddleRevert r = s_pendingReverts[i];
                // 过期清理
                if (now - r.QueuedAtSeconds > 5f)
                {
                    s_pendingReverts.RemoveAt(i);
                    continue;
                }
                // 模板匹配 + 位置匹配
                if (!string.Equals(r.OriginalTemplate, expectedOriginal, StringComparison.Ordinal)) continue;
                if (Vector3.Distance(r.Position, pos) > 2f) continue;
                matched = r;
                s_pendingReverts.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 撤销上鞍：删除 Saddled 实体，重建原马模板实体，恢复繁殖状态。
        /// 按配置决定是否退鞍给玩家(ConsumeSaddleOnBlocked=false 时尝试退鞍)。
        /// </summary>
        static void RevertSaddling(Entity saddledEntity, PendingSaddleRevert revert, BreedingConfig cfg)
        {
            try
            {
                // 1. 删除 Saddled 实体
                s_project.RemoveEntity(saddledEntity, true);

                // 2. 重建原马模板实体
                Entity original = DatabaseManager.CreateEntity(s_project, revert.OriginalTemplate, false);
                if (original == null)
                {
                    Log.Warning($"[Breeding] 撤销上鞍失败：无法重建原模板 {revert.OriginalTemplate}");
                    return;
                }
                ComponentBody origBody = original.FindComponent<ComponentBody>(true);
                origBody.Position = revert.Position;
                origBody.Rotation = revert.Rotation;
                origBody.Velocity = revert.Velocity;
                original.FindComponent<ComponentSpawn>(true).SpawnDuration = 0f;
                s_project.AddEntity(original);

                // 3. 恢复繁殖状态(OnEntityAdd 会先按自然生成初始化，这里覆盖回原状态)
                //    注意：AddEntity 后 OnEntityAdd 会被同步调用并注册新状态，我们要在它之后覆盖
                s_states[original] = revert.State;
                CacheAndApplyBoxSize(original, revert.State, cfg);

                // 4. 退鞍处理(如果配置 ConsumeSaddleOnBlocked=false)
                //    原版 OnUse 在调用我们 hook 前已经 RemoveActiveTool(1) 扣了鞍。
                //    当前 mod API 无 OnUse hook，无法在扣鞍前拦截，也无法精确定位操作玩家。
                //    因此 ConsumeSaddleOnBlocked=false 的实际行为是"鞍已扣 + 上鞍被撤销"，
                //    无法真正退鞍。此处仅日志提示。
                SpeciesConfig species = cfg.GetSpecies(revert.OriginalTemplate);
                bool consume = species?.ConsumeSaddleOnBlocked ?? false;
                if (!consume)
                {
                    Log.Warning("[Breeding] ConsumeSaddleOnBlocked=false：原版已扣鞍，mod API 无 OnUse hook 无法退鞍，上鞍已撤销");
                }

            }
            catch (Exception e)
            {
                Log.Warning($"[Breeding] 撤销上鞍异常: {e.Message}");
            }
        }


        // ==================== 每帧更新(联机版: 由 SubsystemUpdate 驱动 BreedingModLoader.Update) ====================

        /// <summary>
        /// 每帧实体全量同步(联机版无 OnEntityAdd/OnEntityRemove 钩子，由 ModLoader.Tick 调用)。
        /// 对比当前 Project 实体集合与追踪表：新实体注册、已消失实体清理。
        /// </summary>
        public static void SyncEntities(Project project)
        {
            if (!s_initialized || project == null) return;

            // 新增实体
            foreach (Entity entity in project.Entities)
            {
                if (entity != null && !s_states.ContainsKey(entity))
                {
                    OnEntityAdd(entity);
                }
            }

            // 移除已消失实体
            if (s_states.Count > 0)
            {
                List<Entity> toRemove = new();
                foreach (KeyValuePair<Entity, BreedingState> kv in s_states)
                {
                    if (kv.Key == null || !project.Entities.Contains(kv.Key))
                    {
                        toRemove.Add(kv.Key);
                    }
                }
                foreach (Entity entity in toRemove)
                {
                    OnEntityRemove(entity);
                }
            }
        }

        /// <summary>当前游戏时间(现实秒，用于定期存档节流)。</summary>
        public static double GetCurrentGameTime()
        {
            return s_time != null ? s_time.GameTime : 0d;
        }

        /// <summary>每帧更新所有被追踪的生物(联机版替代单机版 OnFactorsUpdate 钩子)。</summary>
        public static void Update(float dt)
        {
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (s_states.Count == 0) return;

            // 快照遍历：UpdateFemale/UpdateMale 内部可能增删 s_states(交配/产仔/移除)
            List<KeyValuePair<Entity, BreedingState>> snapshot = new(s_states);
            foreach (KeyValuePair<Entity, BreedingState> kv in snapshot)
            {
                Entity entity = kv.Key;
                BreedingState state = kv.Value;
                if (entity == null || state == null) continue;

                SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
                if (species == null) continue;

                UpdateEntity(entity, state, species, dt);
            }
        }

        /// <summary>更新单只生物。</summary>
        static void UpdateEntity(Entity entity, BreedingState state, SpeciesConfig species, float dt)
        {
            // 1. 恢复期倒计时(公母共用)
            if (state.WeaknessRemainingSeconds > 0f)
            {
                state.WeaknessRemainingSeconds -= dt;
                if (state.WeaknessRemainingSeconds < 0f)
                {
                    state.WeaknessRemainingSeconds = -1f;
                }
            }

            // 1b. 喂食状态倒计时(条件性繁衍用)
            if (state.FedRemainingSeconds > 0f)
            {
                state.FedRemainingSeconds -= dt;
                if (state.FedRemainingSeconds < 0f)
                {
                    state.FedRemainingSeconds = -1f;
                }
            }

            // 2. 求偶期判定(成年 + 在季节 + 不在恢复期 + 喂食条件满足)
            // 条件性繁衍: RequireFeeding=true 时还要求 IsFed(已喂食状态未过期)
            // 幼崽不求偶，避免幼崽与成年公狼冲突
            Season currentSeason = GetCurrentSeason();
            state.IsInEstrus = state.IsAdult
                && species.ParsedSeasons.Contains(currentSeason)
                && !state.IsWeak
                && (!species.RequireFeeding || state.IsFed);

            // 3. 成长阶段推进
            UpdateGrowth(entity, state, species);

            // 4. 体型随成长度更新(节流，每 60 帧一次)
            UpdateBoxSize(entity, state, species);

            // 5. 仇恨范围修改(联机版无 ComponentFactors，已移除；如需可用
            //    ComponentChaseBehavior 的 m_dayChaseRange/m_nightChaseRange 自行扩展)

            // 6. 性别特定更新
            if (state.Gender == BreedingGender.Female)
            {
                UpdateFemale(entity, state, species, dt);
            }
            else
            {
                UpdateMale(entity, state, species);
            }
        }

        /// <summary>
        /// 当前季节(联机版无 SubsystemSeasons，用游戏天自算伪季节：每 30 天换一季)。
        /// 0~29=春、30~59=夏、60~89=秋、90~119=冬，循环。
        /// 配置中的 BreedingSeasons(Spring/Summer/Autumn/Winter) 按此映射生效。
        /// </summary>
        static Season GetCurrentSeason()
        {
            double day = s_timeOfDay != null ? s_timeOfDay.Day : 0d;
            int seasonIndex = ((int)Math.Floor(day / 30d)) % 4;
            if (seasonIndex < 0) seasonIndex += 4;
            switch (seasonIndex)
            {
                case 0: return Season.Spring;
                case 1: return Season.Summer;
                case 2: return Season.Autumn;
                default: return Season.Winter;
            }
        }

        /// <summary>成长阶段推进。幼崽期到达 CubDurationDays 后进阶成年。</summary>
        static void UpdateGrowth(Entity entity, BreedingState state, SpeciesConfig species)
        {
            if (state.Stage != GrowthStage.Cub) return;

            double currentDay = s_timeOfDay.Day;
            double ageDays = currentDay - state.BirthDay;

            if (ageDays >= species.CubDurationDays)
            {
                state.Stage = GrowthStage.Adult;
                // 进阶成年时立即应用一次成年体型
                ApplyBoxSizeByGrowth(entity, state, species, 1f);
            }
        }

        /// <summary>体型更新节流：每 60 帧根据成长度重新计算 BoxSize + ModelScale。</summary>
        static void UpdateBoxSize(Entity entity, BreedingState state, SpeciesConfig species)
        {
            if (state.Stage != GrowthStage.Cub) return; // 成年在进阶时已应用
            if (s_debugFrameCounter++ % 60 != 0) return;

            double currentDay = s_timeOfDay.Day;
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
            ApplyBoxSizeByGrowth(entity, state, species, progress);
        }

        /// <summary>
        /// 按成长度计算并应用 BoxSize + ModelScale。
        /// scale = lerp(CubBoxScale, 成年scale, progress)
        /// 成年scale：公=AdultMaleBoxScale，母=AdultFemaleBoxScale。
        /// </summary>
        static void ApplyBoxSizeByGrowth(Entity entity, BreedingState state, SpeciesConfig species, float progress)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!state.OriginalBoxSize.HasValue) return;

            float adultScale = state.Gender == BreedingGender.Male ? species.AdultMaleBoxScale : species.AdultFemaleBoxScale;
            float scale = species.CubBoxScale + (adultScale - species.CubBoxScale) * progress;

            // 碰撞盒缩放(联机版 ComponentModel 无 ModelScale，视觉模型缩放已移除)
            Vector3 orig = state.OriginalBoxSize.Value;
            body.BoxSize = new Vector3(orig.X * scale, orig.Y * scale, orig.Z * scale);
        }

        /// <summary>缓存原版 BoxSize 并按当前成长度应用体型。</summary>
        static void CacheAndApplyBoxSize(Entity entity, BreedingState state, BreedingConfig cfg)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!state.OriginalBoxSize.HasValue)
            {
                state.OriginalBoxSize = body.BoxSize;
            }

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            double currentDay = s_timeOfDay.Day;
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
            ApplyBoxSizeByGrowth(entity, state, species, progress);
        }

        // ==================== 母体更新：孕期倒计时 + 相处交配 ====================

        static void UpdateFemale(Entity entity, BreedingState state, SpeciesConfig species, float dt)
        {
            // 1. 孕期倒计时
            if (state.PregnancyRemainingSeconds > 0f)
            {
                state.PregnancyRemainingSeconds -= dt;
                if (state.PregnancyRemainingSeconds <= 0f)
                {
                    state.PregnancyRemainingSeconds = -1f;
                    GiveBirth(entity, state, species);
                    state.PregnancyFatherId = 0;
                    // 分娩后进入虚弱期
                    state.WeaknessRemainingSeconds = species.WeaknessSeconds;
                }
                return; // 怀孕中不交配
            }

            // 2. 不在发情期 → 重置相处计时，跳过交配
            if (!state.IsInEstrus)
            {
                state.MatingProximitySeconds = 0f;
                return;
            }

            // 3. 寻找附近发情成年公体(MateRadius 内)
            Entity mate = FindNearbyEstrusMale(entity, state, species);
            if (mate == null)
            {
                state.MatingProximitySeconds = 0f;
                return;
            }

            // 4. 累加相处计时(受区域密度影响：同繁殖群个体越多，计时累加越慢 → 配对效率越低)
            float densityFactor = GetDensityFactor(entity, species);
            state.MatingProximitySeconds += dt * densityFactor;

            // 5. 相处时间达到阈值 → 交配
            if (state.MatingProximitySeconds >= species.MatingRequiredProximitySeconds)
            {
                state.PregnancyRemainingSeconds = species.GestationSeconds;
                state.PregnancyFatherId = mate.EntityId;
                state.MatingProximitySeconds = 0f;
                // 母狼不进入虚弱期，直接怀孕(怀孕期间不会再次交配)
                // 分娩后才进入虚弱期

                // 只有公狼进入虚弱期(防止一公多母)
                if (s_states.TryGetValue(mate, out BreedingState maleState))
                {
                    maleState.WeaknessRemainingSeconds = species.WeaknessSeconds;
                    maleState.IsInEstrus = false; // 立即更新，防止同帧其他母狼找到他
                    maleState.TargetFemaleId = 0;
                }

                Log.Information($"[Breeding] 配对成功(相处{species.MatingRequiredProximitySeconds}秒): mother={state.TemplateName}#{entity.EntityId}, father#{mate.EntityId}, gestationSec={species.GestationSeconds}, maleWeaknessSec={species.WeaknessSeconds}");

                // 扩展接口事件：通知其他模组(疾病/草药系统等)
                BreedingEvents.RaiseMatingSuccess(entity, mate);
            }
        }

        /// <summary>查找 MateRadius 内的发情成年公体(同物种或别名互通)。额外检查 IsWeak 防止同帧多次交配。</summary>
        static Entity FindNearbyEstrusMale(Entity entity, BreedingState state, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = species.MateRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);
            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Male) continue;
                if (!otherState.IsAdult) continue;
                if (otherState.IsWeak) continue; // 虚弱期公狼不可交配(双重保险)
                if (!otherState.IsInEstrus) continue;
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 别名互通
                Vector3 otherPos = results.Array[i].Position;
                if (Vector3.Distance(pos, otherPos) > radius) continue;
                return other;
            }
            return null;
        }

        /// <summary>
        /// 判断当前物种是否可与 targetTemplateName 交配(同物种或别名互通)。
        /// 例: Cow.MatingSet={Cow,Bull}，Bull.MatingSet={Bull,Cow}，二者有交集即可交配。
        /// </summary>
        static bool IsMatingCompatible(SpeciesConfig species, string targetTemplateName)
        {
            if (species == null || string.IsNullOrEmpty(targetTemplateName)) return false;
            return species.MatingSet.Contains(targetTemplateName);
        }

        // ==================== 公体更新：寻找母狼 + 竞争打斗 ====================

        /// <summary>
        /// 求偶公体逻辑：
        /// 1. 在 SeekRadius 内寻找最近的求偶母狼，记录 TargetFemaleId。
        /// 2. 检查是否有其他公狼也以同一母狼为目标 → 竞争对手。
        /// 3. 有竞争对手 → 通过 ComponentChaseBehavior.Attack 攻击对方(公狼间矛盾)。
        /// 4. 无竞争对手 → 设路径走向母狼。
        ///
        /// 重要：只有进入求偶期(IsInEstrus)的公体才会执行本逻辑——
        /// 需喂食物种必须已喂食(FedRemainingSeconds>0)才会 IsInEstrus=true，
        /// 因此"未喂食的需喂食动物在繁殖季节不会开始求偶竞争"。
        /// </summary>
        static void UpdateMale(Entity entity, BreedingState state, SpeciesConfig species)
        {
            // 不在求偶期(含未喂食的需喂食物种) → 清除目标，不寻路、不竞争
            if (!state.IsInEstrus)
            {
                state.TargetFemaleId = 0;
                return;
            }

            // 求偶中(已喂食或无需喂食) → 寻找最近的求偶母狼
            Entity female = FindNearestEstrusFemale(entity, state, species);
            if (female == null)
            {
                state.TargetFemaleId = 0;
                return;
            }

            state.TargetFemaleId = female.EntityId;

            // 检查是否有竞争对手(其他求偶中的公狼也以同一母狼为目标)
            // FindRival 内部同样只接受 IsInEstrus=true(需喂食的已喂食)的对手
            Entity rival = FindRival(entity, state, female.EntityId, species);
            if (rival != null)
            {
                // 有竞争对手 → 攻击对方
                ComponentCreature rivalCreature = rival.FindComponent<ComponentCreature>();
                ComponentChaseBehavior chaseBehavior = entity.FindComponent<ComponentChaseBehavior>();
                if (rivalCreature != null && chaseBehavior != null)
                {
                    // 攻击竞争对手(范围=SeekRadius，追击时间=RivalChaseTime秒，非持久)
                    chaseBehavior.Attack(rivalCreature, species.SeekRadius, species.RivalChaseTime, false);
                }
                return;
            }

            // 无竞争对手 → 设路径走向母狼
            ComponentBody femaleBody = female.FindComponent<ComponentBody>();
            if (femaleBody == null) return;

            ComponentPathfinding pathfinding = entity.FindComponent<ComponentPathfinding>();
            if (pathfinding == null) return;

            pathfinding.SetDestination(
                femaleBody.Position,
                1f,            // speed
                1f,            // range
                0,             // maxPathfindingPositions
                true,          // useRandomMovements
                false,         // ignoreHeightDifference
                true,          // raycastDestination
                femaleBody     // doNotAvoidBody(不避开母狼)
            );
        }

        /// <summary>
        /// 查找竞争对手：在同一 SeekRadius 内，有其他发情公狼也以 targetFemaleId 为目标。
        /// </summary>
        static Entity FindRival(Entity entity, BreedingState state, int targetFemaleId, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = species.SeekRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);

            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Male) continue;
                if (!otherState.IsAdult) continue;
                if (!otherState.IsInEstrus) continue;
                if (otherState.TargetFemaleId != targetFemaleId) continue; // 同一目标母狼
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 别名互通
                return other; // 找到竞争对手
            }
            return null;
        }

        /// <summary>查找 SeekRadius 内最近的发情成年母狼(同模板，未怀孕)。</summary>
        static Entity FindNearestEstrusFemale(Entity entity, BreedingState state, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = species.SeekRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);

            Entity nearest = null;
            float nearestDist = float.MaxValue;

            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Female) continue;
                if (!otherState.IsAdult) continue;
                if (otherState.IsWeak) continue; // 虚弱期母狼不可交配
                if (!otherState.IsInEstrus) continue;
                if (otherState.PregnancyRemainingSeconds > 0f) continue; // 跳过怀孕母狼
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 别名互通

                Vector3 otherPos = results.Array[i].Position;
                float dist = Vector3.Distance(pos, otherPos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = other;
                }
            }
            return nearest;
        }

        // ==================== 分娩 ====================

        /// <summary>
        /// 分娩：在母体附近生成 1 只幼崽。
        /// 用母体模板生成(保证外观一致)，出生后通过 BoxSize+ModelScale 缩小为幼崽尺寸。
        /// 幼崽性别随机；成长后公狼像公狼(大)，母狼像母狼(小)——由 AdultMaleBoxScale/AdultFemaleBoxScale 决定。
        /// </summary>
        static void GiveBirth(Entity mother, BreedingState motherState, SpeciesConfig species)
        {
            ComponentBody motherBody = mother.FindComponent<ComponentBody>();
            if (motherBody == null) return;

            Vector3 basePos = motherBody.Position;
            float off = species.BirthSpawnOffset;
            Vector3 offset = new(s_random.Float(-off, off), 0f, s_random.Float(-off, off));
            Vector3 spawnPos = basePos + offset;

            // 选择幼崽模板(优先级: CubTemplates权重表 > CubTemplateOverride > 沿用母体)
            // CubTemplates: 按权重随机选，如 Cow 配 {"Cow":1,"Bull":1} → 50%生Cow 50%生Bull
            // CubTemplateOverride: 固定模板，如 Cow 配 "Cow" → 永远生 Cow
            // 默认: 沿用母体模板
            string cubTemplate = ChooseCubTemplate(species, motherState.TemplateName);
            Entity cub = s_creatureSpawn.SpawnCreature(cubTemplate, spawnPos, false);
            if (cub == null)
            {
                Log.Warning("[Breeding] 幼崽生成失败");
                return;
            }

            // 修正幼崽的繁殖状态(OnEntityAdd 已按"自然生成成体"初始化，需覆盖)
            if (s_states.TryGetValue(cub, out BreedingState cubState))
            {
                cubState.Stage = GrowthStage.Cub;
                cubState.BirthDay = s_timeOfDay.Day;
                // 幼崽性别同样用新实体 Id 确定性分配：出生后无论经历多少次 Despawn/重进存档，性别都不变
                cubState.Gender = RollGender(cub, species);
                cubState.PregnancyRemainingSeconds = -1f;
                cubState.PregnancyFatherId = 0;
                cubState.MatingProximitySeconds = 0f;
                cubState.WeaknessRemainingSeconds = -1f;

                // 立即应用幼崽体型(成长度=0 → CubBoxScale)
                ApplyBoxSizeByGrowth(cub, cubState, species, 0f);
            }
            Log.Information($"[Breeding] 产仔成功: mother={motherState.TemplateName}#{mother.EntityId}, cub#{cub.EntityId}, cubTemplate={cubTemplate}, cubGender={(s_states.TryGetValue(cub, out var cs) ? cs.GetGenderDisplayName() : "?")}");

            // 扩展接口事件：通知其他模组(疾病系统可在此时标记新生个体患病)
            BreedingEvents.RaiseBirth(mother, cub);
        }

        /// <summary>
        /// 选择幼崽模板。优先级：CubTemplates权重表 > CubTemplateOverride > 沿用母体。
        /// CubTemplates 按权重随机(如 {"Cow":1,"Bull":1} → 50%/50%)。
        /// </summary>
        static string ChooseCubTemplate(SpeciesConfig species, string motherTemplate)
        {
            // 1. CubTemplates 权重表
            if (species.CubTemplates != null && species.CubTemplates.Count > 0)
            {
                float totalWeight = 0f;
                foreach (var kv in species.CubTemplates) totalWeight += kv.Value;
                if (totalWeight > 0f)
                {
                    float r = s_random.Float(0f, totalWeight);
                    float cum = 0f;
                    foreach (var kv in species.CubTemplates)
                    {
                        cum += kv.Value;
                        if (r <= cum) return kv.Key;
                    }
                    // 浮点精度兜底
                    return species.CubTemplates.Last().Key;
                }
            }
            // 2. CubTemplateOverride 固定模板
            if (!string.IsNullOrEmpty(species.CubTemplateOverride))
            {
                return species.CubTemplateOverride;
            }
            // 3. 沿用母体模板
            return motherTemplate;
        }

        // ==================== 攻击力与 ChaseRange ====================

        /// <summary>
        /// 攻击力修正(乘算)：
        /// · 幼崽 ×CubAttackFactor / 成年 ×AdultAttackFactor
        /// · 公狼额外 ×MaleAttackBonus(母狼为1.0)
        /// </summary>
        public static void OnMinerHit(ComponentMiner miner, ComponentBody target, ref float attackPower)
        {
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (miner?.Entity == null) return;

            Entity attacker = miner.Entity;
            if (!s_states.TryGetValue(attacker, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            float stageFactor = state.Stage == GrowthStage.Cub ? species.CubAttackFactor : species.AdultAttackFactor;
            float genderFactor = state.Gender == BreedingGender.Male ? species.MaleAttackBonus : 1.0f;
            attackPower *= stageFactor * genderFactor;
        }

        // ==================== 骑乘拦截(ScoreMount hook) ====================

        /// <summary>
        /// 骑乘拦截：当玩家试图骑乘处于禁止交互状态(繁殖期/幼崽期)的生物时返回 -1 阻止。
        /// 由 BreedingModLoader.ScoreMount 调用。
        /// </summary>

        // ==================== 喂食发情(OnEatPickable hook) ====================

        /// <summary>
        /// 生物吃掉落物时触发(由 BreedingModLoader.OnEatPickable 调用)。
        /// 此钩子在生物吃完物品(Count 已扣减)后触发，无法阻止吃，但可据此标记"已喂食"。
        /// 逻辑：
        /// 1. 仅处理被繁殖系统追踪 + RequireFeeding=true 的物种。
        /// 2. 若 FeedItem 为空 = 接受任何食物；否则匹配方块索引(+可选数据)。
        /// 3. 匹配成功 → 设 FedRemainingSeconds = FedDurationSeconds，使该个体可发情。
        /// 注: dealed 始终返回 false，不影响其他模组的喂食钩子。
        /// </summary>
        public static void OnEatPickable(ComponentEatPickableBehavior eatPickableBehavior, Pickable eatPickable, out bool dealed)
        {
            dealed = false;
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (eatPickableBehavior?.Entity == null || eatPickable == null) return;

            Entity entity = eatPickableBehavior.Entity;
            if (!s_states.TryGetValue(entity, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null || !species.RequireFeeding) return;

            // 匹配喂食物品
            if (!IsFeedItemMatch(species, eatPickable)) return;

            // 喂食成功：设置已喂食状态
            state.FedRemainingSeconds = species.FedDurationSeconds;

            // 扩展接口事件：通知其他模组(草药系统可在此识别"喂药/喂草"事件)
            BreedingEvents.RaiseFed(entity, species.FedDurationSeconds);
        }

        /// <summary>
        /// 判断被吃掉的物品是否匹配物种配置的 FeedItem。
        /// · ParsedFeedBlockIndex == null → FeedItem 为空，接受任何食物。
        /// · ParsedFeedBlockIndex < 0 → 解析失败，不匹配任何物品。
        /// · ParsedFeedBlockIndex >= 0 → 比较方块索引；若 ParsedFeedBlockData 非 null 还要比较数据。
        /// </summary>
        static bool IsFeedItemMatch(SpeciesConfig species, Pickable eatPickable)
        {
            if (!species.ParsedFeedBlockIndex.HasValue) return true; // FeedItem 为空 = 接受任何食物
            if (species.ParsedFeedBlockIndex.Value < 0) return false; // 解析失败

            int value = eatPickable.Value;
            int blockId = Terrain.ExtractContents(value);
            if (blockId != species.ParsedFeedBlockIndex.Value) return false;

            // 若配置了数据约束，还要匹配数据
            if (species.ParsedFeedBlockData.HasValue)
            {
                int data = Terrain.ExtractData(value);
                if (data != species.ParsedFeedBlockData.Value) return false;
            }
            return true;
        }

        /// <summary>
        /// 应用仇恨范围 factor(每帧重新 Add Factor)。
        /// · 幼崽：ChaseRange=0(不产生仇恨)
        /// · 怀孕母狼：ChaseRange=0(不产生仇恨)
        /// · 发情期(非虚弱)：ChaseRange ×EstrusChaseRangeMultiplier
        /// · 其他：无额外 factor(正常仇恨)
        /// </summary>

        // ==================== 调试/查询 ====================

        public static int TrackedCount => s_states.Count;

        public static bool Initialized => s_initialized && BreedingConfig.Current?.Enabled == true;

        public static double GetCurrentDay()
        {
            return s_timeOfDay != null ? s_timeOfDay.Day : 0.0;
        }

        /// <summary>查询某实体的繁殖状态(渲染钩子 OnModelRendererDrawExtra 用)。无则返回 null。</summary>
        public static BreedingState GetState(Entity entity)
        {
            return entity != null && s_states.TryGetValue(entity, out BreedingState s) ? s : null;
        }

        // ==================== 区域密度因子(繁殖效率限制) ====================

        /// <summary>
        /// 计算区域繁殖密度因子(0~1)：区域内同繁殖群(含别名)成年个体越多，因子越低。
        /// · 1.0 = 密度达标(个体数 ≤ DensityLimit，配对效率 100%)
        /// · 0.x = 拥挤(每超 DensityLimit 一只，效率 -DensityPenaltyStep，最低 0)
        /// 效率作用于母体"相处计时"累加速度，头顶"求偶中(相处N秒)"的 N 增长随之变慢。
        /// 扩展接口：其他模组可调用此方法查询任意个体的密度压力(如疾病系统据此加额外惩罚)。
        /// </summary>
        public static float GetDensityFactor(Entity entity, SpeciesConfig species)
        {
            if (entity == null || species == null || !species.DensityEnabled) return 1f;

            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return 1f;
            Vector3 pos = body.Position;
            float radius = species.DensityRadius;

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);

            int count = 0;
            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == null || other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (!otherState.IsAdult) continue;
                if (!IsMatingCompatible(species, otherState.TemplateName)) continue; // 同繁殖群(含别名)
                Vector3 otherPos = results.Array[i].Position;
                if (Vector3.Distance(pos, otherPos) > radius) continue;
                count++;
            }

            float excess = count - species.DensityLimit;
            if (excess <= 0f) return 1f;
            return Math.Clamp(1f - excess * species.DensityPenaltyStep, 0f, 1f);
        }

        // ==================== 模组扩展接口(供疾病/草药等第三方模组接入) ====================

        // ---- 状态查询(只读) ----
        // 其他模组引用本 DLL 后可直接调用；未引用 DLL 时可用反射调用同名静态方法。

        /// <summary>查询实体繁殖状态；未追踪返回 null。(已公开)</summary>
        // GetState 见上方

        /// <summary>查询实体性别；无状态返回 null。</summary>
        public static BreedingGender? GetGender(Entity entity)
        {
            BreedingState s = GetState(entity);
            return s != null ? s.Gender : (BreedingGender?)null;
        }

        /// <summary>查询实体是否成年(幼崽期 false)。</summary>
        public static bool IsAdult(Entity entity)
        {
            BreedingState s = GetState(entity);
            return s != null && s.IsAdult;
        }

        /// <summary>查询实体是否处于求偶期(需喂食物种含已喂食判定)。</summary>
        public static bool IsInEstrus(Entity entity)
        {
            BreedingState s = GetState(entity);
            return s != null && s.IsInEstrus;
        }

        /// <summary>查询实体是否怀孕(母体孕期倒计时中)。</summary>
        public static bool IsPregnant(Entity entity)
        {
            BreedingState s = GetState(entity);
            return s != null && s.Gender == BreedingGender.Female && s.PregnancyRemainingSeconds > 0f;
        }

        /// <summary>查询实体是否处于恢复期(配对后/产仔后，期间不进入求偶)。</summary>
        public static bool IsWeak(Entity entity)
        {
            BreedingState s = GetState(entity);
            return s != null && s.IsWeak;
        }

        /// <summary>查询实体是否已喂食(条件性繁衍状态)。</summary>
        public static bool IsFed(Entity entity)
        {
            BreedingState s = GetState(entity);
            return s != null && s.IsFed;
        }

        /// <summary>查询实体成长进度(0~1，幼崽期线性增长，成年恒为 1)。</summary>
        public static float GetGrowthProgress(Entity entity)
        {
            BreedingState s = GetState(entity);
            if (s == null) return 1f;
            BreedingConfig cfg = BreedingConfig.Current;
            SpeciesConfig species = cfg?.GetSpecies(s.TemplateName);
            if (species == null) return 1f;
            return s.GetGrowthProgress(s_timeOfDay != null ? s_timeOfDay.Day : 0d, species.CubDurationDays);
        }

        // ---- 状态操作(供第三方模组调用，如疾病/草药系统) ----

        /// <summary>设置母体怀孕(孕期秒数)。成功返回 true。疾病系统可用它模拟"异常孕期"。</summary>
        public static bool SetPregnant(Entity entity, float gestationSeconds)
        {
            BreedingState s = GetState(entity);
            if (s == null || s.Gender != BreedingGender.Female) return false;
            s.PregnancyRemainingSeconds = Math.Max(0f, gestationSeconds);
            s.MatingProximitySeconds = 0f;
            BreedingEvents.RaiseStateChanged(entity, s);
            return true;
        }

        /// <summary>设置恢复期(秒数)。疾病系统可用它让个体暂停求偶。</summary>
        public static bool SetWeak(Entity entity, float seconds)
        {
            BreedingState s = GetState(entity);
            if (s == null) return false;
            s.WeaknessRemainingSeconds = Math.Max(0f, seconds);
            BreedingEvents.RaiseStateChanged(entity, s);
            return true;
        }

        /// <summary>设置已喂食状态(秒数)。草药系统可用它模拟"喂药后发情"。</summary>
        public static bool SetFed(Entity entity, float seconds)
        {
            BreedingState s = GetState(entity);
            if (s == null) return false;
            s.FedRemainingSeconds = Math.Max(0f, seconds);
            BreedingEvents.RaiseStateChanged(entity, s);
            return true;
        }

        /// <summary>治愈/重置繁殖状态：清空孕期、恢复期、相处计时、已喂食。疾病系统"治愈"用。</summary>
        public static bool CureBreedingState(Entity entity)
        {
            BreedingState s = GetState(entity);
            if (s == null) return false;
            s.PregnancyRemainingSeconds = -1f;
            s.PregnancyFatherId = 0;
            s.WeaknessRemainingSeconds = -1f;
            s.MatingProximitySeconds = 0f;
            s.FedRemainingSeconds = -1f;
            BreedingEvents.RaiseStateChanged(entity, s);
            return true;
        }

        /// <summary>
        /// 繁殖系统事件(模组扩展接口)。
        /// 其他模组直接订阅静态事件即可拓展玩法：
        ///   疾病系统：监听 Birth(新生个体标记患病)、MatingSuccess(患病个体抑制繁殖)
        ///   草药系统：监听 Fed(喂食草药的个体获得增益/喂食事件)
        /// 注意：事件仅在游戏运行时触发；订阅者请自行处理线程与生命周期。
        /// 外部模组只应 += / -= 订阅事件，不得直接触发；触发由本模组通过 Raise* 方法完成。
        /// </summary>
        public static class BreedingEvents
        {
            /// <summary>配对成功(motherEntity, fatherEntity)。</summary>
            public static event Action<Entity, Entity> MatingSuccess;

            /// <summary>产仔成功(motherEntity, cubEntity)。</summary>
            public static event Action<Entity, Entity> Birth;

            /// <summary>个体被喂食触发求偶(entity, fedSeconds)。</summary>
            public static event Action<Entity, float> Fed;

            /// <summary>繁殖状态被 API 操作修改(entity, state)。</summary>
            public static event Action<Entity, BreedingState> StateChanged;

            // ---- 触发方法(仅本模组内部调用；外部模组只订阅，不触发) ----

            public static void RaiseMatingSuccess(Entity mother, Entity father) => MatingSuccess?.Invoke(mother, father);

            public static void RaiseBirth(Entity mother, Entity cub) => Birth?.Invoke(mother, cub);

            public static void RaiseFed(Entity entity, float fedSeconds) => Fed?.Invoke(entity, fedSeconds);

            public static void RaiseStateChanged(Entity entity, BreedingState state) => StateChanged?.Invoke(entity, state);
        }
    }

    /// <summary>
    /// 上鞍撤销待恢复项。
    /// 当禁止交互的原马被上鞍(原版 RemoveEntity+AddEntity Saddled)时，
    /// 暂存其状态+位置，等 Saddled 实体 OnEntityAdd 时按位置匹配撤销。
    /// </summary>
    class PendingSaddleRevert
    {
        public string OriginalTemplate;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public BreedingState State;
        public float QueuedAtSeconds;
    }
}
