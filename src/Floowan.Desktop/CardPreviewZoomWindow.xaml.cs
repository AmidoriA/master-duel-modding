using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Floowan.Desktop;

public partial class CardPreviewZoomWindow : Window
{
    public CardPreviewZoomWindow(ImageSource source)
    {
        InitializeComponent();
        ZoomImage.Source = source;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks on the close button (it handles its own Click).
        if (e.OriginalSource is DependencyObject d && IsDescendantOf(d, CloseButton))
            return;
        Close();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }
}
