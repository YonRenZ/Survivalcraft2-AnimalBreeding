using System;
using System.Collections.Generic;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 蛋状态管理器。追踪已放置蛋的受精状态、物种、孵化进度。
    /// 供原版 ComponentLayEggBehavior 产出后标记，以及孵化子系统使用。
    ///
    /// 蛋数据格式(EggBlock, ID=118)：
    ///   Bit 0: cooked
    ///   Bit 1: IsLaid (受精标记：true=有精蛋，false=无精蛋)
    ///   Bits 4-15: egg type index
    ///   Bit 16: damage
    /// </summary>
    public static class BreedingEggManager
    {
        /// <summary>蛋位置→蛋状态</summary>
        static Dictionary<Point3, EggInfo> s_eggs = new();

        /// <summary>注册一个蛋(由蛋交互子系统或产蛋逻辑调用)</summary>
        public static void RegisterEgg(int x, int y, int z, string species, bool fertilized)
        {
            var key = new Point3(x, y, z);
            s_eggs[key] = new EggInfo
            {
                Species = species,
                Fertilized = fertilized,
                PlacedTime = Time.FrameStartTime,
                IncubationProgress = 0f
            };
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

        /// <summary>设置蛋受精状态</summary>
        public static void SetFertilized(int x, int y, int z, bool fertilized)
        {
            var key = new Point3(x, y, z);
            if (s_eggs.TryGetValue(key, out EggInfo info))
            {
                info.Fertilized = fertilized;
            }
        }

        /// <summary>所有蛋(用于孵化子系统遍历)</summary>
        public static IEnumerable<KeyValuePair<Point3, EggInfo>> AllEggs => s_eggs;

        /// <summary>世界加载时清空(蛋位置不跨世界)</summary>
        public static void Clear()
        {
            s_eggs.Clear();
        }

        /// <summary>
        /// 给原版 ComponentLayEggBehavior 产出的蛋打标：
        /// 根据雌体繁殖状态，把蛋块数据的 IsLaid 位设为有精/无精。
        /// 在蛋被产出的瞬间(OnFactorsUpdate 中检测)调用。
        /// </summary>
        public static int MarkEggData(int eggValue, bool isFertilized)
        {
            int data = Terrain.ExtractData(eggValue);
            data = EggBlock.SetIsLaid(data, isFertilized);
            return Terrain.ReplaceData(eggValue, data);
        }
    }

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
}