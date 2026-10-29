using System.IO;
using System.Text.Json;
using Stockhog.Presentation.Settings;

namespace Stockhog.App.Hosting;

/// <summary>
/// User preferences in <c>%LOCALAPPDATA%\Stockhog\settings.json</c>. A missing or unreadable file yields defaults.
/// </summary>
internal sealed class JsonUserSettingsStore(string path) : IUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockhog", "settings.json");

    public UserSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
