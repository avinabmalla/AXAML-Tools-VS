using System;
using CompletionMetadata = Avalonia.Ide.CompletionEngine.Metadata;

namespace AvaloniaVS.Models
{
    internal class XamlBufferMetadata
    {
        private bool? _compiledBindingsByDefault;

        public CompletionMetadata CompletionMetadata { get; set; }

        public bool NeedInvalidation { get; set; } = true;

        /// <summary>
        /// The project's <c>AvaloniaUseCompiledBindingsByDefault</c> (true by default in Avalonia 12),
        /// or null until it has been read.
        /// </summary>
        public bool? CompiledBindingsByDefault
        {
            get => _compiledBindingsByDefault;
            set
            {
                if (_compiledBindingsByDefault != value)
                {
                    _compiledBindingsByDefault = value;
                    CompiledBindingsByDefaultChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler CompiledBindingsByDefaultChanged;
    }
}
