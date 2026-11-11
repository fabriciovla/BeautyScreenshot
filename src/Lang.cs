using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BeautyScreenshot
{
    /// <summary>
    /// The app speaks Windows' display language: Spanish when Windows is in Spanish, English otherwise. Each text is
    /// written in place with both versions, T("…", "…"), so the two never drift apart.
    /// </summary>
    static class L
    {
        public static readonly bool Es = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

        public static string T(string es, string en) => Es ? es : en;

        /// <summary>
        /// Translates the fixed texts of a XAML tree (written in English) with an English → Spanish table. Texts not
        /// in the table stay as they are, so a missing entry shows up in English rather than as nothing.
        /// </summary>
        public static void Translate(DependencyObject node, IDictionary<string, string> spanish)
        {
            if (!Es || node == null) return;
            switch (node)
            {
                case TextBlock t when t.Text != null && spanish.TryGetValue(t.Text, out var text):
                    t.Text = text;
                    break;
                case ContentControl c when c.Content is string s && spanish.TryGetValue(s, out var content):
                    c.Content = content;
                    break;
            }
            if (node is FrameworkElement fe && fe.ToolTip is string tip && spanish.TryGetValue(tip, out var tooltip))
                fe.ToolTip = tooltip;
            foreach (var child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) Translate(d, spanish);
        }
    }
}
