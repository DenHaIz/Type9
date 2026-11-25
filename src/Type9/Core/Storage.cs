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

    public static void Save<T>(string path, T value, JsonTypeInfo<T> info)
    {
        var json = JsonSerializer.Serialize(value, info);
        lock (WriteSync)
        {
            Pending.Remove(path); // более свежее содержимое — отложенная фоновая запись больше не нужна
            WriteAtomic(path, json);
        }
    }

    /// <summary>
    /// Сериализация сейчас (в вызывающем потоке), запись на диск — в фоне: сброс на диск и замена файла не задерживают UI
    /// (папка %AppData% на сервере терминалов бывает сетевой). Из нескольких отложенных записей файла пишется последняя.
    /// </summary>
    public static void SaveInBackground<T>(string path, T value, JsonTypeInfo<T> info)
    {
        var json = JsonSerializer.Serialize(value, info);
        lock (WriteSync) Pending[path] = json;
        Task.Run(() =>
        {
            lock (WriteSync)
            {
                if (Pending.Remove(path, out var latest)) WriteAtomic(path, latest);
            }
        });
    }

    /// <summary>Дописать отложенные фоновые записи (при выходе).</summary>
    public static void Flush()
    {
        lock (WriteSync)
        {
            foreach (var (path, json) in Pending.ToList()) WriteAtomic(path, json);
            Pending.Clear();
        }
    }

    private static readonly object WriteSync = new();
    private static readonly Dictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Запись через временный файл с уникальным именем и сбросом на диск, затем атомарная замена.
    /// Записи сериализуются, чтобы фоновое и немедленное сохранение не мешали друг другу. false — не удалось.
    /// </summary>
    public static bool WriteAtomic(string path, string content)
    {
        lock (WriteSync)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(content);
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось сохранить {path}", ex);
                try { File.Delete(tmp); } catch { /* ignore */ }
                return false;
            }
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
