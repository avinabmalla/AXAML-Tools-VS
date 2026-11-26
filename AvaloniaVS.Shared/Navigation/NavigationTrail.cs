using System;
using System.Collections.Generic;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// The jumps this extension made (from where, to which file), so Back works in the Avalonia XAML
    /// editor. VS's own navigation history can't track our designer pane: it isn't a standard code
    /// window, so the history neither knows where you are in it nor how to return from it.
    /// </summary>
    internal static class NavigationTrail
    {
        private const int MaxEntries = 50;

        private static readonly List<Entry> s_entries = new List<Entry>();

        internal sealed class Entry
        {
            public string FromPath { get; set; }

            public int FromOffset { get; set; }

            public string ToPath { get; set; }
        }

        public static void Record(string fromPath, int fromOffset, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath))
            {
                return;
            }

            s_entries.Add(new Entry { FromPath = fromPath, FromOffset = fromOffset, ToPath = toPath });
            if (s_entries.Count > MaxEntries)
            {
                s_entries.RemoveAt(0);
            }
        }

        /// <summary>Whether <see cref="TakeBackFrom"/> would return an entry.</summary>
        public static bool CanGoBackFrom(string currentPath) =>
            s_entries.Count > 0 &&
            string.Equals(s_entries[s_entries.Count - 1].ToPath, currentPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Takes the most recent jump that landed in <paramref name="currentPath"/>, if the last jump
        /// did; returns null otherwise (so Back isn't hijacked after unrelated navigation).
        /// </summary>
        public static Entry TakeBackFrom(string currentPath)
        {
            if (s_entries.Count == 0)
            {
                return null;
            }

            var last = s_entries[s_entries.Count - 1];
            if (!string.Equals(last.ToPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            s_entries.RemoveAt(s_entries.Count - 1);
            return last;
        }
    }
}
