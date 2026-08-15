using System.Collections.Generic;
using Engine;

namespace Game
{
    /// <summary>
    /// 带颜色的文本片段。
    /// </summary>
    public readonly struct ColoredSegment
    {
        public readonly string Text;
        public readonly Color Color;

        public ColoredSegment(string text, Color color)
        {
            Text = text;
            Color = color;
        }
    }

    /// <summary>
    /// 信息行：包含一个图标(可选)和多个带颜色的文本片段。
    /// 由 InfoLineBuilder 构建为可视 Widget。
    /// </summary>
    public class InfoLine
    {
        public Subtexture Icon;
        public List<ColoredSegment> Segments = new List<ColoredSegment>();
    }
}