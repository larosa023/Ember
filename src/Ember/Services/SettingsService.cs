using System.Text.Json;
using Ember.Models;

namespace Ember.Services;

public sealed class SettingsService
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ember");

    public static readonly string ThemesDir = Path.Combine(Dir, "themes");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public AppSettings Current { get; private set; } = new();

    public static SettingsService Load()
    {
        var service = new SettingsService();
        try
        {
            if (File.Exists(FilePath))
                service.Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt file: fall back to defaults.
        }
        return service;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch
        {
            // Not fatal; settings just won't persist this time.
        }
    }
}
