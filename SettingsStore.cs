using System.Text.Json;

namespace ULKHelper;

// Persist outside the plugin binaries; preserve unknown keys for future updates.
internal sealed class SettingsStore
{
    private readonly string path;
    private readonly Action<Exception>? error;
    private Dictionary<string, JsonElement> values = new();
    private bool dirty;
    private bool readOnly;
    private DateTimeOffset nextSave;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };
    public SettingsStore(string path, Action<Exception>? error = null)
    {
        this.path = path; this.error = error;
        try { if (File.Exists(path)) values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path), Options) ?? new(); }
        catch (Exception ex)
        {
            error?.Invoke(ex);
            try { if (File.Exists(path)) File.Copy(path, path + ".invalid-" + DateTime.UtcNow.Ticks, false); }
            catch (Exception backupError) { readOnly = true; error?.Invoke(backupError); }
        }
    }
    public T Get<T>(string key, T fallback)
    {
        try { return values.TryGetValue(key, out var value) ? value.Deserialize<T>(Options) ?? fallback : fallback; }
        catch { return fallback; }
    }
    public void Put<T>(string key, T value)
    {
        var json = JsonSerializer.SerializeToElement(value, Options);
        if (values.TryGetValue(key, out var old) && old.GetRawText() == json.GetRawText()) return;
        values[key] = json; dirty = true;
    }
    public void Flush(bool force = false)
    {
        if (readOnly || !dirty || !force && DateTimeOffset.UtcNow < nextSave) return;
        nextSave = DateTimeOffset.UtcNow.AddSeconds(1);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(values, Options));
            File.Move(temp, path, true); dirty = false;
        }
        catch (Exception ex) { error?.Invoke(ex); }
    }
}
