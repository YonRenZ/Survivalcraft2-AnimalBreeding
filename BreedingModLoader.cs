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
    /// 动物繁殖系统独立模组的加载入口。
    /// 仅注册繁殖系统所需的钩子，不依赖荒野科技主模组的任何功能。
    /// 所有逻辑委托给 SubsystemBreeding 静态类。
    ///
    /// 浮动文字渲染：通过 OnModelDrawExtra 钩子(ComponentModel.DrawExtras 回调)实现。
    /// 该钩子对所有 ComponentModel(蒙皮 + 非蒙皮)都会触发，
    /// 因此能覆盖原版 .dae 模型与第三方 glTF/PBR 蒙皮模型(如 HC 模组的生物)。
    /// 用 SubsystemBreeding.ModelsRenderer.PrimitivesRenderer.FontBatch(...).QueueText(...) 入队文字(layer 1)，
    /// 由 SubsystemModelsRenderer 在 DrawOrder=201 时统一 Flush，不需要自己 Flush。
    /// </summary>
    public class BreedingModLoader : ModLoader
    {
        public override void __ModInitialize()
        {
            // 动物繁殖系统相关钩子：实体生命周期、存档读写、每帧更新、攻击力修正、模型绘制扩展
            ModsManager.RegisterHook("OnProjectLoaded", this);
            ModsManager.RegisterHook("OnProjectDisposed", this);
            ModsManager.RegisterHook("ProjectXmlLoad", this);
            ModsManager.RegisterHook("ProjectXmlSave", this);
            ModsManager.RegisterHook("OnProjectXmlSaved", this);
            ModsManager.RegisterHook("OnEntityAdd", this);
            ModsManager.RegisterHook("OnEntityRemove", this);
            ModsManager.RegisterHook("OnReadSpawnData", this);
            ModsManager.RegisterHook("OnSaveSpawnData", this);
            ModsManager.RegisterHook("OnFactorsUpdate", this);
            ModsManager.RegisterHook("OnMinerHit", this);
            ModsManager.RegisterHook("OnModelDrawExtra", this);
            ModsManager.RegisterHook("ScoreMount", this);
            ModsManager.RegisterHook("OnEatPickable", this);
            ModsManager.RegisterHook("LoadCreatureInfoInBestiaryScreen", this);
            ModsManager.RegisterHook("UpdateCreaturePropertiesInBestiaryDescriptionScreen", this);

            Log.Information("[BreedingMod] 动物繁殖系统模组初始化(含 OnModelDrawExtra 渲染钩子 + OnEatPickable 喂食钩子)");
        }

        /// <summary>当 Project 加载完成时执行。繁殖系统在此缓存子系统引用 + 加载配置。</summary>
        public override void OnProjectLoaded(Project project)
        {
            SubsystemBreeding.Initialize(project);
        }

        /// <summary>Project 卸载时清理静态缓存，避免跨世界残留。</summary>
        public override void OnProjectDisposed()
        {
            SubsystemBreeding.ClearXmlCache();
        }

        // ==================== Project.xml 持久化(活着的生物状态) ====================

        /// <summary>
        /// 世界加载时、ProjectData 构造之前触发。读取 Project.xml 中的活体生物繁殖状态。
        /// 时序：ProjectXmlLoad → ProjectData 构造(创建实体) → OnEntityAdd → OnProjectLoaded(Initialize)。
        /// 注：使用单参数重载(兼容旧版 DLL；三参数重载在较新版本才提供)。
        /// </summary>
#pragma warning disable CS0618
        public override void ProjectXmlLoad(XElement xElement)
        {
            SubsystemBreeding.LoadXmlStates(xElement);
        }
#pragma warning restore CS0618

        /// <summary>
        /// 世界保存时、ProjectData.Save 之前触发(备用保存点)。
        /// 把活体生物繁殖状态写入 Project.xml。SaveXmlStates 内部有 Remove 旧节点逻辑，重复调用安全。
        /// </summary>
        public override void ProjectXmlSave(XElement xElement)
        {
            SubsystemBreeding.SaveXmlStates(xElement);
        }

        /// <summary>
        /// 世界保存时、ProjectData.Save 之后、写盘之前触发(主保存点)。
        /// 把活体生物繁殖状态写入 Project.xml。被 Despawn 的生物已通过 OnSaveSpawnData 保存，
        /// 此处只处理 s_states 中仍存活的生物。
        /// </summary>
        public override void OnProjectXmlSaved(XElement xElement)
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

        public override void OnReadSpawnData(Entity entity, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnReadSpawnData(entity, spawnEntityData);
        }

        public override void OnSaveSpawnData(ComponentSpawn spawn, SpawnEntityData spawnEntityData)
        {
            SubsystemBreeding.OnSaveSpawnData(spawn, spawnEntityData);
        }

        // ==================== 图鉴生物介绍(LoadCreatureInfoInBestiaryScreen / UpdateCreaturePropertiesInBestiaryDescriptionScreen) ====================

        /// <summary>
        /// 构建图鉴文本 = 生物介绍(Lang) + 动态基础信息(配置读取)。
        /// 无介绍也无配置时返回 null(保留原版描述)。
        /// </summary>
        static string BuildBestiaryText(string templateName)
        {
            System.Text.StringBuilder sb = new();

            // 1. 模组生物介绍(zh-CN 中文 / en-US 英文，其他语言回退英文；缺失则跳过)
            string intro = LanguageControl.Get(out bool foundIntro, "BreedingMod", "SpeciesDescription", templateName);
            if (foundIntro && !string.IsNullOrEmpty(intro))
            {
                sb.Append(intro);
            }

            // 2. 动态基础信息(攻击力/体型/时间，从配置读取，多语言标签)
            BreedingConfig cfg = BreedingConfig.Current;
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
        /// 图鉴列表页(BestiaryScreen)加载每个生物条目时触发：条目 Details 显示介绍+Stats。
        /// </summary>
        public override void LoadCreatureInfoInBestiaryScreen(BestiaryScreen bestiaryScreen,
            ContainerWidget creatureInfoWidget,
            BestiaryCreatureInfo bestiaryCreatureInfo,
            ValuesDictionary entityValuesDictionary)
        {
            try
            {
                if (creatureInfoWidget == null || entityValuesDictionary == null) return;

                string templateName = entityValuesDictionary.DatabaseObject?.Name;
                if (string.IsNullOrEmpty(templateName)) return;

                string text = BuildBestiaryText(templateName);
                if (string.IsNullOrEmpty(text)) return;

                LabelWidget details = creatureInfoWidget.Children.Find<LabelWidget>("BestiaryItem.Details");
                if (details != null) details.Text = text;
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 图鉴列表生物介绍加载失败: " + e.Message);
            }
        }

        /// <summary>
        /// 图鉴详情页(BestiaryDescriptionScreen)每次显示/切换生物时触发：
        /// 在详情页 Description 区域显示介绍+Stats(攻击力/体型/孕期/成长期/恢复期)。
        /// </summary>
        public override void UpdateCreaturePropertiesInBestiaryDescriptionScreen(BestiaryDescriptionScreen bestiaryDescriptionScreen,
            BestiaryCreatureInfo bestiaryCreatureInfo,
            ValuesDictionary entityValuesDictionary)
        {
            try
            {
                if (bestiaryDescriptionScreen == null || entityValuesDictionary == null) return;

                string templateName = entityValuesDictionary.DatabaseObject?.Name;
                if (string.IsNullOrEmpty(templateName)) return;

                string text = BuildBestiaryText(templateName);
                if (string.IsNullOrEmpty(text)) return;

                // 详情页 Description 标签(介绍 + Stats)
                LabelWidget description = bestiaryDescriptionScreen.Children.Find<LabelWidget>("Description");
                if (description != null) description.Text = text;
            }
            catch (Exception e)
            {
                Log.Warning("[Breeding] 图鉴详情生物介绍加载失败: " + e.Message);
            }
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

        // ==================== 骑乘拦截 ====================

        public override void ScoreMount(ComponentRider componentRider, ComponentMount componentMount, out float? score)
        {
            SubsystemBreeding.OnScoreMount(componentRider, componentMount, out score);
        }

        // ==================== 喂食发情 ====================

        /// <summary>
        /// 生物吃掉落物时触发。委托给 SubsystemBreeding 处理"喂食发情"逻辑。
        /// 此钩子在生物吃完物品(Count 已扣减)后触发，用于标记该个体为"已喂食"。
        /// </summary>
        public override void OnEatPickable(ComponentEatPickableBehavior eatPickableBehavior, Pickable eatPickable, out bool dealed)
        {
            SubsystemBreeding.OnEatPickable(eatPickableBehavior, eatPickable, out dealed);
        }

        // ==================== 浮动文字渲染(OnModelDrawExtra 对蒙皮+非蒙皮模型均触发) ====================

        /// <summary>
        /// 每个 ComponentModel 绘制完毕后由 ComponentModel.DrawExtras 回调。
        /// 在此为被追踪的繁殖生物入队 3 行浮动文字 + 1 个图形进度条：
        ///   第1行：性别 + 生物显示名(例如 "♂公 灰狼")
        ///   第2行：成长阶段 + 繁殖状态(例如 "幼崽期 | 成长中" / "成年期 | 怀孕中(0.5天)")
        ///   第3行：成长进度百分比(例如 "成长 60%")
        ///   第4行：图形进度条(FlatBatch3D 画矩形，背景灰 + 前景绿按进度填充)
        ///
        /// 文字用 SubsystemBreeding.ModelsRenderer.PrimitivesRenderer.FontBatch(layer=1) 入队，
        /// 进度条用 FlatBatch(layer=1) 画矩形，均由 SubsystemModelsRenderer 在 DrawOrder=201 时统一 Flush。
        /// </summary>
        public override void OnModelDrawExtra(ComponentModel componentModel, Camera camera, out bool skip)
        {
            skip = false;
            if (!SubsystemBreeding.Initialized) return;

            SubsystemModelsRenderer modelsRenderer = SubsystemBreeding.ModelsRenderer;
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
            // 整体下移到头顶上方 0.5 格(原 0.9，下移 0.4)；行距 0.15 格
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
                // 注: TextAnchor.Bottom 时 textPos 是文字左下角，文字向上延伸绘制
                Vector3 textPos = vector3 + right * -halfTotalPx;
                fontBatch.QueueText(line3, textPos, right, down, color * 0.85f, TextAnchor.Left | TextAnchor.Bottom);

                // 进度条：紧跟文字右侧 gapPx 像素处；垂直居中于文字再额外上移 3.0 像素
                //   textPos 是文字左下角，文字顶部 = textPos + up * textHeightPx
                //   基准：进度条顶部 = 文字顶部 - (文字高 - 进度条高) / 2 (垂直居中)
                //   微调：再沿 up 方向偏移 3.0 像素(进度条相对文字再上移，补偿字体基线偏移)
                float vAlignOffsetPx = (textHeightPx - barHeight) * 0.5f + 3.0f;
                Vector3 barOrigin = textPos + right * (textWidthPx + gapPx) + down * -vAlignOffsetPx;
                DrawProgressBar(modelsRenderer, barOrigin, right, down, progress, color);
            }
        }

        /// <summary>
        /// 用 FlatBatch3D 在视图空间绘制带白色边框的矩形进度条(与文字同一行，紧贴文字右侧)。
        /// 单位说明：right/down 向量长度 = 0.005，即 1 单位 = 1 屏幕像素。
        /// barOrigin 为进度条左上角预期位置(视图空间)，方法内部做 Z 偏置。
        /// 绘制层次(由内到外，所有顶点朝相机方向 +Z 偏置 zBias 避免被自身模型遮挡)：
        ///   1. 背景填充矩形(灰半透明，固定大小，覆盖整个进度条区域)
        ///   2. 前景填充矩形(绿色，宽度 = 总宽 × progress，从左对齐填充)
        ///   3. 白色边框(4 条线，固定大小，不随 progress 变化)
        /// 关于"两个三角形"：GPU 没有矩形图元，QueueTriangle×2 沿对角线拼矩形是标准做法。
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

            const float barWidth = 100f;     // 进度条总宽(像素) — 加长版
            const float barHeight = 9f;      // 进度条高度(像素)
            const float zBias = 0.01f;       // 朝相机 Z 偏置，避免被自身模型遮挡

            // 进度条四角(视图空间)：barOrigin 为左上角预期位置，朝相机偏置 zBias
            Vector3 topLeft = barOrigin + Vector3.UnitZ * zBias;
            Vector3 topRgt = topLeft + right * barWidth;
            Vector3 botLeft = topLeft + down * barHeight;
            Vector3 botRgt = topRgt + down * barHeight;

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
            //    边框稍亮但保留淡出，与文字一致
            Color borderColor = new Color(240, 240, 240, 230) * baseColor;
            flatBatch.QueueLine(topLeft, topRgt, borderColor);   // 上边
            flatBatch.QueueLine(botLeft, botRgt, borderColor);   // 下边
            flatBatch.QueueLine(topLeft, botLeft, borderColor);  // 左边
            flatBatch.QueueLine(topRgt, botRgt, borderColor);    // 右边
        }

        /// <summary>
        /// 用两个三角形拼成实心矩形(修复引擎 QueueQuad 单色版的三角形重叠 bug)。
        ///
        /// 引擎 FlatBatch3D.QueueQuad(p1,p2,p3,p4,color) 单色版的三角形拆分为：
        ///   △1 = (p1,p2,p3)，△2 = (p3,p4,p1)
        /// 注释标 p1=左上 p2=右上 p3=左下 p4=右下，则 △2 的对角边是 p1-p3(即左边)，
        /// 导致两三角形沿左边重叠、尖角分别朝右上和右下 → 视觉上像"共用底边、尖在不同处"的错位矩形。
        ///
        /// 本方法改用正确拆分(对角线 p2-p3，即右上到左下)：
        ///   △1 = (p1,p2,p3) = (左上,右上,左下)
        ///   △2 = (p2,p3,p4) = (右上,左下,右下)
        /// 两三角形沿对角线 p2-p3 拼合，刚好覆盖整个矩形区域，无重叠无缺口。
        /// </summary>
        static void QueueRect(FlatBatch3D flatBatch,
            Vector3 topLeft, Vector3 topRgt, Vector3 botLeft, Vector3 botRgt, Color color)
        {
            flatBatch.QueueTriangle(topLeft, topRgt, botLeft, color); // △1: 左上,右上,左下
            flatBatch.QueueTriangle(topRgt, botLeft, botRgt, color);  // △2: 右上,左下,右下
        }
    }
}
