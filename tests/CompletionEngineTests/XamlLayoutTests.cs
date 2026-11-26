using System.Linq;
using Avalonia.Ide.CompletionEngine.Navigation;
using Xunit;

namespace CompletionEngineTests
{
    public class XamlLayoutTests
    {
        /// <summary>Indent column for the line that starts at the '|' marker.</summary>
        private static int? Indent(string text, int tabSize = 4, int indentSize = 4)
        {
            var caret = text.IndexOf('|');
            return XamlLayout.GetIndentColumn(text.Remove(caret, 1), caret, tabSize, indentSize);
        }

        [Fact]
        public void Attributes_Align_With_First_Attribute()
        {
            Assert.Equal(12, Indent("<Window>\n    <Button Content=\"Save\"\n|Click=\"x\"/>\n</Window>"));
        }

        [Fact]
        public void Attributes_Align_When_Caret_Before_Closing_Bracket()
        {
            Assert.Equal(12, Indent("<Window>\n    <Button Content=\"Save\"\n|/>\n</Window>"));
        }

        [Fact]
        public void Attributes_Indent_One_Level_When_None_On_First_Line()
        {
            Assert.Equal(8, Indent("<Window>\n    <Button\n|Content=\"Save\"/>\n</Window>"));
        }

        [Fact]
        public void Content_After_Multiline_Start_Tag_Indents_From_The_Element()
        {
            // The XML editor indents this to the attribute column; it belongs one level in from <StackPanel.
            Assert.Equal(8, Indent("<Window>\n    <StackPanel x:Name=\"Panel\"\n                Spacing=\"4\">\n|\n    </StackPanel>\n</Window>"));
        }

        [Fact]
        public void End_Tag_Aligns_With_Its_Element()
        {
            Assert.Equal(4, Indent("<Window>\n    <StackPanel Spacing=\"4\"\n                Margin=\"2\">\n        <Button/>\n|</StackPanel>\n</Window>"));
        }

        [Fact]
        public void After_Self_Closing_Multiline_Tag_Uses_Parent_Indent()
        {
            Assert.Equal(4, Indent("<Window>\n    <Button Content=\"a\"\n            Click=\"b\"/>\n|\n</Window>"));
        }

        [Fact]
        public void Tabs_Count_As_Tab_Size()
        {
            Assert.Equal(12, Indent("<Window>\n\t<Button Content=\"Save\"\n|/>\n</Window>"));
        }

        [Fact]
        public void Inside_Multiline_Attribute_Value_Is_Left_Alone()
        {
            // Re-indenting here would change the attribute's value.
            Assert.Null(Indent("<Window>\n    <TextBlock Text=\"line one\n|line two\"/>\n</Window>"));
        }

        [Fact]
        public void Blocks_Are_Multiline_Elements_Only()
        {
            var text = "<Window>\n    <StackPanel>\n        <Button/>\n        <TextBlock>Hi</TextBlock>\n    </StackPanel>\n</Window>";
            var blocks = XamlLayout.GetBlocks(text);

            Assert.Equal(2, blocks.Count);
            Assert.Equal(new[] { "<Window>", "<StackPanel>" }, blocks.Select(b => text.Substring(b.Start, text.IndexOf('>', b.Start) - b.Start + 1)));
            Assert.All(blocks, b => Assert.StartsWith("</", text.Substring(b.EndTagStart)));
        }

        /// <summary>Exit position for the caret at '|', or null.</summary>
        private static int? Exit(string text)
        {
            var caret = text.IndexOf('|');
            return XamlLayout.GetAttributeExitPosition(XamlDocument.Parse(text.Remove(caret, 1)), caret);
        }

        [Theory]
        [InlineData("<Button Content=\"Save|\" Click=\"x\"/>")]
        [InlineData("<Button Content=\"Save|\"/>")]
        [InlineData("<Button Content='Save|'/>")]
        [InlineData("<TextBlock Text=\"{Binding Name}|\"/>")]
        [InlineData("<Button Content=\"|\"/>")]
        public void Enter_Before_Closing_Quote_Exits(string text)
        {
            var caret = text.IndexOf('|');
            Assert.Equal(caret + 1, Exit(text));
        }

        [Theory]
        [InlineData("<Button Content=\"Sa|ve\"/>")]
        [InlineData("<TextBlock Text=\"{Binding Name, |}\"/>")]
        [InlineData("<TextBlock Text=\"{Binding Name, Converter={StaticResource C}|\"/>")]
        [InlineData("<Button Content=\"Save\"|/>")]
        [InlineData("<Window>|</Window>")]
        [InlineData("<Button Content=\"Save|")]
        public void Enter_Elsewhere_Keeps_Normal_Behaviour(string text)
        {
            Assert.Null(Exit(text));
        }

        [Fact]
        public void MakeIndent_Uses_Tabs_When_Asked()
        {
            Assert.Equal("\t\t  ", XamlLayout.MakeIndent(10, useTabs: true, tabSize: 4));
            Assert.Equal(new string(' ', 10), XamlLayout.MakeIndent(10, useTabs: false, tabSize: 4));
        }
    }
}
