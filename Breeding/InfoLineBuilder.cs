using Engine;
using Engine.Graphics;

namespace Game
{
    /// <summary>
    /// 将 InfoLine 构建为可显示的 Widget(StackPanelWidget 包含图标 + 文本分段)。
    /// 图标依赖 IconCache 加载，若图标为 null 则只显示文本。
    /// </summary>
    public static class InfoLineBuilder
    {
        public static Widget Build(InfoLine line)
        {
            if (line?.Segments == null || line.Segments.Count == 0)
                return null;

            var panel = new StackPanelWidget
            {
                Direction = LayoutDirection.Horizontal,
                Margin = new Vector2(10f, 0f),
                IsHitTestVisible = false
            };

            if (line.Icon != null)
            {
                panel.AddChildren(new RectangleWidget
                {
                    Subtexture = line.Icon,
                    FillColor = Color.White,
                    OutlineColor = Color.Transparent,
                    OutlineThickness = 0f,
                    Size = new Vector2(14f, 14f),
                    HorizontalAlignment = WidgetAlignment.Center,
                    VerticalAlignment = WidgetAlignment.Center,
                    IsHitTestVisible = false
                });
                panel.AddChildren(new CanvasWidget
                {
                    Size = new Vector2(3f, 14f),
                    IsHitTestVisible = false
                });
            }

            foreach (var segment in line.Segments)
            {
                panel.AddChildren(new LabelWidget
                {
                    Text = segment.Text,
                    Color = segment.Color,
                    FontScale = 0.85f,
                    DropShadow = true,
                    IsHitTestVisible = false,
                    HorizontalAlignment = WidgetAlignment.Center
                });
            }
            return panel;
        }
    }
}