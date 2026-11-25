using System.Text.Json;

namespace SampAccountManager.Services;

/// <summary>Salvare JSON atomica (fisier temporar + inlocuire) ca sa nu se piarda datele la oprire brusca.</summary>
public static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch
        {
            // fisier corupt: il pastram ca .corrupt si pornim curat
            try { File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        lock (Gate)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
