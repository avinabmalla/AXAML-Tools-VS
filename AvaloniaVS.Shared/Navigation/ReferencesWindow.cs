using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// Shows a list of locations in Visual Studio's Find All References window: used for
    /// Shift+F12 and for Ctrl+F12 when there is more than one implementation.
    /// </summary>
    internal static class ReferencesWindow
    {
        private const string SourceTypeIdentifier = "AxamlTools.References";

        public static async Task ShowAsync(string title, IReadOnlyList<XamlSourceLocation> locations)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsFindAllReferences)) is not IFindAllReferencesService service)
            {
                await NavigationHelper.SetStatusAsync("The Find All References window isn't available.");
                return;
            }

            var window = service.StartSearch(title);
            var source = new Source(locations);
            window.Manager.AddSource(source);

            // Navigate ourselves on double-click / Enter: our entries are plain file locations and
            // don't depend on Roslyn's navigation support for the window.
            var control = window.TableControl;
            control.Control.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = NavigateToSelection(control);
                }
            };
            control.Control.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = NavigateToSelection(control);
                }
            };
        }

        private static bool NavigateToSelection(IWpfTableControl control)
        {
            var entry = control.SelectedEntries.FirstOrDefault();
            if (entry is null ||
                !entry.TryGetValue(StandardTableKeyNames.DocumentName, out string path) ||
                !entry.TryGetValue(StandardTableKeyNames.Line, out int line) ||
                !entry.TryGetValue(StandardTableKeyNames.Column, out int column) ||
                !File.Exists(path))
            {
                return false;
            }

            AvaloniaVS.Services.BackgroundWork.Start(async () =>
            {
                // Offsets from the file on disk: line/column are what the table shows, so convert
                // those rather than trusting a stored offset into a possibly-edited buffer.
                var text = File.ReadAllText(path);
                var offset = 0;
                for (var i = 0; i < line && offset >= 0; i++)
                {
                    offset = text.IndexOf('\n', offset) + 1;
                    if (offset == 0)
                    {
                        break;
                    }
                }

                await NavigationHelper.OpenAtAsync(path, Math.Min(text.Length, offset + column));
            }, "AvaloniaVS/ReferencesNavigate");

            return true;
        }

        private sealed class Source : ITableDataSource
        {
            private readonly IReadOnlyList<XamlSourceLocation> _locations;

            public Source(IReadOnlyList<XamlSourceLocation> locations) => _locations = locations;

            public string SourceTypeIdentifier => ReferencesWindow.SourceTypeIdentifier;

            public string Identifier => ReferencesWindow.SourceTypeIdentifier + "." + Guid.NewGuid().ToString("N");

            public string DisplayName => "AXAML Tools";

            public IDisposable Subscribe(ITableDataSink sink)
            {
                sink.AddEntries(_locations.Select(l => (ITableEntry)new Entry(l)).ToList(), removeAllEntries: true);
                sink.IsStable = true;
                return null;
            }
        }

        private sealed class Entry : TableEntryBase
        {
            private readonly XamlSourceLocation _location;

            public Entry(XamlSourceLocation location) => _location = location;

            public override object Identity => _location;

            public override bool TryGetValue(string keyName, out object content)
            {
                switch (keyName)
                {
                    case StandardTableKeyNames.DocumentName:
                        content = _location.Path;
                        return true;
                    case StandardTableKeyNames.Line:
                        content = _location.Line;
                        return true;
                    case StandardTableKeyNames.Column:
                        content = _location.Column;
                        return true;
                    case StandardTableKeyNames.Text:
                    case StandardTableKeyNames.FullText:
                        content = _location.IsDefinition ? "(definition) " + _location.LineText : _location.LineText;
                        return true;
                    case StandardTableKeyNames.ProjectName:
                        content = _location.ProjectName ?? "";
                        return _location.ProjectName != null;
                    default:
                        content = null;
                        return false;
                }
            }
        }
    }
}
