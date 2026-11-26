using System.Linq;
using Avalonia.Ide.CompletionEngine.Navigation;
using Xunit;

namespace CompletionEngineTests
{
    public class XamlScannerTests
    {
        [Fact]
        public void Scans_Elements_Attributes_And_Parents()
        {
            var text = "<Window x:Class=\"A.B\"><!-- <Fake/> --><StackPanel><Button x:Name=\"Save\" Content='Hi'/></StackPanel></Window>";
            var elements = XamlScanner.Scan(text);

            Assert.Equal(new[] { "Window", "StackPanel", "Button" }, elements.Select(e => e.Name));
            var button = elements[2];
            Assert.Equal("Save", button.ElementName);
            Assert.Equal("Hi", button.GetAttribute("Content")!.Value);
            Assert.True(button.IsSelfClosing);
            Assert.Equal("StackPanel", button.Parent!.Name);
            Assert.Equal("A.B", elements[0].GetXamlDirective("Class")!.Value);
            Assert.Equal(text.Length, elements[0].End);
        }

        [Fact]
        public void Tolerates_Unterminated_Tags()
        {
            var text = "<Window><Button Click=\"\n<TextBlock Text=\"x\"/>";
            var elements = XamlScanner.Scan(text);

            Assert.Equal(new[] { "Window", "Button", "TextBlock" }, elements.Select(e => e.Name));
            Assert.Equal("", elements[1].GetAttribute("Click")!.Value.Trim());
        }

        [Fact]
        public void Finds_Start_Tag_At_Position()
        {
            var text = "<Window><Button Click=\"\" /></Window>";
            var elements = XamlScanner.Scan(text);

            Assert.Equal("Button", XamlScanner.FindStartTagAt(elements, text.IndexOf("Click")).Name);
            Assert.Equal("Window", XamlScanner.FindInnermostAt(elements, text.IndexOf("</Window>")).Name);
        }
    }

    public class EventHandlerNamingTests : XamlCompletionTestBase
    {
        private static XamlElement Element(string tag) => XamlScanner.Scan(tag)[0];

        [Theory]
        [InlineData("<Button x:Name=\"SaveButton\" Click=\"\"/>", "Click", "SaveButton_Click")]
        [InlineData("<Button Click=\"\" x:Name=\"SaveButton\"/>", "Click", "SaveButton_Click")]
        [InlineData("<Button Name=\"SaveButton\" Click=\"\"/>", "Click", "SaveButton_Click")]
        [InlineData("<Button Click=\"\"/>", "Click", "Button_Click")]
        [InlineData("<local:MyButton Click=\"\"/>", "Click", "MyButton_Click")]
        [InlineData("<Border x:Name=\"Card\" Gestures.Tapped=\"\"/>", "Gestures.Tapped", "Card_Tapped")]
        [InlineData("<Button x:Name=\"save-button\" Click=\"\"/>", "Click", "save_button_Click")]
        public void Uses_XName_Then_Type(string xaml, string eventAttribute, string expected)
        {
            Assert.Equal(expected, EventHandlerNaming.Create(Element(xaml), eventAttribute));
        }

        [Fact]
        public void Supports_On_Prefix_Pattern()
        {
            Assert.Equal("OnSaveButtonClick",
                EventHandlerNaming.Create(Element("<Button x:Name=\"SaveButton\"/>"), "Click", EventHandlerNaming.OnPrefixPattern));
        }

        [Fact]
        public void Completion_Uses_XName_Written_After_The_Event()
        {
            var comp = GetCompletionsFor("<Button Click=\"", "\" x:Name=\"SaveButton\"/>");

            Assert.NotNull(comp);
            Assert.Single(comp.Completions);
            Assert.Equal("SaveButton_Click", comp.Completions[0].InsertText);
        }

        [Theory]
        [InlineData("<Button x:Name=\"btnClickMe\" Click=\"", "\" />")]
        [InlineData("<Button x:Name=\"btnClickMe\" Content=\"Click Me\" Click=\"", "\"></Button>")]
        [InlineData("<StackPanel>\r\n  <Button x:Name=\"btnClickMe\"\r\n          Click=\"", "\" />\r\n</StackPanel>")]
        [InlineData("<Button Click=\"", "\" x:Name=\"btnClickMe\"/>")]
        public void Completion_Uses_XName_As_Typed_In_VS(string before, string after)
        {
            var comp = GetCompletionsFor(before, after);

            Assert.NotNull(comp);
            Assert.Equal("btnClickMe_Click", comp.Completions.Single().InsertText);
        }

        [Fact]
        public void Completion_Uses_XName_Written_Before_The_Event()
        {
            var comp = GetCompletionsFor("<Button x:Name=\"SaveButton\" Click=\"");

            Assert.NotNull(comp);
            Assert.Equal("SaveButton_Click", comp.Completions.Single().InsertText);
        }
    }

