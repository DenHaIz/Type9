using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Type9.Core;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PhraseBook))]
internal sealed partial class JsonCtx : JsonSerializerContext;

internal static class Paths
{
    /// <summary>Папка данных; переменная TYPE9_DATA_DIR позволяет переопределить её (проверка вёрстки, портативный режим).</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("TYPE9_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Type9");

    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string Phrases => Path.Combine(DataDir, "phrases.json");
    public static string UserDict => Path.Combine(DataDir, "userdict.tsv");
    public static string DictDir => Path.Combine(DataDir, "dict");
    public static string Log => Path.Combine(DataDir, "log.txt");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Type9.exe");
}

internal static class Storage
{
    public static T Load<T>(string path, JsonTypeInfo<T> info, Func<T> fallback, out bool existed)
    {
        existed = File.Exists(path);
        if (!existed) return fallback();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), info) ?? fallback();
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось прочитать {path}, используется значение по умолчанию", ex);
            try { File.Copy(path, path + ".broken", true); } catch { /* ignore */ }
            return fallback();
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> info) =>
        WriteAtomic(path, JsonSerializer.Serialize(value, info));

    /// <summary>Запись через временный файл, чтобы сбой не оставил полупустой файл.</summary>
    public static void WriteAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось сохранить {path}", ex);
        }
    }
}

internal static class Log
{
    private static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Paths.DataDir);
                var file = new FileInfo(Paths.Log);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(file.FullName, file.FullName + ".old", true);
                File.AppendAllText(Paths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch { /* логирование не должно ронять программу */ }
    }
}
