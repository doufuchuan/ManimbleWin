using System.Windows;
using System.Windows.Controls;
using ManimbleWin.Models;

namespace ManimbleWin.Views
{
    public class PreviewTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }
        public DataTemplate TexTemplate { get; set; }
        public DataTemplate ShapeTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is CanvasObject o)
            {
                switch (o.Kind)
                {
                    case ObjectKind.Tex: return TexTemplate;
                    case ObjectKind.Shape:
                    case ObjectKind.Line:
                    case ObjectKind.Table:
                    case ObjectKind.Image: return ShapeTemplate;
                    default: return TextTemplate;
                }
            }
            return TextTemplate;
        }
    }
}
