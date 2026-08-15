using System;
using Engine;
using Game;
using GameEntitySystem;

namespace Game
{
    /// <summary>
    /// 生物信息面板(屏幕顶部)。显示当前追踪生物的：名称+性别、血量条、繁殖状态。
    /// 血量条从 ComponentHealth 读取，颜色随血量变化(绿/黄/红)。
    /// </summary>
    public static class BreedingInfoPanel
    {
        const float HpBarWidth = 120f;
        const float HpBarHeight = 8f;

        static LabelWidget s_nameLabel;
        static RectangleWidget s_hpBar;
        static LabelWidget s_statusLabel;

        /// <summary>创建信息面板控件(竖排：名称 / 血量条 / 繁殖状态)。</summary>
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

            // 名称 + 性别
            s_nameLabel = new LabelWidget
            {
                FontScale = 0.85f,
                DropShadow = true,
                Color = Color.White,
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            panel.AddChildren(s_nameLabel);

            // 血量条(CanvasWidget 叠加背景 + 前景)
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
            hpCanvas.AddChildren(s_hpBar); // 前景覆盖背景(前景后添加，在上层)
            panel.AddChildren(hpCanvas);

            // 繁殖状态
            s_statusLabel = new LabelWidget
            {
                FontScale = 0.75f,
                DropShadow = true,
                Color = Color.Gray,
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            panel.AddChildren(s_statusLabel);

            return panel;
        }

        /// <summary>更新面板内容。</summary>
        public static void Update(ContainerWidget panel, Entity targetEntity)
        {
            if (panel == null || s_nameLabel == null || s_hpBar == null || s_statusLabel == null) return;

            if (targetEntity == null)
            {
                panel.IsVisible = false;
                return;
            }
            panel.IsVisible = true;

            ComponentCreature creature = targetEntity.FindComponent<ComponentCreature>();
            ComponentHealth health = targetEntity.FindComponent<ComponentHealth>();
            BreedingState state = SubsystemBreeding.GetState(targetEntity);

            // 名称 + 性别
            string name = creature != null ? creature.DisplayName : (targetEntity.ValuesDictionary.DatabaseObject?.Name ?? "?");
            string gender = state != null ? state.GetGenderDisplayName() : "";
            s_nameLabel.Text = (gender + " " + name).Trim();
            s_nameLabel.Color = state != null ? Color.White : Color.Gray;

            // 血量条
            float hp = health != null ? health.Health : 100f;
            float ratio = Math.Clamp(hp / 100f, 0f, 1f);
            s_hpBar.Size = new Vector2(HpBarWidth * ratio, HpBarHeight);
            s_hpBar.FillColor = ratio > 0.5f ? Color.Green : (ratio > 0.25f ? Color.Yellow : Color.Red);

            // 繁殖状态
            if (state != null)
            {
                BreedingConfig cfg = BreedingConfig.Current;
                SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
                if (species != null)
                {
                    Color c = Color.Gray;
                    string status;
                    if (state.IsInEstrus) { status = LanguageControl.Get("BreedingMod", "Status", "Estrus"); c = new Color(255, 69, 0); }
                    else if (state.PregnancyRemainingSeconds > 0f) { status = string.Format(LanguageControl.Get("BreedingMod", "Status", "Pregnant"), state.PregnancyRemainingSeconds.ToString("F0")); c = new Color(255, 192, 203); }
                    else if (state.IsWeak) { status = string.Format(LanguageControl.Get("BreedingMod", "Status", "Weak"), state.WeaknessRemainingSeconds.ToString("F0")); }
                    else if (species.RequireFeeding && !state.IsFed) { status = LanguageControl.Get("BreedingMod", "Status", "NeedFeeding"); c = Color.Yellow; }
                    else { status = LanguageControl.Get("BreedingMod", "Status", "NotInSeason"); }
                    s_statusLabel.Text = status;
                    s_statusLabel.Color = c;
                }
                else s_statusLabel.Text = "";
            }
            else s_statusLabel.Text = "";
        }
    }
}