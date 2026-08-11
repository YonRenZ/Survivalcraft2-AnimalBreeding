using System;
using Engine;
using Engine.Graphics;
using Engine.Media;
using GameEntitySystem;
using Game;
using HYKJ.Breeding;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统独立模组的加载入口。
    /// 仅注册繁殖系统所需的钩子，不依赖荒野科技主模组的任何功能。
    /// 所有逻辑委托给 SubsystemBreeding 静态类。
    ///
    /// 浮动文字渲染：通过 OnModelRendererDrawExtra 钩子(原版画玩家名称的方式)实现。
    /// SubsystemModelsRenderer.Draw 在画完每只生物模型后会回调本钩子，
    /// 我们用 modelsRenderer.PrimitivesRenderer.FontBatch(...).QueueText(...) 入队文字(layer 1)，
    /// 由 SubsystemModelsRenderer 在 DrawOrder=201 时统一 Flush，不需要自己 Flush。
    /// </summary>
    public class BreedingModLoader : ModLoader
    {
        public override void __ModInitialize()
        {
            // 动物繁殖系统相关钩子：实体生命周期、存档读写、每帧更新、攻击力修正、模型绘制扩展
            ModsManager.RegisterHook("OnProjectLoaded", this);
            ModsManager.RegisterHook("OnEntityAdd", this);
            ModsManager.RegisterHook("OnEntityRemove", this);
            ModsManager.RegisterHook("OnReadSpawnData", this);
            ModsManager.RegisterHook("OnSaveSpawnData", this);
            ModsManager.RegisterHook("OnFactorsUpdate", this);
            ModsManager.RegisterHook("OnMinerHit", this);
            ModsManager.RegisterHook("OnModelRendererDrawExtra", this);

            Log.Information("[BreedingMod] 动物繁殖系统模组初始化(含 OnModelRendererDrawExtra 渲染钩子)");
        }

        /// <summary>当 Project 加载完成时执行。繁殖系统在此缓存子系统引用 + 加载配置。</summary>
        public override void OnProjectLoaded(Project project)
        {
            SubsystemBreeding.Initialize(project);
        }

        // ==================== 实体生命周期 ====================

        public override void OnEntityAdd(Entity entity)
        {
            SubsystemBreeding.OnEntityAdd(entity);
        }

        public override void OnEntityRemove(Entity entity)
        {
            SubsystemBreeding.OnEntityRemove(entity);
        }

        public override void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnReadSpawnData(entity, spawnEntityData);
        }

        public override void OnSaveSpawnData(ComponentSpawn spawn, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnSaveSpawnData(spawn, spawnEntityData);
        }

#pragma warning disable CS0618
        public override void OnFactorsUpdate(ComponentFactors componentFactors, float dt)
        {
            SubsystemBreeding.OnFactorsUpdate(componentFactors, dt);
        }
