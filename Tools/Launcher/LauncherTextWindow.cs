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
    /// is the key; this runs once after InitializeComponent and swaps each one for its Russian.
    /// Anything bound to data - a mod's name, its version - is not in the table and is left alone.
    /// </summary>
    public static void TranslateTree(DependencyObject node)
    {
        if (node is Window window) window.Title = T(window.Title);
        // A bound value is data, and writing to it would cut the binding - only literals move.
        if (node is TextBlock block && !string.IsNullOrEmpty(block.Text) &&
            !BindingOperations.IsDataBound(block, TextBlock.TextProperty)) block.Text = T(block.Text);
        if (node is ContentControl control && control.Content is string content &&
            !BindingOperations.IsDataBound(control, ContentControl.ContentProperty)) control.Content = T(content);
        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string tip) element.ToolTip = T(tip);
            var spoken = AutomationProperties.GetName(element);
            if (!string.IsNullOrEmpty(spoken) && !BindingOperations.IsDataBound(element, AutomationProperties.NameProperty))
                AutomationProperties.SetName(element, T(spoken));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            TranslateTree(child);
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
