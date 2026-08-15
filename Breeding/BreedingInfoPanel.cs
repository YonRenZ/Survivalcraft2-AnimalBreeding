using System;
using Engine;
using Game;
using GameEntitySystem;

namespace Game
{
    /// <summary>
    /// 生物繁殖信息面板工具。构建信息行并添加到指定面板容器。
    /// 使用 InfoLine + InfoLineBuilder 构建图标+文本信息行。
    /// </summary>
    public static class BreedingInfoPanel
    {
        /// <summary>更新信息面板：清除旧信息，显示目标生物的繁殖状态。</summary>
        public static void Update(ContainerWidget panel, Entity targetEntity)
        {
            if (panel == null) return;

            panel.Children.Clear();
            if (targetEntity == null) return;

            BreedingState state = SubsystemBreeding.GetState(targetEntity);
            if (state == null) return;

            BreedingConfig cfg = BreedingConfig.Current;
            SpeciesConfig species = cfg?.GetSpecies(state.TemplateName);
            if (species == null) return;

            string gender = state.GetGenderDisplayName();
            panel.AddChildren(BuildLine(gender + " " + state.GetStageDisplayName(), Color.White));

            if (state.IsInEstrus)
                panel.AddChildren(BuildLine(LanguageControl.Get("BreedingMod", "Status", "Estrus"), new Color(255, 69, 0)));
            else if (state.PregnancyRemainingSeconds > 0f)
                panel.AddChildren(BuildLine(string.Format(LanguageControl.Get("BreedingMod", "Status", "Pregnant"), state.PregnancyRemainingSeconds.ToString("F0")), new Color(255, 192, 203)));
            else if (state.IsWeak)
                panel.AddChildren(BuildLine(string.Format(LanguageControl.Get("BreedingMod", "Status", "Weak"), state.WeaknessRemainingSeconds.ToString("F0")), Color.Gray));
            else if (species.RequireFeeding && !state.IsFed)
                panel.AddChildren(BuildLine(LanguageControl.Get("BreedingMod", "Status", "NeedFeeding"), Color.Yellow));
            else
                panel.AddChildren(BuildLine(LanguageControl.Get("BreedingMod", "Status", "NotInSeason"), Color.Gray));

            if (state.Stage == GrowthStage.Cub)
            {
                SubsystemTimeOfDay timeOfDay = targetEntity.Project?.FindSubsystem<SubsystemTimeOfDay>(true);
                double currentDay = timeOfDay != null ? timeOfDay.Day : 0d;
                float progress = state.GetGrowthProgress(currentDay, species.CubDurationDays);
                panel.AddChildren(BuildLine(string.Format(LanguageControl.Get("BreedingMod", "Growth"), ((int)Math.Round(progress * 100f)).ToString()), new Color(50, 205, 50)));
            }
        }

        /// <summary>用 InfoLineBuilder 构建单行信息。</summary>
        static Widget BuildLine(string text, Color color)
        {
            var line = new InfoLine();
            line.Segments.Add(new ColoredSegment(text, color));
            return InfoLineBuilder.Build(line);
        }
    }
}