#pragma warning restore CS0618

        public override void OnMinerHit(ComponentMiner miner,
            ComponentBody componentBody,
            Vector3 hitPoint,
            Vector3 hitDirection,
            ref float attackPower,
            ref float playerProbability,
            ref float creatureProbability,
            out bool hitted)
        {
            hitted = false;
            SubsystemBreeding.OnMinerHit(miner, componentBody, ref attackPower);
        }

        // ==================== 浮动文字渲染(参考原版 SurvivalCraftModLoader.OnModelRendererDrawExtra) ====================

        /// <summary>
        /// 每只生物模型绘制完毕后由 SubsystemModelsRenderer.Draw 回调。
        /// 在此为被追踪的繁殖生物入队 3 行浮动文字：
        ///   第1行：性别 + 生物显示名(例如 "♂公 灰狼")
        ///   第2行：成长阶段 + 繁殖状态(例如 "幼崽期 | 成长中" / "成年期 | 怀孕中(0.5天)")
        ///   第3行：成长进度百分比 + 文字进度条(例如 "成长 60% [███░░]")
        ///
        /// 用 modelsRenderer.PrimitivesRenderer.FontBatch(layer=1) 入队，
        /// 由 SubsystemModelsRenderer 在 DrawOrder=201 时统一 Flush(camera.ProjectionMatrix)，无需自己 Flush。
        /// </summary>
        public override void OnModelRendererDrawExtra(SubsystemModelsRenderer modelsRenderer,
            SubsystemModelsRenderer.ModelData modelData,
            Camera camera,
            float? alphaThreshold)
        {
            if (!SubsystemBreeding.Initialized) return;

            ComponentModel componentModel = modelData?.ComponentModel;
            if (componentModel == null) return;
            Entity entity = componentModel.Entity;
            if (entity == null) return;

            // 只处理被繁殖系统追踪的生物(非繁殖生物/玩家/船等直接跳过)
            BreedingState state = SubsystemBreeding.GetState(entity);
            if (state == null) return;

            BreedingConfig cfg = BreedingConfig.Current;
            SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
            if (species == null) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;
            ComponentBody body = creature.ComponentBody;
            if (body == null) return;

            // 跳过尸体
            ComponentHealth health = creature.ComponentHealth;
            if (health != null && health.DeathTime.HasValue) return;

            // 头顶世界坐标(参考原版 ComponentDisplayHealthAndNameBehavior)
            float height = body.BoxSize.Y;
            Vector3 headPos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.4f, 0f);
            Vector3 line2Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.2f, 0f);
            Vector3 line3Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.0f, 0f);

            // 转视图空间
            Vector3 vector = Vector3.Transform(headPos, camera.ViewMatrix);
            if (vector.Z >= 0f) return; // 在相机后方

            // 距离淡出：16m 内全显，19m 外全隐
            float fade = MathUtils.Saturate((vector.Length() - 16f) / 3f);
            Color color = Color.Lerp(Color.White, Color.Transparent, fade);
            if (color.A <= 6) return;

            // 视图空间 right/down 向量(参考原版 OnModelRendererDrawExtra)
            Vector3 right = Vector3.TransformNormal(
                0.005f * Vector3.Normalize(Vector3.Cross(camera.ViewDirection, camera.ViewUp)),
                camera.ViewMatrix);
            Vector3 down = Vector3.TransformNormal(-0.005f * Vector3.UnitY, camera.ViewMatrix);

            // 用原版同款字体(LabelWidget.BitmapFont)
            BitmapFont font = LabelWidget.BitmapFont;
            double currentDay = SubsystemBreeding.GetCurrentDay();

            // 字体批次(layer 1，由 SubsystemModelsRenderer 在 DrawOrder=201 统一 Flush)
            FontBatch3D fontBatch = modelsRenderer.PrimitivesRenderer.FontBatch(
                font, 1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp);

            // ==================== 第1行：性别 + 生物名称 ====================
            string line1 = state.GetGenderDisplayName() + " " + creature.DisplayName;
            fontBatch.QueueText(line1, vector, right, down, color, TextAnchor.HorizontalCenter | TextAnchor.Bottom);

            // ==================== 第2行：成长阶段 + 繁殖状态 ====================
            Vector3 vector2 = Vector3.Transform(line2Pos, camera.ViewMatrix);
            if (vector2.Z < 0f)
            {
                string line2 = state.GetStageDisplayName() + " | " + state.GetBreedingStatus(currentDay);
                fontBatch.QueueText(line2, vector2, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            // ==================== 第3行：成长进度百分比 + 进度条 ====================
            Vector3 vector3 = Vector3.Transform(line3Pos, camera.ViewMatrix);
            if (vector3.Z < 0f)
            {
                float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
                string line3 = "成长 " + ((int)(progress * 100f)).ToString() + "% " + BuildProgressBar(progress);
                fontBatch.QueueText(line3, vector3, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }
        }

        /// <summary>生成 6 格 ASCII 进度条。进度 0 → "[░░░░░░]"，进度 1 → "[██████]"。</summary>
        static string BuildProgressBar(float progress)
        {
            const int blocks = 6;
            int filled = (int)Math.Clamp(Math.Round(progress * blocks), 0, blocks);
            return "[" + new string('█', filled) + new string('░', blocks - filled) + "]";
        }
    }
}
