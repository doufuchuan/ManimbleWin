using System.Windows;
using System.Windows.Controls;
using ManimbleWin.Models;

namespace ManimbleWin.Helpers
{
    /// <summary>根据 CanvasObject.Kind 选择对应的 DataTemplate。</summary>
    public class ObjectTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }
        public DataTemplate TexTemplate { get; set; }
        public DataTemplate ShapeTemplate { get; set; }
        public DataTemplate ImageTemplate { get; set; }
        public DataTemplate TableTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is CanvasObject o)
            {
                switch (o.Kind)
                {
                    case ObjectKind.Tex: return TexTemplate;
                    case ObjectKind.Shape:
                    case ObjectKind.Line: return ShapeTemplate;
                    case ObjectKind.Image: return ImageTemplate;
                    case ObjectKind.Table: return TableTemplate;
                    default: return TextTemplate;
                }
            }
            return base.SelectTemplate(item, container);
        }
    }
}
