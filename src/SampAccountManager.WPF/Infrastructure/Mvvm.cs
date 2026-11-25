using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SampAccountManager.Models;

namespace SampAccountManager.WPF.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute; _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = _ => execute();
        if (canExecute != null) _canExecute = _ => canExecute();
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public static class UiThread
{
    public static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}

public sealed class StateBrushConverter : IValueConverter
{
    private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static readonly Brush Good = B("#3DD68C"), Warn = B("#F5B94A"), Bad = B("#FF6B6B"), Muted = B("#8A93A6"), Info = B("#6C8CFF");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SessionState s
            ? s switch
            {
                SessionState.Online => Good,
                SessionState.SpawnConfirmed => Good,
                SessionState.Error => Bad,
                SessionState.Connecting or SessionState.Connected or SessionState.Authenticating
                    or SessionState.WaitingForSpawn => Info,
                SessionState.Disconnecting => Warn,
                _ => Muted,
            }
            : Muted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value is ServerStatus s
            ? s switch { ServerStatus.Online => "#3DD68C", ServerStatus.Offline => "#FF6B6B", _ => "#8A93A6" }
            : "#8A93A6";
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
