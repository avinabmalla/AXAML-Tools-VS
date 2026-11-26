using System.Linq;
using Avalonia.Ide.CompletionEngine.Navigation;
using Xunit;

namespace CompletionEngineTests
{
    public class XamlPickingTests : XamlCompletionTestBase
    {
        private static XamlPickingMap Create(string body)
        {
            var text = PrologueText + body + "</UserControl>";
            return XamlPicking.Create(XamlDocument.Parse(text), CreateHelper(text));
        }

        [Fact]
        public void Controls_Get_Distinct_Colours_On_Their_Brush_Properties()
        {
            var map = Create("<StackPanel><Button Content=\"A\"/><TextBlock Text=\"B\"/></StackPanel>");

            // UserControl, StackPanel, Button and TextBlock all have Background.
            Assert.Equal(4, map.Count);

            var picked = XamlDocument.Parse(map.Xaml);
            var button = picked.Elements.Single(e => e.Name == "Button");
            var textBlock = picked.Elements.Single(e => e.Name == "TextBlock");

            Assert.NotEqual(button.GetAttribute("Background")!.Value, textBlock.GetAttribute("Background")!.Value);
            Assert.Equal(button.GetAttribute("Background")!.Value, button.GetAttribute("Foreground")!.Value);
            Assert.Equal("A", button.GetAttribute("Content")!.Value);
        }

        [Fact]
        public void Existing_Brush_Values_Are_Replaced_Not_Duplicated()
        {
            var map = Create("<Border Background=\"{Binding Brush}\" BorderBrush=\"Red\"/>");
            var border = XamlDocument.Parse(map.Xaml).Elements.Single(e => e.Name == "Border");

            Assert.Single(border.Attributes, a => a.Name == "Background");
            Assert.StartsWith("#FF", border.GetAttribute("Background")!.Value);
            Assert.Equal(border.GetAttribute("Background")!.Value, border.GetAttribute("BorderBrush")!.Value);
        }

        [Fact]
        public void Property_Element_Brushes_Are_Not_Set_Twice()
        {
            var map = Create("<Button><Button.Background><SolidColorBrush Color=\"Red\"/></Button.Background></Button>");
            var button = XamlDocument.Parse(map.Xaml).Elements.Single(e => e.Name == "Button");

            Assert.Null(button.GetAttribute("Background"));
            Assert.NotNull(button.GetAttribute("Foreground"));
        }

        [Fact]
        public void Non_Visual_Elements_Are_Untouched()
        {
            var map = Create("<UserControl.Styles><Style Selector=\"Button\"><Setter Property=\"Margin\" Value=\"2\"/></Style></UserControl.Styles>");
            var picked = XamlDocument.Parse(map.Xaml);

            Assert.All(picked.Elements.Where(e => e.Name is "Style" or "Setter"), e => Assert.Empty(e.Attributes.Where(a => a.Name == "Background")));
        }

        [Fact]
        public void Disabled_Controls_Are_Enabled_In_The_Id_Render()
        {
            var map = Create("<StackPanel IsEnabled=\"False\"><Button IsEnabled=\"{Binding CanSave}\" Command=\"{Binding SaveCommand}\" Content=\"A\"/></StackPanel>");
            var picked = XamlDocument.Parse(map.Xaml);
            var panel = picked.Elements.Single(e => e.Name == "StackPanel");
            var button = picked.Elements.Single(e => e.Name == "Button");

            Assert.Equal("True", panel.GetAttribute("IsEnabled")!.Value);
            Assert.Equal("True", button.GetAttribute("IsEnabled")!.Value);
            Assert.Null(button.GetAttribute("Command"));
            Assert.Equal("A", button.GetAttribute("Content")!.Value);
            Assert.NotNull(button.GetAttribute("Background"));
        }

        [Fact]
        public void HitTest_Finds_Element_At_Pixel_And_Nearby()
        {
            var map = Create("<StackPanel><Button Content=\"A\"/></StackPanel>");
            var picked = XamlDocument.Parse(map.Xaml);
            var color = int.Parse(picked.Elements.Single(e => e.Name == "Button").GetAttribute("Background")!.Value.Substring(3), System.Globalization.NumberStyles.HexNumber);

            // A 10x10 BGRA frame: all an unknown colour except one pixel of the button's ID colour.
            const int size = 10;
            var frame = new byte[size * size * 4];
            for (var i = 0; i < frame.Length; i += 4)
            {
                frame[i] = 1; frame[i + 1] = 2; frame[i + 2] = 3; frame[i + 3] = 255;
            }

            var p = (5 * size + 5) * 4;
            frame[p] = (byte)color; frame[p + 1] = (byte)(color >> 8); frame[p + 2] = (byte)(color >> 16);

            Assert.Equal("Button", map.HitTest(frame, size, size, size * 4, 5, 5)!.Name);
            Assert.Equal("Button", map.HitTest(frame, size, size, size * 4, 7, 6)!.Name);
            Assert.Null(map.HitTest(frame, size, size, size * 4, 0, 0, searchRadius: 2));
        }

        [Fact]
        public void Colours_Are_Unique_And_Not_Black_Or_White()
        {
            var colors = Enumerable.Range(1, 5000).Select(XamlPicking.ColorFor).ToList();
            Assert.Equal(colors.Count, colors.Distinct().Count());
            Assert.DoesNotContain(0, colors);
            Assert.DoesNotContain(0xFFFFFF, colors);
        }
    }
}
