using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;

namespace Game
{
    /// <summary>单个蛋的运行时状态</summary>
    public class EggInfo
    {
        /// <summary>生物模板名(如 Ostrich/Cassowary)</summary>
        public string Species;
        /// <summary>是否受精(有精蛋可孵化)</summary>
        public bool Fertilized;
        /// <summary>放置时刻(游戏时间)</summary>
        public double PlacedTime;
        /// <summary>孵化进度 0~1</summary>
        public float IncubationProgress;
        /// <summary>是否在孵化中(由孵化子系统设)</summary>
        public bool IsIncubating;
    }

    /// <summary>
    /// 蛋状态管理器 + 孵化子系统。
    /// 追踪已放置蛋的受精状态、物种、孵化进度。每帧推进孵化。
    /// </summary>
    public static class BreedingEggManager
    {
        /// <summary>蛋位置→蛋状态</summary>
        static Dictionary<Point3, EggInfo> s_eggs = new();
        static Project s_project;
        static System.Random s_rng = new();

        /// <summary>初始化(设置 Project 引用)</summary>
        public static void Initialize(Project project)
        {
            s_project = project;
        }

        /// <summary>注册一个蛋(由蛋交互子系统在放置时调用)</summary>
        public static void RegisterEgg(int x, int y, int z, string species, bool fertilized)
        {
            var key = new Point3(x, y, z);
            if (!s_eggs.ContainsKey(key))
            {
                s_eggs[key] = new EggInfo
                {
                    Species = species,
                    Fertilized = fertilized,
                    PlacedTime = Time.FrameStartTime,
                    IncubationProgress = 0f
                };
            }
        }

        /// <summary>移除蛋(破碎/破坏时调用)</summary>
        public static void RemoveEgg(int x, int y, int z)
        {
            s_eggs.Remove(new Point3(x, y, z));
        }

        /// <summary>获取蛋状态</summary>
        public static EggInfo GetEgg(int x, int y, int z)
        {
            s_eggs.TryGetValue(new Point3(x, y, z), out EggInfo info);
            return info;
        }

        /// <summary>所有蛋(用于遍历)</summary>
        public static IEnumerable<KeyValuePair<Point3, EggInfo>> AllEggs => s_eggs;

        /// <summary>世界加载时清空</summary>
        public static void Clear()
        {
            s_eggs.Clear();
            s_project = null;
        }

        /// <summary>每帧推进孵化进度</summary>
        public static void AdvanceIncubation(float dt)
        {
            if (s_project == null) return;
            SubsystemTerrain terrain = s_project.FindSubsystem<SubsystemTerrain>(true);
            if (terrain == null) return;

            List<Point3> toRemove = new();
            List<(Point3 pos, EggInfo info)> toHatch = new();

            foreach (var kv in s_eggs)
            {
                EggInfo egg = kv.Value;
                // 只有有精蛋才能孵化
                if (!egg.Fertilized) continue;

                Point3 pos = kv.Key;
                int blockValue = terrain.Terrain.GetCellValue(pos.X, pos.Y, pos.Z);
                int contents = Terrain.ExtractContents(blockValue);

                // 检查蛋块是否还在
                if (contents != 118)
                {
                    toRemove.Add(pos);
                    continue;
                }

                // 检查孵化条件：落在合适的方块上
                if (!IsValidIncubationBlock(terrain, pos.X, pos.Y, pos.Z, egg))
                {
                    egg.IsIncubating = false;
                    continue;
                }

                egg.IsIncubating = true;
                // 如果物种需要孵化喂食，检查附近是否有同种成年雌体
                bool needsFeeding = GetIncubationNeedsFeeding(egg.Species);
                if (needsFeeding)
                {
                    // 搜索附近 8 格内是否有同种成年雌体
                    if (!HasNearbyAdultFemale(terrain, pos.X, pos.Y, pos.Z, egg.Species))
                    {
                        // 无成体在附近，孵化进度减半(模拟无母体孵蛋)
                        egg.IncubationProgress += dt / GetIncubationDuration(egg) * 0.5f;
                        continue;
                    }
                }
                egg.IncubationProgress += dt / GetIncubationDuration(egg);
                if (egg.IncubationProgress >= 1f)
                {
                    toHatch.Add((pos, egg));
                }
            }

            // 移除已消失的蛋
            foreach (var pos in toRemove) s_eggs.Remove(pos);

            // 执行孵化
            foreach (var (pos, egg) in toHatch)
            {
                HatchEgg(pos, egg);
            }
        }