    public class NavigationClassifierTests : XamlCompletionTestBase
    {
        /// <summary>Classifies the position marked with '|' in <paramref name="xaml"/>.</summary>
        private static XamlNavigationTarget Classify(string xaml, bool withMetadata = true)
        {
            var caret = xaml.IndexOf('|');
            var text = PrologueText + xaml.Remove(caret, 1) + "</UserControl>";
            var position = PrologueText.Length + caret;
            return XamlNavigationClassifier.Classify(text, position, withMetadata ? CreateHelper(text) : null);
        }

        [Fact]
        public void Element_Tag_Is_Type()
        {
            var target = Classify("<But|ton/>");
            Assert.Equal(XamlNavigationKind.Type, target.Kind);
            Assert.Equal("Avalonia.Controls.Button", target.TypeName);
        }

        [Fact]
        public void Clr_Namespace_Type_Resolves_Without_Metadata()
        {
            var target = Classify("<local:My|Button/>", withMetadata: false);
            Assert.Equal(XamlNavigationKind.Type, target.Kind);
            Assert.Equal("CompletionEngineTests.Models.MyButton", target.TypeName);
        }

        [Fact]
        public void Property_Attribute_Uses_Declaring_Type()
        {
            var target = Classify("<Button Cont|ent=\"x\"/>");
            Assert.Equal(XamlNavigationKind.Property, target.Kind);
            Assert.Equal("Content", target.MemberName);
            Assert.Equal("Avalonia.Controls.ContentControl", target.TypeName);
        }

        [Fact]
        public void Attached_Property_Owner_And_Member()
        {
            var member = Classify("<Button Grid.R|ow=\"1\"/>");
            Assert.Equal(XamlNavigationKind.Property, member.Kind);
            Assert.True(member.IsAttached);
            Assert.Equal("Row", member.MemberName);
            Assert.Equal("Avalonia.Controls.Grid", member.TypeName);

            var owner = Classify("<Button Gr|id.Row=\"1\"/>");
            Assert.Equal(XamlNavigationKind.Type, owner.Kind);
            Assert.Equal("Avalonia.Controls.Grid", owner.TypeName);
        }

        [Fact]
        public void Property_Element()
        {
            var target = Classify("<Grid><Grid.RowDefin|itions/></Grid>");
            Assert.Equal(XamlNavigationKind.Property, target.Kind);
            Assert.Equal("RowDefinitions", target.MemberName);
            Assert.Equal("Avalonia.Controls.Grid", target.TypeName);
        }

        [Fact]
        public void Event_Attribute_Value_Is_Handler()
        {
            var target = Classify("<Button Click=\"Save|Button_Click\"/>");
            Assert.Equal(XamlNavigationKind.EventHandler, target.Kind);
            Assert.Equal("SaveButton_Click", target.MemberName);
            Assert.Equal("Click", target.EventName);
            Assert.False(target.IsAttached);
        }

        [Fact]
        public void XName_Value()
        {
            var target = Classify("<Button x:Name=\"Sa|ve\"/>");
            Assert.Equal(XamlNavigationKind.XName, target.Kind);
            Assert.Equal("Save", target.MemberName);
        }

        [Fact]
        public void XDataType_Value_Is_Type()
        {
            var target = Classify("<TextBlock x:DataType=\"local:My|Button\"/>");
            Assert.Equal(XamlNavigationKind.Type, target.Kind);
            Assert.Equal("CompletionEngineTests.Models.MyButton", target.TypeName);
        }

        [Fact]
        public void Binding_Path_Segment_With_DataType_In_Scope()
        {
            var target = Classify("<StackPanel x:DataType=\"local:MyButton\"><TextBlock Text=\"{Binding Content.Le|ngth, Mode=OneWay}\"/></StackPanel>");
            Assert.Equal(XamlNavigationKind.BindingPath, target.Kind);
            Assert.Equal(new[] { "Content", "Length" }, target.PathSegments);
            Assert.Equal(1, target.PathIndex);
            Assert.Equal("CompletionEngineTests.Models.MyButton", target.DataTypeName);
        }

        [Fact]
        public void Binding_Path_Named_Argument_And_Negation()
        {
            var target = Classify("<TextBlock x:DataType=\"local:MyButton\" IsVisible=\"{Binding Path=!IsEn|abled}\"/>");
            Assert.Equal(XamlNavigationKind.BindingPath, target.Kind);
            Assert.Equal("IsEnabled", target.MemberName);
            Assert.Equal(0, target.PathIndex);
        }

        [Fact]
        public void Binding_To_Named_Element()
        {
            var xaml = "<StackPanel><TextBox x:Name=\"Input\"/><TextBlock Text=\"{Binding #Input.Te|xt}\"/></StackPanel>";
            var target = Classify(xaml);
            Assert.Equal(XamlNavigationKind.BindingPath, target.Kind);
            Assert.Equal(new[] { "Text" }, target.PathSegments);
            Assert.Equal("Avalonia.Controls.TextBox", target.DataTypeName);

            var name = Classify("<StackPanel><TextBox x:Name=\"Input\"/><TextBlock Text=\"{Binding #In|put.Text}\"/></StackPanel>");
            Assert.Equal(XamlNavigationKind.XName, name.Kind);
            Assert.Equal("Input", name.MemberName);
        }

