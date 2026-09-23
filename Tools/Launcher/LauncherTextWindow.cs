using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace DealOrForce.Launcher;

/// <summary>The half of the launcher's languages that walks a window. See LauncherText.cs.</summary>
public static partial class L
{
    /// <summary>
    /// Translate every literal the window was built with. The XAML is written in English, which
    /// is the key; this swaps each literal for its translation, and can be run again after the
    /// language changes. Anything bound to data - a mod's name, its version - is not in the table
    /// and is left alone.
    /// </summary>
    public static void TranslateTree(DependencyObject node)
    {
        if (node is Window window) window.Title = Caption(window, "title", window.Title);
        // A bound value is data, and writing to it would cut the binding - only literals move.
        if (node is TextBlock block && !string.IsNullOrEmpty(block.Text) &&
            !BindingOperations.IsDataBound(block, TextBlock.TextProperty)) block.Text = Caption(block, "text", block.Text);
        if (node is ContentControl control && control.Content is string content &&
            !BindingOperations.IsDataBound(control, ContentControl.ContentProperty))
            control.Content = Caption(control, "content", content);
        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string tip) element.ToolTip = Caption(element, "tip", tip);
            var spoken = AutomationProperties.GetName(element);
            if (!string.IsNullOrEmpty(spoken) && !BindingOperations.IsDataBound(element, AutomationProperties.NameProperty))
                AutomationProperties.SetName(element, Caption(element, "name", spoken));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            TranslateTree(child);
    }

    /// <summary>
    /// The English a caption was written in and the translation last put on it, per element.
    ///
    /// Translating in place loses the English, and then a second language has nothing left to
    /// translate from - which is why the window could only ever be translated once, at start.
    /// And a caption the code has rewritten since - the status line, the play button while the
    /// game runs - is not the XAML's any more, so it is only touched while it still shows exactly
    /// what was last written onto it; the window puts the rest back itself.
    /// </summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, (string Source, string Applied)>> Written = new();

    private static string Caption(DependencyObject node, string slot, string current)
    {
        var captions = Written.GetOrCreateValue(node);
        if (!captions.TryGetValue(slot, out var caption)) caption = (current, current);
        else if (!string.Equals(current, caption.Applied, StringComparison.Ordinal)) return current;
        var translated = T(caption.Source);
        captions[slot] = (caption.Source, translated);
        return translated;
    }
}

/// <summary>
/// For the few strings that live inside a template, where the window's tree does not reach at
/// load time because a template is only built per row: {local:T 'English text'}.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
