using System.Windows;
using SampAccountManager.Models;

namespace SampAccountManager.WPF.Views;

public partial class AccountDialog : Window
{
    private readonly Func<string, string, Guid?, string?> _validate;
    private readonly Guid? _id;

    public string Nickname => NickBox.Text.Trim();
    /// <summary>La editare, gol = parola ramane neschimbata.</summary>
    public string Password => PassBox.Password;
    public string Notes => NotesBox.Text.Trim();

    public AccountDialog(Account? existing, Func<string, string, Guid?, string?> validate)
    {
        InitializeComponent();
        _validate = validate;
        _id = existing?.Id;
        Owner = Application.Current.MainWindow;
        if (existing == null)
        {
            Heading.Text = "Cont nou";
        }
        else
        {
            Heading.Text = "Editeaza cont";
            NickBox.Text = existing.Nickname;
            NotesBox.Text = existing.Notes;
            PassLabel.Text = "Parola noua (lasa gol ca sa pastrezi parola actuala)";
        }
        NickBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var err = _validate(Nickname, Password, _id);
        if (err != null)
        {
            ErrorText.Text = err;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