        [Fact]
        public void Static_Resource_Key()
        {
            var target = Classify("<Border Background=\"{StaticResource Accent|Brush}\"/>");
            Assert.Equal(XamlNavigationKind.ResourceKey, target.Kind);
            Assert.Equal("AccentBrush", target.MemberName);
        }

        [Fact]
        public void Nested_Markup_Extension()
        {
            var target = Classify("<TextBlock Text=\"{Binding Name, Converter={StaticResource My|Converter}}\"/>");
            Assert.Equal(XamlNavigationKind.ResourceKey, target.Kind);
            Assert.Equal("MyConverter", target.MemberName);
        }

        [Fact]
        public void XStatic_Type_And_Member()
        {
            var member = Classify("<TextBlock Text=\"{x:Static local:AttachedBehavior.Is|Enabled}\"/>");
            Assert.Equal(XamlNavigationKind.StaticMember, member.Kind);
            Assert.Equal("IsEnabled", member.MemberName);
            Assert.Equal("CompletionEngineTests.Models.AttachedBehavior", member.TypeName);

            var type = Classify("<TextBlock Text=\"{x:Static local:Attached|Behavior.IsEnabled}\"/>");
            Assert.Equal(XamlNavigationKind.Type, type.Kind);
        }

        [Fact]
        public void XType_Argument()
        {
            var target = Classify("<ContentControl Tag=\"{x:Type local:My|Button}\"/>");
            Assert.Equal(XamlNavigationKind.Type, target.Kind);
            Assert.Equal("CompletionEngineTests.Models.MyButton", target.TypeName);
        }

        [Fact]
        public void StyleInclude_Source_Is_Uri()
        {
            var target = Classify("<UserControl.Styles><StyleInclude Source=\"/Styles/Th|eme.axaml\"/></UserControl.Styles>");
            Assert.Equal(XamlNavigationKind.Uri, target.Kind);
            Assert.Equal("/Styles/Theme.axaml", target.Uri);
        }

        [Fact]
        public void Plain_Values_And_Directives_Are_Not_Targets()
        {
            Assert.Null(Classify("<TextBlock Text=\"He|llo\"/>"));
            Assert.Null(Classify("<TextBlock x:Na|me=\"a\"/>"));
            Assert.Null(Classify("<Button Content=\"x\"/>|"));
        }
    }

    public class BindingDataTypeAnalyzerTests
    {
        private const string Root = "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:vm=\"using:App\"";

        [Fact]
        public void Flags_Binding_Without_DataType()
        {
            var text = Root + "><TextBlock Text=\"{Binding Name}\"/></UserControl>";
            var result = BindingDataTypeAnalyzer.Analyze(text);

            var binding = Assert.Single(result);
            Assert.Equal("{Binding Name}", text.Substring(binding.Start, binding.Length));
            Assert.Equal("UserControl", binding.SuggestedDataTypeOwner.Name);
        }

        [Theory]
        [InlineData(" x:DataType=\"vm:Main\"><TextBlock Text=\"{Binding Name}\"/>")]
        [InlineData(" x:CompileBindings=\"False\"><TextBlock Text=\"{Binding Name}\"/>")]
        [InlineData("><TextBlock Text=\"{Binding Text, ElementName=Other}\"/>")]
        [InlineData("><TextBlock Text=\"{Binding #Other.Text}\"/>")]
        [InlineData("><TextBlock Text=\"{Binding $parent[Window].Title}\"/>")]
        [InlineData("><TextBlock Text=\"{ReflectionBinding Name}\"/>")]
        [InlineData("><TextBlock Text=\"{Binding Name, DataType=vm:Main}\"/>")]
        [InlineData("><ItemsControl><ItemsControl.ItemTemplate><DataTemplate DataType=\"vm:Item\"><TextBlock Text=\"{Binding Name}\"/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>")]
        public void Does_Not_Flag(string body)
        {
            Assert.Empty(BindingDataTypeAnalyzer.Analyze(Root + body + "</UserControl>"));
        }

        [Fact]
        public void Suggests_Enclosing_DataTemplate()
        {
            var text = Root + " x:DataType=\"vm:Main\"><ItemsControl><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text=\"{Binding Name}\"/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl></UserControl>";
            var binding = Assert.Single(BindingDataTypeAnalyzer.Analyze(text.Replace(" x:DataType=\"vm:Main\"", "")));
            Assert.Equal("DataTemplate", binding.SuggestedDataTypeOwner.Name);
        }

        [Fact]
        public void CompiledBinding_Ignores_CompileBindings_False()
        {
            var text = Root + " x:CompileBindings=\"False\"><TextBlock Text=\"{CompiledBinding Name}\"/></UserControl>";
            Assert.Single(BindingDataTypeAnalyzer.Analyze(text));
        }
    }
}
