using System.Linq;
using Avalonia.Ide.CompletionEngine.Navigation;
using Xunit;

namespace CompletionEngineTests
{
    public class XamlPreviewTransformTests : XamlCompletionTestBase
    {
        private static (string Before, string After) Transform(string body)
        {
            var text = PrologueText + body + "</UserControl>";
            return (text, XamlPreviewTransform.RemoveEventHandlers(XamlDocument.Parse(text), CreateHelper(text)));
        }

        [Fact]
        public void Event_Handlers_Are_Removed_And_Properties_Kept()
        {
            var (_, after) = Transform("<Button x:Name=\"Save\" Content=\"Save\" Click=\"Save_Click\" IsEnabled=\"True\"/>");
            var button = XamlDocument.Parse(after).Elements.Single(e => e.Name == "Button");

            Assert.Null(button.GetAttribute("Click"));
            Assert.Equal("Save", button.GetAttribute("Content")!.Value);
            Assert.Equal("Save", button.GetXamlDirective("Name")!.Value);
            Assert.Equal("True", button.GetAttribute("IsEnabled")!.Value);
        }

        [Fact]
        public void Attached_Events_Are_Removed()
        {
            var (_, after) = Transform("<Border Gestures.Tapped=\"Card_Tapped\" Width=\"10\"/>");
            var border = XamlDocument.Parse(after).Elements.Single(e => e.Name == "Border");

            Assert.Null(border.GetAttribute("Gestures.Tapped"));
            Assert.Equal("10", border.GetAttribute("Width")!.Value);
        }

        [Fact]
        public void Positions_Are_Preserved()
        {
            var (before, after) = Transform("<StackPanel>\n  <Button Click=\"A\"\n          Content=\"B\"/>\n  <TextBlock Text=\"C\"/>\n</StackPanel>");

            Assert.Equal(before.Length, after.Length);
            Assert.Equal(before.Split('\n').Length, after.Split('\n').Length);
            Assert.Equal(before.IndexOf("Content=\"B\""), after.IndexOf("Content=\"B\""));
            Assert.Equal(before.IndexOf("<TextBlock"), after.IndexOf("<TextBlock"));
        }

        [Fact]
        public void Xaml_Without_Events_Is_Unchanged()
        {
            var (before, after) = Transform("<Button Content=\"B\" Command=\"{Binding Save}\"/>");
            Assert.Equal(before, after);
        }
    }
}
