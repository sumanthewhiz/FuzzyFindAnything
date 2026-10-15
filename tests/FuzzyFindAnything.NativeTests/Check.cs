using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FuzzyFindAnything.NativeTests;

internal static class Check
{
    public static bool RequireUsn { get; set; }
    public static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("No native test host.");
    public static async Task TemporaryAsync(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "ffa-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { Directory.Delete(root, true); }
    }
    public static async Task UntilAsync(Func<bool> condition, string message, int milliseconds = 15000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= milliseconds) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static SqliteConnection Open(string directory, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "catalog.sqlite3"),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
    public static long Scalar(string directory, string sql)
    {
        using var database = Open(directory, true);
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    public static void Execute(string directory, string sql)
    {
        using var database = Open(directory);
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    public static string Scenario(string root, object value)
    {
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "scenario.json"), JsonSerializer.Serialize(value));
        return directory;
    }
}
