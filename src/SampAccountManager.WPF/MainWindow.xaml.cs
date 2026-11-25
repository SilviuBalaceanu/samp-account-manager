using System.ComponentModel;
using System.Windows;
using SampAccountManager.WPF.ViewModels;

namespace SampAccountManager.WPF;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        var services = new AppServices();
        _vm = new ShellViewModel(services);
        DataContext = _vm;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        if (_vm.HasDirtyData)
        {
            var r = MessageBox.Show("Ai modificari nesalvate (salvarea automata e oprita). Le salvezi acum?", "Inchidere",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes)
            {
                _vm.Services.Settings.Save(); _vm.Services.Accounts.Save(); _vm.Services.Servers.Save();
            }
        }
        // oprim curat toate sesiunile: niciun proces headless nu trebuie sa ramana in fundal
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        try { await _vm.Services.Sessions.DisconnectAllAsync(); } catch { }
        _vm.Services.Sessions.Dispose();
        Close();
    }
}
