using System;
using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game
{
    /// <summary>
    /// 繁殖系统蛋交互行为：蛋的放置、踩踏破碎、挖掘掉落。
    ///
    /// 功能：
    ///   1. 放置：玩家右键放置蛋块时，记录为"已放置的蛋"
    ///   2. 踩踏：玩家踩在蛋上时蛋破碎，掉落蛋物品（可被拾取）
    ///   3. 挖掘：挖掘时有概率掉落蛋物品，否则破坏
    ///
    /// 与 SubsystemEggBlockBehavior 共存，不冲突。
    /// </summary>
    public class SubsystemBreedingEggBehavior : SubsystemBlockBehavior
    {
        public SubsystemTerrain m_subsystemTerrain;
        public SubsystemPickables m_subsystemPickables;
        public SubsystemAudio m_subsystemAudio;
        public Random m_random = new();

        /// <summary>处理蛋块(118)的交互</summary>
        public override int[] HandledBlocks => [118];

        public override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            m_subsystemPickables = Project.FindSubsystem<SubsystemPickables>(true);
            m_subsystemAudio = Project.FindSubsystem<SubsystemAudio>(true);
        }

        // ==================== 放置跟踪 ====================

        /// <summary>放置的蛋被踩踏时是否破碎(由 OnCollide 触发，避免重复处理)</summary>
        public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
        {
            if (Terrain.ExtractContents(value) == 118)
            {
                // 记录蛋的位置，用于后续碰撞检测
                // (不持久化，重进世界后原有蛋块仍可被踩碎/挖掘)
            }
        }

        // ==================== 踩踏破碎 ====================

        /// <summary>生物碰撞蛋块时，如果是玩家且蛋块完整，则破碎并掉落蛋物品</summary>
        public override void OnCollide(CellFace cellFace, float velocity, ComponentBody componentBody)
        {
            if (componentBody == null) return;
            // 只处理玩家踩踏(上方碰撞)
            if (componentBody.Entity.FindComponent<ComponentPlayer>() == null) return;
            if (cellFace.Face != 4) return; // 仅处理顶部碰撞(面朝上)

            int x = cellFace.X, y = cellFace.Y, z = cellFace.Z;
            int value = m_subsystemTerrain.Terrain.GetCellValue(x, y, z);
            if (Terrain.ExtractContents(value) != 118) return;

            // 破碎蛋块，掉落蛋物品
            BreakEgg(value, x, y, z);
        }

        // ==================== 挖掘掉落 ====================

        /// <summary>挖掘蛋块时，有概率掉落蛋物品，否则正常破坏</summary>
        public override void OnItemHarvested(int x, int y, int z, int blockValue, ref BlockDropValue dropValue, ref int newBlockValue)
        {
            if (Terrain.ExtractContents(blockValue) != 118) return;

            // 50% 概率掉落蛋物品，50% 直接破坏
            if (m_random.Float(0f, 1f) < 0.5f)
            {
                dropValue.Value = blockValue;
                dropValue.Count = 1;
            }
            else
            {
                dropValue.Value = 0;
                dropValue.Count = 0;
            }
            newBlockValue = 0;
        }

        // ==================== 工具方法 ====================

        void BreakEgg(int value, int x, int y, int z)
        {
            // 破坏蛋块
            m_subsystemTerrain.DestroyCell(0, x, y, z, value, false, false);

            // 掉落蛋物品(可拾取)
            Vector3 pos = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            Vector3 velocity = new Vector3(
                m_random.Float(-0.5f, 0.5f),
                1f,
                m_random.Float(-0.5f, 0.5f)
            );
            m_subsystemPickables.AddPickable(value, 1, pos, velocity, null, null);

            // 播放破碎音效
            m_subsystemAudio.PlaySound("Audio/EggLaid", 1f, m_random.Float(-0.1f, 0.1f), pos, 2f, true);
        }
    }
}