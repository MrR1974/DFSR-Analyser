using System.Windows;
using System.Windows.Controls;
using DfsrAnalyser.App.ViewModels;

namespace DfsrAnalyser.App;

public partial class CollectWindow : Window
{
    public CollectWindow(CollectViewModel vm)
    {
        InitializeComponent();
        vm.PasswordProvider = () => Password.Password;
        DataContext = vm;
        Closing += (_, e) =>
        {
            if (vm.IsBusy)
            {
                vm.CancelCommand.Execute(null);
                e.Cancel = true;
            }
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnLogChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
