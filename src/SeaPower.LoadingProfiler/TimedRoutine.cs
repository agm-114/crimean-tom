using System.Collections;
using System.Diagnostics;

namespace SeaPower.LoadingProfiler;

internal class TimedRoutine(string name, IEnumerator inner, Action<bool>? scope = null) : IEnumerator, IDisposable
{
    private Stopwatch? timer;
    private int startFrame;
    private bool ended;
    private bool disposed;
    public object Current => inner.Current;
    public bool MoveNext()
    {
        if (ended) return false;
        if (timer == null)
        {
            timer = Stopwatch.StartNew();
            startFrame = UnityEngine.Time.frameCount;
            scope?.Invoke(true);
            TraceLog.Write("begin", new { name });
        }
        try
        {
            bool more = inner.MoveNext();
            if (!more) Finish("end");
            return more;
        }
        catch { Finish("failed"); throw; }
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { (inner as IDisposable)?.Dispose(); }
        finally { Finish("disposed"); }
    }
    private void Finish(string kind)
    {
        if (ended) return;
        ended = true;
        // Frames are recorded because time spent yielding belongs to no call hook.
        TraceLog.Write(kind, new { name, seconds = timer?.Elapsed.TotalSeconds, frames = timer == null ? 0 : UnityEngine.Time.frameCount - startFrame });
        if (timer != null) scope?.Invoke(false);
    }
}
