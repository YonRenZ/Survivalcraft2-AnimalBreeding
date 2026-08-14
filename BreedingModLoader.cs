using System;
using System.Xml.Linq;
using Engine;
using Engine.Graphics;
using Engine.Media;
using GameEntitySystem;
using TemplatesDatabase;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统 · 联机版(SurvivalcraftNet)适配入口。
    ///
    /// 与单机版的差异适配：
    ///   · 每帧驱动：联机版无 OnFactorsUpdate 钩子 → 本类实现 IUpdateable，
    ///     在 OnProjectLoaded 时注册到 SubsystemUpdate(AddUpdateable)，OnProjectDisposed 注销。
    ///   · 渲染：无 OnModelDrawExtra → 改用 OnModelRendererDrawExtra(签名含 alphaThreshold)。
    ///   · 存档：无 OnReadSpawnData/OnSaveSpawnData/OnProjectXmlSaved →
    ///       活体生物状态用 ProjectXmlSave 写入 Project.xml(BreedingModStates)，
    ///       Despawn 生物状态联机版无法随实体保存 → 重新生成时按 EntityId 确定性分配性别兜底。
    ///   · 骑乘拦截(ScoreMount)/图鉴介绍钩子：联机版未提供 → 已移除。
    ///   · 季节：联机版无 SubsystemSeasons → SubsystemBreeding 用游戏天自算伪季节(30天/季)。
    /// </summary>
    public class BreedingModLoader : ModLoader, IUpdateable
    {
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("OnProjectLoaded", this);
            ModsManager.RegisterHook("OnProjectDisposed", this);
            ModsManager.RegisterHook("ProjectXmlLoad", this);
            ModsManager.RegisterHook("ProjectXmlSave", this);
            ModsManager.RegisterHook("OnEntityAdd", this);
            ModsManager.RegisterHook("OnEntityRemove", this);
            ModsManager.RegisterHook("OnMinerHit", this);
            ModsManager.RegisterHook("OnModelRendererDrawExtra", this);
            ModsManager.RegisterHook("OnEatPickable", this);

            Log.Information("[BreedingMod] 动物繁殖系统模组初始化(联机版 SurvivalcraftNet 适配)");
        }

        /// <summary>当 Project 加载完成时执行：初始化繁殖系统 + 注册每帧更新。</summary>
        public override void OnProjectLoaded(Project project)
        {
            SubsystemBreeding.Initialize(project);
            // 联机版无 OnFactorsUpdate 钩子，模组自己注册为 IUpdateable 实现每帧更新
            SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(true);
            subsystemUpdate?.AddUpdateable(this);
        }

        /// <summary>Project 卸载时注销更新并清理缓存。</summary>
        public override void OnProjectDisposed()
        {
            Project project = SubsystemBreeding.ProjectInstance;
            if (project != null)
            {
                SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(true);
                subsystemUpdate?.RemoveUpdateable(this);
            }
            SubsystemBreeding.ClearXmlCache();
        }

        /// <summary>每帧更新(由 SubsystemUpdate 驱动)。</summary>
        public void Update(float dt)
        {
            SubsystemBreeding.Update(dt);
        }

        // ==================== Project.xml 持久化(活着的生物状态) ====================

        /// <summary>世界加载时读取 Project.xml 中的活体生物繁殖状态。</summary>
#pragma warning disable CS0618
        public override void ProjectXmlLoad(XElement xElement)
        {
            SubsystemBreeding.LoadXmlStates(xElement);
        }
