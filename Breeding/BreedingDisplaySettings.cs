using System;
using Engine;
using Game;

namespace Game
{
    /// <summary>
    /// 动物繁殖系统的"显示设置"读取器(全部走 modsettings.json 数据驱动)。
    ///
    /// 青蛙头顶悬浮文字设置 + 元素开关 + Neorxna 面板注入开关，统一放在"显示设置"页。
    /// 每个元素(名称/性别/成长阶段/繁殖状态/成长进度条)都可独立开关。
    /// </summary>
    public static class BreedingDisplaySettings
    {
        public const string PackageName = "Survivalcraft.AnimalBreeding";
        public const string DisplayPage = "BreedingDisplaySettings";

        // ---------- 头顶悬浮文字 ----------
        public static bool FloatingTextEnabled = true;
        public static float FloatingTextFontScale = 1f;
        /// <summary>悬浮文字整体上下偏移(方块)。正=上移，负=下移；避免大模型遮挡文字。</summary>
        public static float FloatingTextVerticalOffset = 0f;

        // ---------- 元素开关 ----------
        public static bool ShowName = true;
        public static bool ShowGender = true;
        public static bool ShowStage = true;
        public static bool ShowStatus = true;
        public static bool ShowGrowth = true;

        // ---------- Neorxna 面板注入(独立开关) ----------
        public static bool NuiEnabled = true;
        public static bool NuiShowGender = true;
        public static bool NuiShowStage = true;
        public static bool NuiShowStatus = true;
        public static bool NuiShowGrowth = true;

        static bool s_loaded;

        /// <summary>从 ModSettingsManager 读取全部设置(缺失回退默认)。</summary>
        public static void Load()
        {
            s_loaded = true;
            GetBool("FloatingTextEnabled", ref FloatingTextEnabled, true);
            GetFloat("FloatingTextFontScale", ref FloatingTextFontScale, 1f);
            GetFloat("FloatingTextVerticalOffset", ref FloatingTextVerticalOffset, 0f);

            GetBool("ShowName", ref ShowName, true);
            GetBool("ShowGender", ref ShowGender, true);
            GetBool("ShowStage", ref ShowStage, true);
            GetBool("ShowStatus", ref ShowStatus, true);
            GetBool("ShowGrowth", ref ShowGrowth, true);

            GetBool("NuiEnabled", ref NuiEnabled, true);
            GetBool("NuiShowGender", ref NuiShowGender, true);
            GetBool("NuiShowStage", ref NuiShowStage, true);
            GetBool("NuiShowStatus", ref NuiShowStatus, true);
            GetBool("NuiShowGrowth", ref NuiShowGrowth, true);
        }

        public static void EnsureLoaded()
        {
            if (!s_loaded) Load();
        }

        // ==================== 读取工具 ====================

        static void GetBool(string id, ref bool target, bool def)
        {
            target = ModSettingsManager.TryGet(out bool v, PackageName, DisplayPage, id) ? v : def;
        }

        static void GetFloat(string id, ref float target, float def)
        {
            if (ModSettingsManager.TryGet(out float v, PackageName, DisplayPage, id)) target = v;
            else target = def;
        }
    }
}