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
    /// 联机版 ModLoader 缺失单机版大部分钩子(OnProjectLoaded/OnProjectDisposed/
    /// ProjectXmlLoad/ProjectXmlSave/OnEntityAdd/OnEntityRemove 等)，本适配改用：
    ///   · 初始化/卸载：通过 GameManager.Project 静态属性每帧检测世界加载/卸载，惰性初始化
    ///   · 每帧驱动：实现 IUpdateable 注册到 SubsystemUpdate(AddUpdateable)，同时 override SubsystemUpdate 钩子(双保险)
    ///   · 实体同步：每帧全量对比 GameManager.Project.Entities 与追踪表(替代 OnEntityAdd/OnEntityRemove)
    ///   · 存档：无 ProjectXmlSave 钩子 → 使用独立文件 BreedingStates.xml(定期 + 卸载时保存)
    ///   · 渲染：OnModelRendererDrawExtra(联机版签名)
    ///   · 季节：无 SubsystemSeasons → 游戏天伪季节(30天/季)
    ///   · 骑乘拦截(ScoreMount)/图鉴介绍钩子：联机版未提供 → 已移除
    /// </summary>
    public class BreedingModLoader : ModLoader, IUpdateable
    {
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        Project m_lastProject;
        bool m_updateableRegistered;
        double m_nextSaveTime;

        const double SaveIntervalSeconds = 30.0; // 定期保存状态文件间隔(现实秒)

        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("SubsystemUpdate", this);
            ModsManager.RegisterHook("OnMinerHit", this);
            ModsManager.RegisterHook("OnModelRendererDrawExtra", this);
            ModsManager.RegisterHook("OnEatPickable", this);
            ModsManager.RegisterHook("OnXdbLoad", this);

            Log.Information("[BreedingMod] 动物繁殖系统模组初始化(联机版 SurvivalcraftNet 适配)");
        }

        /// <summary>
        /// 构建图鉴文本 = 生物介绍(Lang) + 动态基础信息(配置读取)。
        /// 无介绍也无配置时返回 null。
        /// </summary>
        static string BuildBestiaryText(string templateName)
        {
            System.Text.StringBuilder sb = new();

            string intro = LanguageControl.Get(out bool foundIntro, "BreedingMod", "SpeciesDescription", templateName);
            if (foundIntro && !string.IsNullOrEmpty(intro))
            {
                sb.Append(intro);
            }

            BreedingConfig cfg = BreedingConfig.Current;
            if (cfg == null)
            {
                BreedingConfig.Load();
                cfg = BreedingConfig.Current;
            }
            SpeciesConfig species = cfg?.GetSpecies(templateName);
            if (species != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(string.Format(
                    LanguageControl.Get("BreedingMod", "Stats", "Attack"),
                    (species.AdultAttackFactor * species.MaleAttackBonus).ToString("0.##"),
                    (species.AdultAttackFactor * 1.0).ToString("0.##")));
                sb.AppendLine(string.Format(
                    LanguageControl.Get("BreedingMod", "Stats", "Size"),
                    species.AdultMaleBoxScale.ToString("0.##"),
                    species.AdultFemaleBoxScale.ToString("0.##")));
                sb.AppendLine(string.Format(
                    LanguageControl.Get("BreedingMod", "Stats", "Time"),
                    (species.GestationSeconds / 1200f).ToString("0.##"),
                    species.CubDurationDays.ToString("0.##"),
                    (species.WeaknessSeconds / 1200f).ToString("0.##")));
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// 联机版图鉴无注入钩子(BestiaryDescriptionScreen 直接读数据库 Description)。
        /// 此处用 OnXdbLoad 在数据库加载时，为每个生物模板的 Creature.Description 写入
        /// "介绍 + Stats"(攻击力/体型/孕期/成长期/恢复期)，使图鉴详情页能显示扩展信息。
        /// 注意：会修改数据库模板 Description(其他读取处也会显示)；Stats 在加载时按配置生成。
        /// </summary>
        public override void OnXdbLoad(XElement xElement)
        {
            try
            {
                if (xElement == null) return;

                int injected = 0;
                foreach (XElement entityTemplate in xElement.Descendants("EntityTemplate"))
                {
                    string templateName = entityTemplate.Attribute("Name")?.Value;
                    if (string.IsNullOrEmpty(templateName)) continue;

                    string text = BuildBestiaryText(templateName);
                    if (string.IsNullOrEmpty(text)) continue;

                    foreach (XElement member in entityTemplate.Elements("MemberComponentTemplate"))
                    {
                        if (member.Attribute("Name")?.Value != "Creature") continue;

                        foreach (XElement param in member.Elements("Parameter"))
                        {
                            if (param.Attribute("Name")?.Value == "Description")
                            {
                                param.SetAttributeValue("Value", text);
                                injected++;
                            }
                        }
                        break;
                    }
                }
                Log.Information($"[Breeding] 图鉴生物介绍注入完成: {injected} 个物种");
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] OnXdbLoad 生物介绍注入失败: " + e.Message);
            }
        }

        /// <summary>每帧驱动(钩子，若联机版调用)。</summary>
        public override void SubsystemUpdate(float dt)
        {
            Tick(dt);
        }

        /// <summary>每帧驱动(IUpdateable，由 SubsystemUpdate.AddUpdateable 注册调用)。</summary>
        public void Update(float dt)
        {
            Tick(dt);
        }

        /// <summary>实体 Add 前回调：把已确定的繁殖状态写入实体(随 EntityPackage 同步给客户端)。</summary>
        void OnBeforeEntityAdded(object sender, EntityAddRemoveEventArgs e)
        {
            if (e?.Entity == null) return;
            SubsystemBreeding.OnBeforeEntityAdded(e.Entity);
        }

        /// <summary>联机版核心：每帧检测世界状态 + 实体同步 + 繁殖更新。</summary>
        void Tick(float dt)
        {
            try
            {
                Project project = GameManager.Project;

                // 世界加载 → 初始化
                if (project != null && project != m_lastProject)
                {
                    if (m_lastProject != null)
                    {
                        // 世界切换：清理旧世界
                        SubsystemBreeding.ClearXmlCache();
                    }
                    SubsystemBreeding.Initialize(project);
                    m_lastProject = project;

                    // 联机版：实体 Add 前(EntityPackage 生成前)把已确定的繁殖状态写入实体，
                    // 确保客户端通过 EntityPackage 同步时能拿到状态(性别/阶段/孕期/成长等)。
                    project.BeforeEntityAdded += OnBeforeEntityAdded;

                    // 注册每帧更新(双保险之一)
                    if (!m_updateableRegistered)
                    {
                        SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(true);
                        subsystemUpdate?.AddUpdateable(this);
                        m_updateableRegistered = true;
                    }
                }
                // 世界卸载 → 清理
                else if (project == null && m_lastProject != null)
                {
                    if (m_lastProject is GameEntitySystem.Project p)
                    {
                        p.BeforeEntityAdded -= OnBeforeEntityAdded;
                    }
                    SubsystemBreeding.ClearXmlCache();
                    m_lastProject = null;
                    m_updateableRegistered = false;
                }

                if (project == null) return;

                // 实体全量同步(替代缺失的 OnEntityAdd/OnEntityRemove 钩子)
                SubsystemBreeding.SyncEntities(project);

                // 繁殖逻辑每帧更新
                SubsystemBreeding.Update(dt);

                // 定期保存状态文件(替代缺失的 ProjectXmlSave 钩子)
                if (dt > 0f && SubsystemBreeding.GetCurrentGameTime() >= m_nextSaveTime)
                {
                    SubsystemBreeding.SaveStatesToFile();
                    m_nextSaveTime = SubsystemBreeding.GetCurrentGameTime() + SaveIntervalSeconds;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 联机版每帧更新异常: " + e.Message);
            }
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

        /// <summary>生物吃掉落物时触发，处理"喂食求偶"逻辑。</summary>
        public override void OnEatPickable(ComponentEatPickableBehavior eatPickableBehavior, Pickable EatPickable, out bool Dealed)
        {
            SubsystemBreeding.OnEatPickable(eatPickableBehavior, EatPickable, out Dealed);
        }

        // ==================== 浮动文字渲染(OnModelRendererDrawExtra) ====================

        /// <summary>
        /// 每个 ComponentModel 由 SubsystemModelsRenderer 渲染时触发(联机版钩子)。
        /// 为被追踪的繁殖生物入队 3 行浮动文字 + 1 个图形进度条。
        /// </summary>
        public override void OnModelRendererDrawExtra(SubsystemModelsRenderer modelsRenderer, ComponentModel componentModel, Camera camera, float? alphaThreshold)
        {
            if (!SubsystemBreeding.Initialized) return;
            if (modelsRenderer == null) return;

            Entity entity = componentModel?.Entity;
            if (entity == null) return;

            BreedingState state = SubsystemBreeding.GetState(entity);
            if (state == null) return;

            BreedingConfig cfg = BreedingConfig.Current;
            SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
            if (species == null) return;

            ComponentCreature creature = entity.FindComponent<ComponentCreature>();
            if (creature == null) return;
            ComponentBody body = creature.ComponentBody;
            if (body == null) return;

            ComponentHealth health = creature.ComponentHealth;
            if (health != null && health.DeathTime.HasValue) return;

            // 当前成长进度(用于视觉缩放 + 第3行进度)
            double currentDay = SubsystemBreeding.GetCurrentDay();
            float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);

            // 视觉模型缩放：联机版无 ModelScale，改用根骨骼缩放(SetBoneTransform + 重算绝对骨骼)。
            // 只对幼崽期缩放(Cub→成年按 CubBoxScale→AdultScale 线性)；成年不缩放(保持原版)。
            // 注：若某模型根骨骼带动画可能受影响(多数生物根骨骼是纯层级根, 无动画)。
            try
            {
                if (state.Stage == GrowthStage.Cub && componentModel.Model != null)
                {
                    ModelBone rootBone = componentModel.Model.RootBone;
                    if (rootBone != null)
                    {
                        float adultScale = state.Gender == BreedingGender.Male
                            ? species.AdultMaleBoxScale : species.AdultFemaleBoxScale;
                        float scale = species.CubBoxScale + (adultScale - species.CubBoxScale) * progress;
                        // 左乘 scale 保留根骨骼原有的平移/旋转，避免模型错位
                        componentModel.SetBoneTransform(rootBone.Index, Matrix.CreateScale(scale) * rootBone.Transform);
                        componentModel.CalculateAbsoluteBonesTransforms(camera);
                    }
                }
            }
            catch (Exception)
            {
                // 骨骼缩放失败不影响其他功能
            }

            float height = body.BoxSize.Y;
            Vector3 headPos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.5f, 0f);
            Vector3 line2Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.35f, 0f);
            Vector3 line3Pos = body.Position + Vector3.UnitY * height + new Vector3(0f, 0.20f, 0f);

            Vector3 vector = Vector3.Transform(headPos, camera.ViewMatrix);
            if (vector.Z >= 0f) return;

            float fade = MathUtils.Saturate((vector.Length() - 16f) / 3f);
            Color color = Color.Lerp(Color.White, Color.Transparent, fade);
            if (color.A <= 6) return;

            Vector3 right = Vector3.TransformNormal(
                0.005f * Vector3.Normalize(Vector3.Cross(camera.ViewDirection, camera.ViewUp)),
                camera.ViewMatrix);
            Vector3 down = Vector3.TransformNormal(-0.005f * Vector3.UnitY, camera.ViewMatrix);

            BitmapFont font = LabelWidget.BitmapFont;

            FontBatch3D fontBatch = modelsRenderer.PrimitivesRenderer.FontBatch(
                font, 1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp);

            // 第1行：性别 + 生物名称
            string line1 = state.GetGenderDisplayName() + " " + creature.DisplayName;
            fontBatch.QueueText(line1, vector, right, down, color, TextAnchor.HorizontalCenter | TextAnchor.Bottom);

            // 第2行：成长阶段 + 繁殖状态
            Vector3 vector2 = Vector3.Transform(line2Pos, camera.ViewMatrix);
            if (vector2.Z < 0f)
            {
                string line2 = state.GetStageDisplayName() + " | " + state.GetBreedingStatus(species);
                fontBatch.QueueText(line2, vector2, right, down, color * 0.85f, TextAnchor.HorizontalCenter | TextAnchor.Bottom);
            }

            // 第3行：成长进度百分比 + 右侧图形进度条
            Vector3 vector3 = Vector3.Transform(line3Pos, camera.ViewMatrix);
            if (vector3.Z < 0f)
            {
                int percent = (int)Math.Round(progress * 100f);
                string line3 = string.Format(LanguageControl.Get("BreedingMod", "Growth"), percent.ToString());

                Vector2 textSize = font.MeasureText(line3, new Vector2(right.Length(), down.Length()), Vector2.Zero);
                float textWidthPx = textSize.X / right.Length();
                float textHeightPx = textSize.Y / down.Length();
                const float barWidth = 100f;
                const float barHeight = 9f;
                const float gapPx = 6f;
                float totalWidthPx = textWidthPx + gapPx + barWidth;
                float halfTotalPx = totalWidthPx * 0.5f;

                Vector3 textPos = vector3 + right * -halfTotalPx;
                fontBatch.QueueText(line3, textPos, right, down, color * 0.85f, TextAnchor.Left | TextAnchor.Bottom);

                float vAlignOffsetPx = (textHeightPx - barHeight) * 0.5f + 3.0f;
                Vector3 barOrigin = textPos + right * (textWidthPx + gapPx) + down * -vAlignOffsetPx;
                DrawProgressBar(modelsRenderer, barOrigin, right, down, progress, color);
            }
        }

        /// <summary>用 FlatBatch3D 绘制带白色边框的矩形进度条。</summary>
        static void DrawProgressBar(SubsystemModelsRenderer modelsRenderer,
            Vector3 barOrigin, Vector3 right, Vector3 down,
            float progress, Color baseColor)
        {
            FlatBatch3D flatBatch = modelsRenderer.PrimitivesRenderer.FlatBatch(
                1,
                DepthStencilState.DepthRead,
                RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend);

            const float barWidth = 100f;
            const float barHeight = 9f;
            const float zBias = 0.01f;

            Vector3 topLeft = barOrigin + Vector3.UnitZ * zBias;
            Vector3 topRgt  = topLeft + right * barWidth;
            Vector3 botLeft = topLeft + down * barHeight;
            Vector3 botRgt  = topRgt  + down * barHeight;

            Color bgColor = new Color(40, 40, 40, 180) * baseColor;
            QueueRect(flatBatch, topLeft, topRgt, botLeft, botRgt, bgColor);

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

            Color borderColor = new Color(240, 240, 240, 230) * baseColor;
            flatBatch.QueueLine(topLeft, topRgt, borderColor);
            flatBatch.QueueLine(botLeft, botRgt, borderColor);
            flatBatch.QueueLine(topLeft, botLeft, borderColor);
            flatBatch.QueueLine(topRgt, botRgt, borderColor);
        }

        /// <summary>用两个三角形拼成实心矩形。</summary>
        static void QueueRect(FlatBatch3D flatBatch,
            Vector3 topLeft, Vector3 topRgt, Vector3 botLeft, Vector3 botRgt, Color color)
        {
            flatBatch.QueueTriangle(topLeft, topRgt, botLeft, color);
            flatBatch.QueueTriangle(topRgt, botLeft, botRgt, color);
        }
    }
}
