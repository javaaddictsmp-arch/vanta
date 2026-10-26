using System.Windows;
using System.Windows.Controls;

namespace TuneUp.Controls;

/// <summary>
/// The "In plain English" banner at the top of pages and tabs, so people who are not PC
/// enthusiasts know what a section is for before they read the technical details.
/// Look is defined in Themes/Theme.xaml.
/// </summary>
public sealed class Explainer : Control
{
    static Explainer()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Explainer), new FrameworkPropertyMetadata(typeof(Explainer)));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Explainer), new PropertyMetadata("In plain English"));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Explainer), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
