using System.Windows;
using DfsrAnalyser.App.ViewModels;

namespace DfsrAnalyser.App;

public partial class ImportWindow : Window
{
    public ImportWindow(ImportViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => vm.RescanAsync().Forget();
    }

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;
}
