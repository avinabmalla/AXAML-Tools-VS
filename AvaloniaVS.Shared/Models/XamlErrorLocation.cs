using System;

namespace AvaloniaVS.Models
{
    /// <summary>
    /// A 1-based line/column position in a XAML document.
    /// </summary>
    public sealed class XamlErrorLocation : EventArgs
    {
        public XamlErrorLocation(int line, int column)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }

        public int Column { get; }
    }
}
