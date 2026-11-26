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
    [Name("AXAML Ctrl+Click navigation")]
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
        private Point? _lastMouse;
        private Cursor _savedCursor;
        private bool _showingLink;

        public XamlCtrlClickMouseProcessor(IWpfTextView view)
        {
            _view = view;

            // Pressing or releasing Ctrl without moving the mouse should show/hide the link too.
            _view.VisualElement.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.LeftCtrl or Key.RightCtrl)
                {
                    UpdateLink();
                }
            };
            _view.VisualElement.PreviewKeyUp += (_, e) =>
            {
                if (e.Key is Key.LeftCtrl or Key.RightCtrl)
                {
                    ClearLink();
                }
            };
            _view.LostAggregateFocus += (_, _) => ClearLink();
        }

        public override void PreprocessMouseMove(MouseEventArgs e)
        {
            _lastMouse = e.GetPosition(_view.VisualElement);
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                UpdateLink();
            }
            else if (_showingLink)
            {
                ClearLink();
            }
        }

        public override void PreprocessMouseLeave(MouseEventArgs e)
        {
            _lastMouse = null;
            ClearLink();
        }

        /// <summary>Underlines the navigable target under the mouse and shows a hand cursor.</summary>
        private void UpdateLink()
        {
            if (_lastMouse is not { } mouse || Keyboard.Modifiers != ModifierKeys.Control || !IsAvaloniaXaml())
            {
                ClearLink();
                return;
            }

            var point = GetBufferPosition(mouse);
            var tagger = GetLinkTagger();
            if (point is null || tagger is null)
            {
                ClearLink();
                return;
            }

            // Still over the same link: nothing to do (avoids re-classifying on every move).
            if (tagger.Span is { } current && current.Snapshot == point.Value.Snapshot && current.Contains(point.Value))
            {
                return;
            }

            var target = XamlNavigationService.Classify(_view, point.Value);
            if (target is null || target.Length <= 0 || target.Start + target.Length > point.Value.Snapshot.Length)
            {
                ClearLink();
                return;
            }

            tagger.SetSpan(new SnapshotSpan(point.Value.Snapshot, target.Start, target.Length));
            if (!_showingLink)
            {
                _savedCursor = _view.VisualElement.Cursor;
                _view.VisualElement.Cursor = Cursors.Hand;
                _showingLink = true;
            }
        }

        private void ClearLink()
        {
            GetLinkTagger()?.SetSpan(null);
            if (_showingLink)
            {
                _view.VisualElement.Cursor = _savedCursor;
                _showingLink = false;
            }
        }

        private CtrlClickLinkTagger GetLinkTagger() =>
            _view.Properties.TryGetProperty(typeof(CtrlClickLinkTagger), out CtrlClickLinkTagger tagger) ? tagger : null;

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
            ClearLink();
            AvaloniaVS.Services.BackgroundWork.Start(
                () => XamlNavigationService.NavigateAsync(_view, target, implementation: false, CancellationToken.None),
                "AvaloniaVS/XamlCtrlClick");
        }

        private bool IsAvaloniaXaml() =>
            _view.TextBuffer.Properties.ContainsProperty(typeof(XamlBufferMetadata));

        private SnapshotPoint? GetBufferPosition(MouseEventArgs e) => GetBufferPosition(e.GetPosition(_view.VisualElement));

        private SnapshotPoint? GetBufferPosition(Point position)
        {
            var line = _view.TextViewLines.GetTextViewLineContainingYCoordinate(position.Y + _view.ViewportTop);
            return line?.GetBufferPositionFromXCoordinate(position.X + _view.ViewportLeft);
        }
    }
}
