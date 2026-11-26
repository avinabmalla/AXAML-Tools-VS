using System.ComponentModel.Composition;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Avalonia.Ide.CompletionEngine.Navigation;
using AvaloniaVS.Models;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace AvaloniaVS.Navigation
{
    [Export(typeof(IMouseProcessorProvider))]
    [Name("Avalonia XAML Ctrl+Click navigation")]
    [ContentType("xml")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class XamlCtrlClickMouseProcessorProvider : IMouseProcessorProvider
    {
        public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) =>
            wpfTextView.Properties.GetOrCreateSingletonProperty(() => new XamlCtrlClickMouseProcessor(wpfTextView));
    }

    /// <summary>
    /// Ctrl+Click on something navigable in Avalonia XAML goes to its definition, like in C#.
    /// </summary>
    internal sealed class XamlCtrlClickMouseProcessor : MouseProcessorBase
    {
        private readonly IWpfTextView _view;
        private XamlNavigationTarget _pending;
        private Point _downPosition;

        public XamlCtrlClickMouseProcessor(IWpfTextView view) => _view = view;

        public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _pending = null;

            if (Keyboard.Modifiers != ModifierKeys.Control || !IsAvaloniaXaml())
            {
                return;
            }

            var point = GetBufferPosition(e);
            if (point is null)
            {
                return;
            }

            _pending = XamlNavigationService.Classify(_view, point.Value);
            if (_pending != null)
            {
                // Swallow the click so VS doesn't start a word selection.
                _downPosition = e.GetPosition(_view.VisualElement);
                _view.Caret.MoveTo(point.Value);
                e.Handled = true;
            }
        }

        public override void PreprocessMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            var target = _pending;
            _pending = null;

            if (target is null)
            {
                return;
            }

            var up = e.GetPosition(_view.VisualElement);
            if ((up - _downPosition).Length > SystemParameters.MinimumHorizontalDragDistance * 2)
            {
                return; // A drag, not a click.
            }

            e.Handled = true;
            ThreadHelper.JoinableTaskFactory
                .RunAsync(() => XamlNavigationService.NavigateAsync(_view, target, implementation: false, CancellationToken.None))
                .FileAndForget("AvaloniaVS/XamlCtrlClick");
        }

        private bool IsAvaloniaXaml() =>
            _view.TextBuffer.Properties.ContainsProperty(typeof(XamlBufferMetadata));

        private SnapshotPoint? GetBufferPosition(MouseEventArgs e)
        {
            var position = e.GetPosition(_view.VisualElement);
            var line = _view.TextViewLines.GetTextViewLineContainingYCoordinate(position.Y + _view.ViewportTop);
            return line?.GetBufferPositionFromXCoordinate(position.X + _view.ViewportLeft);
        }
    }
}
