using System;
using System.Collections.Generic;
using Engine;
using GameEntitySystem;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统核心管理器。
    /// 通过 ModLoader 钩子接入游戏生命周期，不修改 API 实体模板，所有运行时状态缓存于本类的 Dictionary 中。
    ///
    /// 负责的功能：
    /// 1. 季节开关：每只动物 1~2 个繁殖季节，不在季节内 → 无法触发交配。
    /// 2. 怀孕成功率：三选一失败检测(血量/温湿度/密度)，否则按 DefaultPregnancySuccessRate 判定。
    /// 3. 成长阶段：幼崽期(CubDurationDays 天) → 成年期；幼崽期每天有夭折判定。
    /// 4. 攻击力动态调整：幼崽 ×0.3 / 成年 ×1.0；发情期 ×0.5；残血 ×0.5；分状态直接乘算。
    ///
    /// 简化点(已移除)：
    /// · 近亲检测：已移除(原 IsInbreeding 逻辑)。
    /// · 重复配对检测：已移除(原 RecentMates / IsRecentMate 逻辑)。
    /// </summary>
    public static class SubsystemBreeding
    {
        // ==================== 运行时状态 ====================

        /// <summary>每只动物的繁殖状态。Key 为 Entity 引用(弱引用语义由 OnEntityRemove 清理)。</summary>
        static readonly Dictionary<Entity, BreedingState> s_states = new();

        /// <summary>实体 Id → Entity 映射，用于近亲检测时按父/母 Id 反查实体。</summary>
        static readonly Dictionary<int, Entity> s_idToEntity = new();

        /// <summary>缓存配置中所有物种的父模板名集合(用于按物种识别模板变种，例如 Wolf_Gray / Wolf_Coyote 都属于 Wolf)。</summary>
        static readonly Dictionary<string, string> s_templateToSpecies = new();

        // ==================== 缓存的子系统 ====================

        static Project s_project;
        static SubsystemCreatureSpawn s_creatureSpawn;
        static SubsystemBodies s_bodies;
        static SubsystemSeasons s_seasons;
        static SubsystemTerrain s_terrain;
        static SubsystemTimeOfDay s_timeOfDay;
        static SubsystemGameInfo s_gameInfo;
        static SubsystemTime s_time;
        
        static Random s_random = new();
        static bool s_initialized;

        /// <summary>攻击力修正命中节流计数器(每 200 次命中输出一次)。</summary>
        static long s_debugHitCounter;

        /// <summary>怀孕持续天数(游戏天)。母体交配成功后 GestationDays 天分娩。</summary>
        const float kGestationDays = 1.0f;

        /// <summary>繁殖检测节流间隔(游戏秒)。每只动物每 N 秒最多跑一次完整怀孕检测。</summary>
        const double kBreedingCheckIntervalSeconds = 8.0;

        /// <summary>
        /// 由 HYKJModLoader.OnProjectLoaded 调用，缓存子系统引用并加载配置。
        /// 必须在 OnProjectLoaded 之后调用，否则 Project.FindSubsystem 不可用。
        /// </summary>
        public static void Initialize(Project project)
        {
            Log.Information("[HYKJ.Breeding] Initialize 开始");
            s_project = project;
            s_creatureSpawn = project.FindSubsystem<SubsystemCreatureSpawn>(true);
            s_bodies = project.FindSubsystem<SubsystemBodies>(true);
            s_seasons = project.FindSubsystem<SubsystemSeasons>(true);
            s_terrain = project.FindSubsystem<SubsystemTerrain>(true);
            s_timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            s_gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            s_time = project.FindSubsystem<SubsystemTime>(true);
            Log.Information($"[HYKJ.Breeding] 子系统已缓存: creatureSpawn={s_creatureSpawn!=null}, bodies={s_bodies!=null}, seasons={s_seasons!=null}, terrain={s_terrain!=null}, timeOfDay={s_timeOfDay!=null}, time={s_time!=null}");

            // 加载配置(若已加载则刷新)。同时把 NestBlocks 字符串解析为方块索引。
            BreedingConfig.Load();
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled == true)
            {
                s_templateToSpecies.Clear();
                foreach (KeyValuePair<string, SpeciesConfig> kv in cfg.Species)
                {
                    s_templateToSpecies[kv.Key] = kv.Key;
                    // 解析 NestBlocks → 方块索引
                    kv.Value.NestBlockIndices.Clear();
                    if (kv.Value.NestBlocks != null)
                    {
                        foreach (string blockName in kv.Value.NestBlocks)
                        {
                            int idx = FindBlockIndexByName(blockName);
                            if (idx > 0)
                            {
                                kv.Value.NestBlockIndices.Add(idx);
                                Log.Information($"[HYKJ.Breeding]   物种 {kv.Key} 巢穴方块 {blockName} → 索引 {idx}");
                            }
                            else
                            {
                                Log.Warning($"[HYKJ.Breeding]   物种 {kv.Key} 巢穴方块 {blockName} 未找到");
                            }
                        }
                    }
                }
                Log.Information($"[HYKJ.Breeding] 初始化完成，追踪物种数={cfg.Species.Count}，GestationDays={kGestationDays}，BreedingCheckInterval={kBreedingCheckIntervalSeconds}s");
            }
            else
            {
                Log.Warning("[HYKJ.Breeding] 配置禁用或加载失败，繁殖系统不生效");
            }
            s_initialized = true;
            Log.Information("[HYKJ.Breeding] Initialize 完成");
        }

        /// <summary>按方块显示名查找方块索引。用于 NestBlocks 配置项。找不到返回 -1。</summary>
        static int FindBlockIndexByName(string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return -1;
            try
            {
                // BlocksManager.Blocks 是按索引的数组，需要 O(N) 查找名字。N 最多约 400，可接受。
                Block[] blocks = BlocksManager.Blocks;
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (string.Equals(blocks[i].GetType().Name, blockName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[HYKJ.Breeding] 查找方块 {blockName} 失败: {e.Message}");
            }
            return -1;
        }

        // ==================== 实体生命周期钩子 ====================

        /// <summary>由 HYKJModLoader.OnEntityAdd 调用。如果是配置中有的物种，初始化繁殖状态。</summary>
        public static void OnEntityAdd(Entity entity)
        {
            if (!s_initialized || entity == null) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return; // 非生物(玩家/船/方块实体等)不处理

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            if (string.IsNullOrEmpty(templateName)) return;

            // 配置中若没该物种，则该实体不参与繁殖(但仍可能作为攻击力调整目标？不，只对配置内的物种生效)
            SpeciesConfig species = cfg.GetSpecies(templateName);
            if (species == null) return;

            // 已存在状态(从存档恢复时 OnReadSpawnData 会先于 OnEntityAdd 吗？保险起见先查)

            if (s_states.ContainsKey(entity))
            {
                Log.Information($"[HYKJ.Breeding] OnEntityAdd 已存在状态: id={entity.Id}, template={templateName}");
                return;
            }

            // 自然生成的成体：默认成年；性别随机；父/母 Id = 0
            BreedingState state = new()
            {
                TemplateName = templateName,
                Gender = s_random.Bool(0.5f) ? BreedingGender.Male : BreedingGender.Female,
                Stage = GrowthStage.Adult,
                BirthDay = s_timeOfDay.Day,
                AdultDay = s_timeOfDay.Day,
                FatherId = 0,
                MotherId = 0
            };
            s_states[entity] = state;
            s_idToEntity[entity.Id] = entity;

            Log.Information($"[HYKJ.Breeding] OnEntityAdd 注册新个体: id={entity.Id}, template={templateName}, gender={state.GetGenderDisplayName()}, stage={state.GetStageDisplayName()}, day={s_timeOfDay.Day}, totalTracked={s_states.Count}");
        }

        /// <summary>由 HYKJModLoader.OnEntityRemove 调用。清理状态。</summary>
        public static void OnEntityRemove(Entity entity)
        {
            if (entity == null) return;

            bool removed = s_states.Remove(entity);
            // idToEntity 不主动清理(键值对少量，且若实体被回收后 Id 可能被复用)；
            // 但下次 OnEntityAdd 同 Id 时会覆盖。这里也清一下避免脏数据。
            s_idToEntity.Remove(entity.Id);
            if (removed)
            {
                Log.Information($"[HYKJ.Breeding] OnEntityRemove 清理: id={entity.Id}, totalTracked={s_states.Count}");
            }
        }

        /// <summary>由 HYKJModLoader.OnReadSpawnData 调用。从 SpawnEntityData.Data 反序列化繁殖状态。</summary>
        public static void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            if (!s_initialized || entity == null || spawnEntityData == null) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;

            string templateName = entity.ValuesDictionary.DatabaseObject?.Name;
            if (string.IsNullOrEmpty(templateName) || cfg.GetSpecies(templateName) == null) return;

            BreedingState state = BreedingState.Deserialize(spawnEntityData.Data);
            if (state == null)
            {
                // 没有保存的状态(老存档/未保存过)，让 OnEntityAdd 走默认初始化
                Log.Information($"[HYKJ.Breeding] OnReadSpawnData 无保存状态(走默认初始化): id={entity.Id}, template={templateName}");

                return;
            }
            // 校验：模板名要一致(防止配置变更)
            if (!string.Equals(state.TemplateName, templateName, StringComparison.Ordinal))
            {

                Log.Warning($"[HYKJ.Breeding] 状态模板名不匹配: state={state.TemplateName}, entity={templateName}，丢弃旧状态");
                return;
            }
            // 已经在 s_states 中的话(OnEntityAdd 先跑了)，覆盖
            s_states[entity] = state;
            s_idToEntity[entity.Id] = entity;

            // 若是幼崽状态，恢复碰撞盒
            if (state.Stage == GrowthStage.Cub)
            {
                ApplyCubBoxSize(entity, cfg.GetSpecies(templateName));
            }

            Log.Information($"[HYKJ.Breeding] OnReadSpawnData 恢复状态: id={entity.Id}, template={templateName}, gender={state.GetGenderDisplayName()}, stage={state.GetStageDisplayName()}, birthDay={state.BirthDay}, dueDay={state.PregnancyDueDay}");
        }

        /// <summary>由 HYKJModLoader.OnSaveSpawnData 调用。把繁殖状态序列化进 SpawnEntityData.Data。</summary>
        public static void OnSaveSpawnData(ComponentSpawn spawn, SpawnEntityData spawnEntityData)
        {
            if (!s_initialized || spawn?.Entity == null || spawnEntityData == null) return;
            if (!s_states.TryGetValue(spawn.Entity, out BreedingState state)) return;
            spawnEntityData.Data = state.Serialize();
        }

        // ==================== 每帧更新(由 OnFactorsUpdate 驱动) ====================

        /// <summary>
        /// 由 HYKJModLoader.OnFactorsUpdate 调用。每帧每只生物(含玩家)触发一次。
        /// 用此钩子驱动：1) 该生物的繁殖状态更新(成长、怀孕、交配尝试)；2) 发情期 ChaseRange factor。
        /// </summary>
        public static void OnFactorsUpdate(ComponentFactors factors, float dt)
        {
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (factors?.Entity == null) return;

            Entity entity = factors.Entity;
            if (!s_states.TryGetValue(entity, out BreedingState state)) return;

            SpeciesConfig species = cfg.GetSpecies(state.TemplateName);
            if (species == null) return;

            // 1. 成长阶段推进
            UpdateGrowth(entity, state, species, cfg);

            // 2. 发情期判定(并应用 ChaseRange factor)
            Season currentSeason = s_seasons.Season;
            state.IsInEstrus = species.ParsedSeasons.Contains(currentSeason);
            ApplyChaseRangeFactor(factors, state, cfg);

            // 3. 怀孕/分娩/交配(仅母体)
            if (state.Gender == BreedingGender.Female)
            {
                UpdateFemale(entity, state, species, cfg);
            }
        }

        /// <summary>成长阶段推进。幼崽期到达 CubDurationDays 后自动晋级成年。</summary>
        static void UpdateGrowth(Entity entity, BreedingState state, SpeciesConfig species, BreedingConfig cfg)
        {
            if (state.Stage != GrowthStage.Cub) return;

            double currentDay = s_timeOfDay.Day;
            double ageDays = currentDay - state.BirthDay;

            // 每日一次：幼崽存活判定(无窝/食物源 或 温湿度不适 → 30% 概率夭折)
            long today = (long)Math.Floor(currentDay);
            if (state.LastCubSurvivalDay != today)
            {
                state.LastCubSurvivalDay = today;
                if (!CheckCubSurvival(entity, species, cfg))
                {
                    // 夭折
                    ComponentHealth health = entity.FindComponent<ComponentHealth>();
                    if (health != null)
                    {
                        health.Injure(2.0f, null, true, "Breeding.CubPerished");
                        Log.Information($"[Breeding] 幼崽夭折: template={state.TemplateName}, age={ageDays}天");
                    }
                    return;
                }
            }

            // 到期 → 进阶成年
            if (ageDays >= species.CubDurationDays)
            {
                state.Stage = GrowthStage.Adult;
                state.AdultDay = currentDay;
                ApplyAdultBoxSize(entity, species);
                Log.Information($"[Breeding] 幼崽进阶成年: id={entity.Id}, template={state.TemplateName}, age={ageDays:F2}天, cubDuration={species.CubDurationDays}天");
            }
        }

        /// <summary>
        /// 幼崽每日存活判定：
        /// 周围 CubSurvivalCheckRadius×CubSurvivalCheckRadius 内无窝/食物源 或 温湿度不适 → 返回 false(触发夭折概率)。
        /// 否则返回 true(安全长大)。
        /// </summary>
        static bool CheckCubSurvival(Entity entity, SpeciesConfig species, BreedingConfig cfg)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return true; // 没身体就不判了
            Vector3 pos = body.Position;
            int cx = (int)Math.Floor(pos.X);
            int cy = (int)Math.Floor(pos.Y);
            int cz = (int)Math.Floor(pos.Z);
            int r = cfg.CubSurvivalCheckRadius;

            // 1. 检查周围是否有窝/食物源方块
            bool hasNest = false;
            if (species.NestBlockIndices.Count == 0)
            {
                // 物种没配 NestBlocks → 默认有食物源(不强制要求)
                hasNest = true;
            }
            else
            {
                for (int dx = -r; dx <= r && !hasNest; dx++)
                {
                    for (int dz = -r; dz <= r && !hasNest; dz++)
                    {
                        for (int dy = -2; dy <= 2 && !hasNest; dy++)
                        {
                            int x = cx + dx, y = cy + dy, z = cz + dz;
                            if (y < 0 || y >= 256) continue;
                            int cellValue = s_terrain.Terrain.GetCellValueFast(x, y, z);
                            int contents = Terrain.ExtractContents(cellValue);
                            if (species.NestBlockIndices.Contains(contents))
                            {
                                hasNest = true;
                            }
                        }
                    }
                }
            }
            if (!hasNest)
            {
                // 30% 概率夭折(由调用方再做随机判定，这里返回 false 即可)
                return s_random.Float(0f, 1f) > cfg.CubDailyDeathProbability;
            }

            // 2. 检查温湿度是否严重不适
            float tempDev, humidDev;
            GetEnvironmentDeviation(cx, cy, cz, species, out tempDev, out humidDev);
            if (tempDev > cfg.TemperatureDeviationThreshold || humidDev > cfg.HumidityDeviationThreshold)
            {
                return s_random.Float(0f, 1f) > cfg.CubDailyDeathProbability;
            }

            return true; // 安全
        }

        /// <summary>母体每帧更新：处理怀孕到期分娩 + 节流后尝试交配。</summary>
        static void UpdateFemale(Entity entity, BreedingState state, SpeciesConfig species, BreedingConfig cfg)
        {
            double currentDay = s_timeOfDay.Day;
            double gameTime = s_time.GameTime;

            // 1. 怀孕到期 → 分娩
            if (state.PregnancyDueDay > 0.0 && currentDay >= state.PregnancyDueDay)
            {
                GiveBirth(entity, state, species);
                state.PregnancyDueDay = -1.0;
                state.PregnancyFatherId = 0;
                state.PregnancyFatherTemplate = null;
                state.LastBirthDay = currentDay;
            }

            // 2. 节流：每 kBreedingCheckIntervalSeconds 秒最多跑一次完整交配尝试
            // (LastBreedingCheckDay 字段实际存的是 GameTime 秒数，沿用字段名以保持状态序列化兼容)
            if (gameTime - state.LastBreedingCheckDay < kBreedingCheckIntervalSeconds)
            {
                return;
            }
            state.LastBreedingCheckDay = gameTime;

            // 3. 不在繁殖季 → 跳过
            if (!state.IsInEstrus) return;

            // 4. 已经怀孕 或 在冷却期内 → 跳过
            if (state.PregnancyDueDay > 0.0) return;
            if (state.LastBirthDay > 0.0 && currentDay - state.LastBirthDay < cfg.PregnancyCooldownDays) return;

            // 5. 寻找附近同物种成年公体
            Entity mate = FindMate(entity, state);
            if (mate == null) return;

            // 6. 三选一失败检测 + 概率判定
            if (!CheckPregnancySuccess(entity, state, species, cfg, mate)) return;

            // 7. 交配成功：开始怀孕(已移除近亲检测与重复配对检测)
            state.PregnancyDueDay = currentDay + kGestationDays;
            state.PregnancyFatherId = mate.Id;
            state.PregnancyFatherTemplate = mate.ValuesDictionary.DatabaseObject?.Name;
            Log.Information($"[Breeding] 交配成功: mother={state.TemplateName}#{entity.Id}, father#{mate.Id}, dueDay={state.PregnancyDueDay}");
        }

        /// <summary>查找附近同物种成年公体。半径用 DensityRadius。</summary>
        static Entity FindMate(Entity entity, BreedingState state)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return null;
            Vector3 pos = body.Position;
            float radius = Math.Max(8f, BreedingConfig.Current.DensityRadius);

            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(pos.X, pos.Z), radius, results);
            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == entity) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (otherState.Gender != BreedingGender.Male) continue;
                if (!otherState.IsAdult) continue;
                if (!string.Equals(otherState.TemplateName, state.TemplateName, StringComparison.Ordinal)) continue;
                // 距离判定(3D)
                Vector3 otherPos = results.Array[i].Position;
                if (Vector3.Distance(pos, otherPos) > radius) continue;
                return other;
            }
            return null;
        }

        /// <summary>
        /// 怀孕成功率三选一失败检测：
        /// · 血量太低：母体生命值 &lt; LowHealthThreshold → 失败
        /// · 温湿度不适：当前温/湿度与最适值差值 &gt; 阈值 → 失败
        /// · 密度过高：以该动物为中心 DensityRadius×DensityRadius 范围内同类成年数量 &gt; DensityMaxAdults → 失败
        /// 通过后按 DefaultPregnancySuccessRate 判定。
        /// </summary>
        static bool CheckPregnancySuccess(Entity entity, BreedingState state, SpeciesConfig species, BreedingConfig cfg, Entity mate)
        {
            // 1. 血量
            ComponentHealth health = entity.FindComponent<ComponentHealth>();
            if (health != null && health.Health < cfg.LowHealthThreshold)
            {
                return false;
            }

            // 2. 温湿度
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body != null)
            {
                int cx = (int)Math.Floor(body.Position.X);
                int cy = (int)Math.Floor(body.Position.Y);
                int cz = (int)Math.Floor(body.Position.Z);
                float tempDev, humidDev;
                GetEnvironmentDeviation(cx, cy, cz, species, out tempDev, out humidDev);
                if (tempDev > cfg.TemperatureDeviationThreshold || humidDev > cfg.HumidityDeviationThreshold)
                {
                    return false;
                }
            }

            // 3. 密度(同类成年个体数)
            if (body != null)
            {
                int count = CountSameSpeciesAdults(body.Position, state.TemplateName, entity);
                if (count > cfg.DensityMaxAdults)
                {
                    return false;
                }
            }

            // 4. 默认成功率
            return s_random.Float(0f, 1f) < cfg.DefaultPregnancySuccessRate;
        }

        /// <summary>统计 center 周围 DensityRadius×DensityRadius 范围内同类成年个体数(不含自己)。</summary>
        static int CountSameSpeciesAdults(Vector3 center, string templateName, Entity exclude)
        {
            float radius = BreedingConfig.Current.DensityRadius;
            DynamicArray<ComponentBody> results = new();
            s_bodies.FindBodiesAroundPoint(new Vector2(center.X, center.Z), radius, results);
            int count = 0;
            for (int i = 0; i < results.Count; i++)
            {
                Entity other = results.Array[i].Entity;
                if (other == exclude) continue;
                if (!s_states.TryGetValue(other, out BreedingState otherState)) continue;
                if (!otherState.IsAdult) continue;
                if (!string.Equals(otherState.TemplateName, templateName, StringComparison.Ordinal)) continue;
                Vector3 otherPos = results.Array[i].Position;
                if (Math.Abs(otherPos.X - center.X) > radius) continue;
                if (Math.Abs(otherPos.Z - center.Z) > radius) continue;
                count++;
            }
            return count;
        }

        // ==================== 分娩 ====================

        /// <summary>
        /// 分娩：在母体附近生成 1 只幼崽。模板优先用 SpeciesConfig.CubTemplate，若 DB 中不存在则用母体模板(仅缩小碰撞盒)。
        /// </summary>
        static void GiveBirth(Entity mother, BreedingState motherState, SpeciesConfig species)
        {
            ComponentBody motherBody = mother.FindComponent<ComponentBody>();
            if (motherBody == null) return;

            // 选择幼崽模板
            string cubTemplate = species.CubTemplate;
            if (!string.IsNullOrEmpty(cubTemplate) && !TemplateExists(cubTemplate))
            {
                Log.Warning($"[Breeding] 幼崽模板 {cubTemplate} 不存在，降级使用母体模板 {motherState.TemplateName}");
                cubTemplate = motherState.TemplateName;
            }
            if (string.IsNullOrEmpty(cubTemplate)) cubTemplate = motherState.TemplateName;

            // 在母体附近找一个出生点
            Vector3 basePos = motherBody.Position;
            Vector3 offset = new(s_random.Float(-1.5f, 1.5f), 0f, s_random.Float(-1.5f, 1.5f));
            Vector3 spawnPos = basePos + offset;

            // 调用原版 SubsystemCreatureSpawn.SpawnCreature 生成实体
            Entity cub = s_creatureSpawn.SpawnCreature(cubTemplate, spawnPos, false);
            if (cub == null)
            {
                Log.Warning("[Breeding] 幼崽生成失败");
                return;
            }

            // 修正幼崽的繁殖状态(OnEntityAdd 已经按"自然生成成体"初始化了，需要覆盖)
            if (s_states.TryGetValue(cub, out BreedingState cubState))
            {
                cubState.Stage = GrowthStage.Cub;
                cubState.BirthDay = s_timeOfDay.Day;
                cubState.AdultDay = -1.0;
                cubState.FatherId = motherState.PregnancyFatherId;
                cubState.MotherId = mother.Id;
                cubState.Gender = s_random.Bool(0.5f) ? BreedingGender.Male : BreedingGender.Female;
                cubState.LastBirthDay = -1.0;
                cubState.PregnancyDueDay = -1.0;
                cubState.LastCubSurvivalDay = (long)Math.Floor(s_timeOfDay.Day);

                // 应用幼崽碰撞盒
                ApplyCubBoxSize(cub, species);
            }
            Log.Information($"[Breeding] 分娩成功: mother={motherState.TemplateName}#{mother.Id}, cub={cubTemplate}#{cub.Id}");
        }

        /// <summary>检查某模板名是否在数据库中存在。</summary>
        static bool TemplateExists(string templateName)
        {
            try
            {
                return DatabaseManager.FindEntityValuesDictionary(templateName, false) != null;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 攻击力与 ChaseRange ====================

        /// <summary>
        /// 由 HYKJModLoader.OnMinerHit 调用。修改攻击力(乘算)：
        /// · 幼崽：× CubAttackFactor(默认 0.3)
        /// · 成年：× AdultAttackFactor(默认 1.0)
        /// · 发情期：× EstrusAttackFactor(默认 0.5)
        /// · 残血(&lt; LowHealthAttackThreshold)：× LowHealthAttackFactor(默认 0.5)
        /// 系数直接相乘，不叠加公式。
        /// </summary>
        public static void OnMinerHit(ComponentMiner miner, ComponentBody target, ref float attackPower)
        {
            if (!s_initialized) return;
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg?.Enabled != true) return;
            if (miner?.Entity == null) return;

            Entity attacker = miner.Entity;
            if (!s_states.TryGetValue(attacker, out BreedingState state)) return;

            // 1. 成长阶段系数
            float stageFactor = state.Stage == GrowthStage.Cub ? cfg.CubAttackFactor : cfg.AdultAttackFactor;

            // 2. 发情期系数
            float estrusFactor = state.IsInEstrus ? cfg.EstrusAttackFactor : 1.0f;

            // 3. 残血系数
            ComponentHealth health = attacker.FindComponent<ComponentHealth>();
            float lowHealthFactor = 1.0f;
            if (health != null && health.Health < cfg.LowHealthAttackThreshold)
            {
                lowHealthFactor = cfg.LowHealthAttackFactor;
            }

            attackPower *= stageFactor * estrusFactor * lowHealthFactor;

            // 节流日志：每 200 次命中输出一次攻击力修正详情
            if (s_debugHitCounter++ % 200 == 0)
            {
                Log.Information($"[HYKJ.Breeding] OnMinerHit 攻击力修正: id={attacker.Id}, template={state.TemplateName}, stage={state.GetStageDisplayName()}, estrus={state.IsInEstrus}, factor=stage×{stageFactor}*estrus×{estrusFactor}*lowHp×{lowHealthFactor}={stageFactor * estrusFactor * lowHealthFactor}");
            }
        }

        /// <summary>
        /// 应用发情期仇恨范围倍率。
        /// 通过给 ComponentFactors.OtherFactors["ChaseRange"] 添加一个临时 Factor 实现。
        /// ComponentChaseBehavior 每帧通过 GetOtherFactorResult("ChaseRange") 读取，自动生效。
        /// </summary>
        static void ApplyChaseRangeFactor(ComponentFactors factors, BreedingState state, BreedingConfig cfg)
        {
            try
            {
                // ComponentFactors.Update 每帧会先 CalculateOtherFactorsResult(用上一帧的 Factor 列表)，
                // 然后 GenerateOtherFactors() 把列表清空，再让各模组通过 OnFactorsUpdate 重新加。
                // 因此这里每帧重新 Add 是正确的。
                if (!factors.OtherFactors.TryGetValue("ChaseRange", out List<ComponentLevel.Factor> list))
                {
                    list = new List<ComponentLevel.Factor>();
                    factors.OtherFactors["ChaseRange"] = list;
                }
                if (state.IsInEstrus)
                {
                    list.Add(new ComponentLevel.Factor
                    {
                        Name = "HYKJ.Breeding.Estrus",
                        Value = cfg.EstrusChaseRangeMultiplier,
                        FactorAdditionType = FactorAdditionType.Multiply,
                        Description = "发情期仇恨范围 ×2"
                    });
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] ApplyChaseRangeFactor 失败: " + e.Message);
            }
        }

        // ==================== 碰撞盒 ====================

        /// <summary>把实体 BoxSize 设为幼崽尺寸(并保存原值便于恢复)。</summary>
        static void ApplyCubBoxSize(Entity entity, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (species.CubBoxSize == null || species.CubBoxSize.Count < 3) return;
            if (!s_states.TryGetValue(entity, out BreedingState state)) return;

            // 仅在第一次应用时保存原值
            if (!state.BoxSizeApplied)
            {
                state.SavedCubBoxSize = body.BoxSize;
                state.BoxSizeApplied = true;
            }
            body.BoxSize = new Vector3(species.CubBoxSize[0], species.CubBoxSize[1], species.CubBoxSize[2]);
        }

        /// <summary>把实体 BoxSize 恢复为成年尺寸(优先用配置的 AdultBoxSize，否则用保存的原值)。</summary>
        static void ApplyAdultBoxSize(Entity entity, SpeciesConfig species)
        {
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (!s_states.TryGetValue(entity, out BreedingState state)) return;

            if (species.AdultBoxSize != null && species.AdultBoxSize.Count >= 3)
            {
                body.BoxSize = new Vector3(species.AdultBoxSize[0], species.AdultBoxSize[1], species.AdultBoxSize[2]);
            }
            else if (state.SavedCubBoxSize.HasValue)
            {
                body.BoxSize = state.SavedCubBoxSize.Value;
            }
            // 否则不动(保持模板默认)
        }

        // ==================== 环境查询 ====================

        /// <summary>
        /// 取指定位置温/湿度的归一化偏差(0~1)。偏差 = |当前值 - 最适值|。
        /// 当前值用 Terrain.GetSeasonalTemperature/GetSeasonalHumidity 归一化到 0~1(除以 15)。
        /// </summary>
        static void GetEnvironmentDeviation(int x, int y, int z, SpeciesConfig species,
                                            out float tempDev, out float humidDev)
        {
            try
            {
                int rawTemp = s_terrain.Terrain.GetSeasonalTemperature(x, z)
                              + SubsystemWeather.GetTemperatureAdjustmentAtHeight(y);
                int rawHumid = s_terrain.Terrain.GetSeasonalHumidity(x, z);

                // 归一化到 0~1(原值范围大致 0~15)
                float normTemp = MathUtils.Saturate(rawTemp / 15f);
                float normHumid = MathUtils.Saturate(rawHumid / 15f);

                tempDev = Math.Abs(normTemp - species.OptimalTemperature);
                humidDev = Math.Abs(normHumid - species.OptimalHumidity);
            }
            catch
            {
                tempDev = 0f;
                humidDev = 0f;
            }
        }

        // ==================== 调试/查询 ====================

        /// <summary>当前追踪的动物数量(调试用)。</summary>
        public static int TrackedCount => s_states.Count;

        /// <summary>繁殖系统是否已初始化并启用(渲染器在 Draw 入口检查)。</summary>
        public static bool Initialized => s_initialized && BreedingConfig.Current?.Enabled == true;

        /// <summary>当前游戏天(SubsystemTimeOfDay.Day)。渲染器计算成长进度时使用。</summary>
        public static double GetCurrentDay()
        {
            return s_timeOfDay != null ? s_timeOfDay.Day : 0.0;
        }

        /// <summary>查询某实体的繁殖状态(渲染钩子 OnModelRendererDrawExtra 用)。无则返回 null。</summary>
        public static BreedingState GetState(Entity entity)
        {
            return entity != null && s_states.TryGetValue(entity, out BreedingState s) ? s : null;
        }
    }
}
