namespace Layal.Tests;

internal sealed class TestTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Layal.Tests", Guid.NewGuid().ToString("N"));

    public TestTempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the test's primary result should remain visible.
        }
    }
}
