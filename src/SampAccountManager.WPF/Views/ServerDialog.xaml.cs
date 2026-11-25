using System.Globalization;
using System.Windows;
using SampAccountManager.Models;

namespace SampAccountManager.WPF.Views;

public partial class ServerDialog : Window
{
    private readonly Func<string, string, int, string?> _validate;

    public string ServerName => NameBox.Text.Trim();
    public string Host => HostBox.Text.Trim();
    public int Port => int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : -1;
    public string Description => DescBox.Text.Trim();

    public ServerDialog(ServerEntry? existing, Func<string, string, int, string?> validate)
    {
        InitializeComponent();
        _validate = validate;
        Owner = Application.Current.MainWindow;
        if (existing == null) Heading.Text = "Server nou";
        else
        {
            Heading.Text = "Editeaza server";
            NameBox.Text = existing.Name; HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString(CultureInfo.InvariantCulture); DescBox.Text = existing.Description;
        }
        NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var err = _validate(ServerName, Host, Port);
        if (err != null)
        {
            ErrorText.Text = err;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
