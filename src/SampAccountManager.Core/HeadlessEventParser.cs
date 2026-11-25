using System.Text.Json;
using SampAccountManager.Models;

namespace SampAccountManager.Core;

public static class HeadlessEventParser
{
    /// <summary>Parseaza o linie JSON Lines produsa de clientul Rust. Liniile invalide sunt respinse.</summary>
    public static bool TryParse(string? line, out HeadlessEvent? ev)
    {
        ev = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("event", out var name) || name.ValueKind != JsonValueKind.String) return false;
            var e = new HeadlessEvent
            {
                Event = name.GetString() ?? "",
                SessionId = root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() ?? "" : "",
                Ts = root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
            };
            if (root.TryGetProperty("data", out var data)) e.Data = data.Clone();
            ev = e;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
