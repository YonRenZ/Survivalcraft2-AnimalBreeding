using System;
using Engine;
using Game;
using GameEntitySystem;

namespace Game
{
    /// <summary>
    /// 生物繁殖信息面板(屏幕顶部，仅未装 Neorxna 时的回退方案)。
    /// 仅当玩家准星对准一只「可繁殖生物」(被繁殖系统追踪、BreedingState 非空)时显示。
    /// 各元素(名称/性别/成长阶段/繁殖状态/成长进度条)可由模组设置独立开关；
    /// 繁殖状态显示在成长阶段【后面】(如 "幼崽期 · 求偶中")。
    /// </summary>
    public static class BreedingInfoPanel
    {
        const float HpBarWidth = 120f;
        const float HpBarHeight = 8f;
        const float GrowthBarWidth = 120f;
        const float GrowthBarHeight = 8f;

        static readonly Color MaleColor = new Color(120, 180, 255);
        static readonly Color FemaleColor = new Color(255, 140, 200);
        static readonly Color CubColor = new Color(255, 200, 80);
        static readonly Color AdultColor = new Color(90, 210, 110);

        static LabelWidget s_nameLabel;
        static RectangleWidget s_hpBar;
        static LabelWidget s_stageStatusLabel;
        static LabelWidget s_growthLabel;
        static CanvasWidget s_growthCanvas;
        static RectangleWidget s_growthBar;

        /// <summary>创建信息面板控件(竖排：名称+性别 / 血量条 / 阶段·状态 / 成长值+进度条)。</summary>
        public static StackPanelWidget Create()
        {
            var panel = new StackPanelWidget
            {
                Direction = LayoutDirection.Vertical,
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Near,
                Margin = new Vector2(10f, 5f),
                IsHitTestVisible = false
            };

            // 1. 名称 + 性别(性别色高亮)
            s_nameLabel = new LabelWidget
            {
                FontScale = 0.85f,
                DropShadow = true,
                Color = Color.White,
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            panel.AddChildren(s_nameLabel);

            // 2. 血量条(CanvasWidget 叠加背景 + 前景)
            var hpCanvas = new CanvasWidget
            {
                Size = new Vector2(HpBarWidth, HpBarHeight),
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            hpCanvas.AddChildren(new RectangleWidget
            {
                FillColor = new Color(0, 0, 0, 140),
                OutlineColor = new Color(80, 80, 80, 200),
                OutlineThickness = 1f,
                Size = new Vector2(HpBarWidth, HpBarHeight),
                IsHitTestVisible = false
            });
            s_hpBar = new RectangleWidget
            {
                FillColor = Color.Green,
                Size = new Vector2(HpBarWidth, HpBarHeight),
                IsHitTestVisible = false
            };
            hpCanvas.AddChildren(s_hpBar);
            panel.AddChildren(hpCanvas);

            // 3. 成长阶段 · 繁殖状态(状态在阶段后)
            s_stageStatusLabel = new LabelWidget
            {
                FontScale = 0.75f,
                DropShadow = true,
                Color = Color.White,
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            panel.AddChildren(s_stageStatusLabel);

            // 4. 成长值文本 + 成长进度条
            s_growthLabel = new LabelWidget
            {
                FontScale = 0.75f,
                DropShadow = true,
                Color = Color.White,
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            panel.AddChildren(s_growthLabel);

            s_growthCanvas = new CanvasWidget
            {
                Size = new Vector2(GrowthBarWidth, GrowthBarHeight),
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            s_growthCanvas.AddChildren(new RectangleWidget
            {
                FillColor = new Color(0, 0, 0, 140),
                OutlineColor = new Color(120, 160, 120, 200),
                OutlineThickness = 1f,
                Size = new Vector2(GrowthBarWidth, GrowthBarHeight),
                IsHitTestVisible = false
            });
            s_growthBar = new RectangleWidget
            {
                FillColor = AdultColor,
                Size = new Vector2(GrowthBarWidth, GrowthBarHeight),
                IsHitTestVisible = false
            };
            s_growthCanvas.AddChildren(s_growthBar);
            panel.AddChildren(s_growthCanvas);

            return panel;
        }

        /// <summary>更新面板内容。仅当准星指向「可繁殖生物」时显示，否则隐藏。</summary>
        public static void Update(ContainerWidget panel, Entity targetEntity)
        {
            if (panel == null || s_nameLabel == null || s_hpBar == null
                || s_stageStatusLabel == null || s_growthLabel == null || s_growthCanvas == null || s_growthBar == null) return;

            // 非可繁殖生物(未追踪)或未指向任何生物 → 隐藏面板
            BreedingState state = targetEntity != null ? SubsystemBreeding.GetState(targetEntity) : null;
            if (state == null)
            {
                panel.IsVisible = false;
                return;
            }
            panel.IsVisible = true;

            ComponentCreature creature = targetEntity.FindComponent<ComponentCreature>();
            ComponentHealth health = targetEntity.FindComponent<ComponentHealth>();
            string name = creature != null ? creature.DisplayName : (targetEntity.ValuesDictionary.DatabaseObject?.Name ?? "?");
            string gender = state.GetGenderDisplayName();

            // ==================== 1. 名称 + 性别(性别拼到名称后) ====================
            string nameText = (BreedingDisplaySettings.ShowName ? name : "")
                            + (BreedingDisplaySettings.ShowGender ? gender : "");
            s_nameLabel.Text = nameText.Trim();
            s_nameLabel.IsVisible = nameText.Length > 0;
            s_nameLabel.Color = state.Gender == BreedingGender.Male ? MaleColor : FemaleColor;

            // ==================== 2. 血量条(始终显示) ====================
            float hp = health != null ? health.Health : 100f;
            float hpRatio = Math.Clamp(hp / 100f, 0f, 1f);
            s_hpBar.Size = new Vector2(HpBarWidth * hpRatio, HpBarHeight);
            s_hpBar.FillColor = hpRatio > 0.5f ? Color.Green : (hpRatio > 0.25f ? Color.Yellow : Color.Red);

            // ==================== 3. 成长阶段 · 繁殖状态(状态在阶段后) ====================
            BreedingConfig cfg = BreedingConfig.Current;
            SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
            string stage = state.GetStageDisplayName();
            string status = species != null ? state.GetBreedingStatus(species) : "";
            string ss = (BreedingDisplaySettings.ShowStage ? stage : "")
                      + ((BreedingDisplaySettings.ShowStage && BreedingDisplaySettings.ShowStatus) ? " · " : "")
                      + (BreedingDisplaySettings.ShowStatus ? status : "");
            s_stageStatusLabel.Text = ss.Trim();
            s_stageStatusLabel.IsVisible = ss.Length > 0;

            // ==================== 4. 成长值 + 成长进度条 ====================
            double currentDay = SubsystemBreeding.GetCurrentDay();
            float growth = state.GetGrowthProgress(currentDay, species != null ? species.CubDurationDays : 0f);
            int percent = (int)Math.Round(growth * 100f);
            bool showGrowth = BreedingDisplaySettings.ShowGrowth;
            s_growthLabel.Text = string.Format(LanguageControl.Get("BreedingMod", "Growth"), percent.ToString());
            s_growthLabel.IsVisible = showGrowth;
            s_growthCanvas.IsVisible = showGrowth;
            s_growthBar.Size = new Vector2(GrowthBarWidth * Math.Clamp(growth, 0f, 1f), GrowthBarHeight);
            s_growthBar.FillColor = state.IsAdult ? AdultColor : CubColor;
        }
    }
}