#pragma warning restore CS0618

        /// <summary>
        /// 世界保存时把活体生物繁殖状态写入 Project.xml。
        /// 联机版无 OnProjectXmlSaved，此为主保存点(写入 BreedingModStates 节点)。
        /// </summary>
        public override void ProjectXmlSave(XElement xElement)
        {
            SubsystemBreeding.SaveXmlStates(xElement);
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

        // ==================== 喂食求偶 ====================

        /// <summary>生物吃掉落物时触发，处理"喂食求偶"逻辑。</summary>
        public override void OnEatPickable(ComponentEatPickableBehavior eatPickableBehavior, Pickable EatPickable, out bool Dealed)
        {
            SubsystemBreeding.OnEatPickable(eatPickableBehavior, EatPickable, out Dealed);
        }

        // ==================== 浮动文字渲染(OnModelRendererDrawExtra) ====================

        /// <summary>
        /// 每个 ComponentModel 由 SubsystemModelsRenderer 渲染时触发(联机版钩子)。
        /// 为被追踪的繁殖生物入队 3 行浮动文字 + 1 个图形进度条：
        ///   第1行：性别 + 生物显示名(例如 "♂公 灰狼")
        ///   第2行：成长阶段 + 繁殖状态(例如 "幼崽期 | 成长中" / "成年期 | 孕期中(0.5天)")
        ///   第3行：成长进度百分比(例如 "成长 60%")
        ///   第4行：图形进度条(FlatBatch3D 画矩形，背景灰 + 前景绿按进度填充)
        /// </summary>
        public override void OnModelRendererDrawExtra(SubsystemModelsRenderer modelsRenderer, ComponentModel componentModel, Camera camera, float? alphaThreshold)
        {
            if (!SubsystemBreeding.Initialized) return;
            if (modelsRenderer == null) return;

            Entity entity = componentModel?.Entity;
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
            Vector3 headPos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.5f, 0f);
            Vector3 line2Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.35f, 0f);
            Vector3 line3Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.20f, 0f);

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

            // 字体批次(layer 1，由 SubsystemModelsRenderer 统一 Flush)
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
                string line2 = state.GetStageDisplayName() + " | " + state.GetBreedingStatus(species);
                fontBatch.QueueText(line2, vector2, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            // ==================== 第3行：成长进度百分比 + 右侧图形进度条(同一行) ====================
            Vector3 vector3 = Vector3.Transform(line3Pos, camera.ViewMatrix);
            if (vector3.Z < 0f)
            {
                float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
                int percent = (int)Math.Round(progress * 100f);
                string line3 = string.Format(LanguageControl.Get("BreedingMod", "Growth"), percent.ToString());

                // 测量文字尺寸(视图空间单位)，用于定位进度条起点 + 整体水平居中 + 垂直对齐
                Vector2 textSize = font.MeasureText(line3, new Vector2(right.Length(), down.Length()), Vector2.Zero);
                float textWidthPx = textSize.X / right.Length();  // 文字宽(像素，与 barWidth 同尺度)
                float textHeightPx = textSize.Y / down.Length();  // 文字高(像素，与 barHeight 同尺度)
                const float barWidth = 100f;     // 进度条总宽(像素)
                const float barHeight = 9f;      // 进度条高度(像素)
                const float gapPx = 6f;          // 文字与进度条之间的间隙(像素)
                float totalWidthPx = textWidthPx + gapPx + barWidth;
                float halfTotalPx = totalWidthPx * 0.5f;

                // 文字左对齐：起点 = vector3 左移 halfTotalPx(让整体居中)，垂直底部对齐
                Vector3 textPos = vector3 + right * -halfTotalPx;
                fontBatch.QueueText(line3, textPos, right, down, color * 0.85f, TextAnchor.Left | TextAnchor.Bottom);

                // 进度条：紧跟文字右侧 gapPx 像素处；垂直居中于文字再额外上移 3.0 像素
                float vAlignOffsetPx = (textHeightPx - barHeight) * 0.5f + 3.0f;
                Vector3 barOrigin = textPos + right * (textWidthPx + gapPx) + down * -vAlignOffsetPx;
                DrawProgressBar(modelsRenderer, barOrigin, right, down, progress, color);
            }
        }

        /// <summary>
        /// 用 FlatBatch3D 在视图空间绘制带白色边框的矩形进度条(与文字同一行，紧贴文字右侧)。
        /// </summary>
        static void DrawProgressBar(SubsystemModelsRenderer modelsRenderer,
            Vector3 barOrigin, Vector3 right, Vector3 down,
            float progress, Color baseColor)
        {
            FlatBatch3D flatBatch = modelsRenderer.PrimitivesRenderer.FlatBatch(
                1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend);

            const float barWidth = 100f;     // 进度条总宽(像素)
            const float barHeight = 9f;      // 进度条高度(像素)
            const float zBias = 0.01f;       // 朝相机 Z 偏置，避免被自身模型遮挡

            Vector3 topLeft = barOrigin + Vector3.UnitZ * zBias;
            Vector3 topRgt  = topLeft + right * barWidth;
            Vector3 botLeft = topLeft + down * barHeight;
            Vector3 botRgt  = topRgt  + down * barHeight;

            // 1. 背景填充矩形(灰半透明，固定大小)
            Color bgColor = new Color(40, 40, 40, 180) * baseColor;
            QueueRect(flatBatch, topLeft, topRgt, botLeft, botRgt, bgColor);

            // 2. 前景填充矩形(绿色，宽度 = 总宽 × progress，左对齐)
            float filledW = barWidth * Math.Clamp(progress, 0f, 1f);
            if (filledW > 0f)
            {
                Color fgColor = new Color(80, 200, 80, 220) * baseColor;
                Vector3 fgTL = topLeft;
                Vector3 fgTR = topLeft + right * filledW;
                Vector3 fgBL = botLeft;
                Vector3 fgBR = botLeft + right * filledW;
                QueueRect(flatBatch, fgTL, fgTR, fgBL, fgBR, fgColor);
            }

            // 3. 白色边框(4 条线，固定大小，不随 progress 变化)
            Color borderColor = new Color(240, 240, 240, 230) * baseColor;
            flatBatch.QueueLine(topLeft, topRgt, borderColor);
            flatBatch.QueueLine(botLeft, botRgt, borderColor);
            flatBatch.QueueLine(topLeft, botLeft, borderColor);
            flatBatch.QueueLine(topRgt, botRgt, borderColor);
        }

        /// <summary>用两个三角形拼成实心矩形(修复引擎 QueueQuad 单色版的三角形重叠 bug)。</summary>
        static void QueueRect(FlatBatch3D flatBatch,
            Vector3 topLeft, Vector3 topRgt, Vector3 botLeft, Vector3 botRgt, Color color)
        {
            flatBatch.QueueTriangle(topLeft, topRgt, botLeft, color);
            flatBatch.QueueTriangle(topRgt, botLeft, botRgt, color);
        }
    }
}
