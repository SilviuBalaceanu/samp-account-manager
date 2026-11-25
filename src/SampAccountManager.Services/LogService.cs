using System.Text;

namespace SampAccountManager.Services;

/// <summary>Loguri pe fisiere (un fisier pe sesiune si zi). Nu primeste niciodata parole.</summary>
public sealed class LogService
{
    private readonly SettingsService _settings;
    private readonly object _gate = new();

    public LogService(SettingsService settings) => _settings = settings;

    public void Append(string channel, string line)
    {
        try
        {
            var dir = _settings.LogFolder;
            Directory.CreateDirectory(dir);
            var safe = string.Concat(channel.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var file = Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd}.log");
            lock (_gate) File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // un esec de logare nu trebuie sa afecteze sesiunea
        }
    }
}
