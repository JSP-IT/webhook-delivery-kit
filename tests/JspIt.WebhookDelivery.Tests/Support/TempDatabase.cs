using Microsoft.Data.Sqlite;

namespace JspIt.WebhookDelivery.Tests.Support;

/// <summary>A throwaway SQLite database file.</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wdk-tests-" + Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "deliveries.db");
    }

    public string Path { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort on platforms that hold file locks briefly.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
