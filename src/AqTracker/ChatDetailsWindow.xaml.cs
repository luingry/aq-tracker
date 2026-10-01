using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AqTracker;

public partial class ChatDetailsWindow : Window
{
    public ChatDetailsWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        for (DependencyObject? current = eventArgs.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Button) return;
        if (eventArgs.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void ToggleProject(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is System.Windows.Controls.Button { DataContext: ChatDetailsProjectRow project }) project.Toggle();
    }

    private void ClearChatSearch(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ChatSearch = "";
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
