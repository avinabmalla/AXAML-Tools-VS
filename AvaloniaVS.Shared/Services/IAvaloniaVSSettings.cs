using System.ComponentModel;
using System.Windows.Controls;
using AvaloniaVS.Views;
using Serilog.Events;

namespace AvaloniaVS.Services
{
    public interface IAvaloniaVSSettings : INotifyPropertyChanged
    {
        Orientation DesignerSplitOrientation { get; set; }
        bool DesignerSplitSwapped { get; set; }
        AvaloniaDesignerView DesignerView { get; set; }
        LogEventLevel MinimumLogVerbosity { get; set; }
        string ZoomLevel { get; set; }

        /// <summary>Pattern for generated event handler names, e.g. <c>{Name}_{Event}</c>.</summary>
        string EventHandlerNamingPattern { get; set; }

        /// <summary>Open a newly generated event handler in the C# editor.</summary>
        bool NavigateToGeneratedEventHandler { get; set; }
        void Save();
        void Load();
    }
}
