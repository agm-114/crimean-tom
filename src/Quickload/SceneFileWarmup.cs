using BackgroundTask = System.Threading.Tasks.Task;

namespace Quickload;

internal class SceneFileWarmup
{
    private bool started;
    private volatile bool stopped;

    internal void Begin(string dataPath)
    {
        if (started || stopped) return;
        started = true;
        // Only file I/O runs off-thread. No Unity scene operations or retained file data.
        _ = BackgroundTask.Run(() => ReadFiles(dataPath));
    }

    internal void Stop() => stopped = true;

    private async BackgroundTask ReadFiles(string dataPath)
    {
        byte[] buffer = new byte[64 * 1024];
        long remaining = 16 * 1024 * 1024;
        foreach (string name in new[] { "level2", "sharedassets2.assets", "sharedassets2.assets.resS" })
        {
            if (stopped || remaining == 0) return;
            try
            {
                using var file = new FileStream(Path.Combine(dataPath, name), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous);
                while (!stopped && remaining > 0)
                {
                    int count = await file.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                    if (count == 0) break;
                    remaining -= count;
                    await BackgroundTask.Delay(1).ConfigureAwait(false);
                }
            }
            // Warming is optional. Missing or inaccessible files remain the native loader's responsibility.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