        /// <summary>检查蛋是否在合适的孵化方块上</summary>
        static bool IsValidIncubationBlock(SubsystemTerrain terrain, int x, int y, int z, EggInfo egg)
        {
            // 蛋在 y 位置，检查下方(y-1)的方块是什么
            int below = terrain.Terrain.GetCellValue(x, y - 1, z);
            int belowContents = Terrain.ExtractContents(below);
            string name = BlocksManager.Blocks[belowContents]?.GetType().Name ?? "";

            // 陆行禽(鸵鸟/食火鸡)：沙子上孵化
            if (egg.Species == "Ostrich" || egg.Species == "Cassowary")
            {
                return name.Contains("Sand") || name.Contains("Sandstone") || name == "GravelBlock";
            }

            // 飞禽/水禽/其他：树叶上孵化（测试时无高度限制，方便观察）
            return name.Contains("Leaves") || name.Contains("Leaf") || name.Contains("LeavesBlock");

            // 水禽(鸭子等)：树叶上孵化，需靠近水域(附近 10 格内有水)
            if (egg.Species.IndexOf("Duck", StringComparison.OrdinalIgnoreCase) >= 0
                || egg.Species.IndexOf("Goose", StringComparison.OrdinalIgnoreCase) >= 0
                || egg.Species.IndexOf("Swan", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!(name.Contains("Leaves") || name.Contains("Leaf"))) return false;
                // 检查附近是否有水(以蛋为中心 10 格半径扫描)
                return IsNearWater(terrain, x, y, z, 10);
            }

            // 飞禽：树叶上孵化，且高度≥10 格
            if (name.Contains("Leaves") || name.Contains("Leaf") || name.Contains("LeavesBlock"))
            {
                return y > 10;
            }

            // 默认：允许在树叶上孵化
            return name.Contains("Leaves") || name.Contains("Leaf");
        }

        /// <summary>获取孵化所需秒数</summary>
        static float GetIncubationDuration(EggInfo egg)
        {
            // 测试用：统一 0.5 游戏天 = 600 秒(10 分钟)，方便观察孵化
            return 0.5f * 1200f;
        }

        /// <summary>检查蛋附近是否有水方块(水禽池塘孵化条件)</summary>
        static bool IsNearWater(SubsystemTerrain terrain, int x, int y, int z, int radius)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    // 检查 y 附近的水面
                    int waterY = terrain.Terrain.GetTopHeight(x + dx, z + dz);
                    if (waterY < 1) continue;
                    int block = terrain.Terrain.GetCellValue(x + dx, waterY, z + dz);
                    int contents = Terrain.ExtractContents(block);
                    string name = BlocksManager.Blocks[contents]?.GetType().Name ?? "";
                    if (name.Contains("Water") || contents == 18 || contents == 19) // WaterBlock or Water
                        return true;
                }
            }
            return false;
        }

        /// <summary>该物种是否需要母体在旁孵化(喂食/守护)</summary>
        static bool GetIncubationNeedsFeeding(string species)
        {
            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg == null) return false;
            SpeciesConfig sp = cfg.GetSpecies(species);
            return sp != null && sp.IncubationNeedsFeeding;
        }

        /// <summary>检查附近是否有同种成年雌体(用于孵化喂食判定)</summary>
        static bool HasNearbyAdultFemale(SubsystemTerrain terrain, int x, int y, int z, string species)
        {
            if (s_project == null) return false;
            SubsystemBodies bodies = s_project.FindSubsystem<SubsystemBodies>(true);
            if (bodies == null) return false;
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float radius = 8f;
            DynamicArray<ComponentBody> results = new();
            bodies.FindBodiesAroundPoint(new Vector2(center.X, center.Z), radius, results);
            for (int i = 0; i < results.Count; i++)
            {
                Entity e = results.Array[i].Entity;
                if (e == null) continue;
                BreedingState s = SubsystemBreeding.GetState(e);
                if (s == null) continue;
                if (s.TemplateName == species && s.Gender == BreedingGender.Female && s.IsAdult)
                {
                    Vector3 otherPos = results.Array[i].Position;
                    if (Vector3.Distance(center, otherPos) <= radius)
                        return true;
                }
            }
            return false;
        }

        /// <summary>孵化：生成幼崽并移除蛋</summary>
        static void HatchEgg(Point3 pos, EggInfo egg)
        {
            try
            {
                if (s_project == null) return;
                SubsystemTerrain terrain = s_project.FindSubsystem<SubsystemTerrain>(true);
                SubsystemAudio audio = s_project.FindSubsystem<SubsystemAudio>(true);

                // 通过模板名生成幼崽
                Entity entity = DatabaseManager.CreateEntity(s_project, egg.Species, false);
                if (entity == null) return;

                ComponentBody body = entity.FindComponent<ComponentBody>();
                if (body != null)
                {
                    body.Position = new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
                    body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, s_rng.Next((int)(Math.PI * 2000)) / 1000f);
                }
                ComponentSpawn spawn = entity.FindComponent<ComponentSpawn>();
                if (spawn != null) spawn.SpawnDuration = 0.25f;

                s_project.AddEntity(entity);

                // 移除蛋块
                terrain?.DestroyCell(0, pos.X, pos.Y, pos.Z, 0, false, false);
                audio?.PlaySound("Audio/EggLaid", 1f, (float)(s_rng.NextDouble() * 0.2 - 0.1), new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f), 2f, true);
                s_eggs.Remove(pos);
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 孵化失败: " + e.Message);
            }
        }
    }
}