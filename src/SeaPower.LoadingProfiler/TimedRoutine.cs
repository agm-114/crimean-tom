using System.Collections;
using System.Diagnostics;

namespace SeaPower.LoadingProfiler;

internal class TimedRoutine(string name, IEnumerator inner) : IEnumerator, IDisposable
{
    private Stopwatch? timer;
    private bool ended;
    public object Current => inner.Current;
    public bool MoveNext()
    {
        if (timer == null) { timer = Stopwatch.StartNew(); TraceLog.Write("begin", new { name }); }
        try
        {
            bool more = inner.MoveNext();
            if (!more) Finish("end");
            return more;
        }
        catch { Finish("failed"); throw; }
    }
    public void Reset() => inner.Reset();
    public void Dispose()
    {
        try { (inner as IDisposable)?.Dispose(); }
        finally { Finish("disposed"); }
    }
    private void Finish(string kind)
    {
        if (ended) return;
        ended = true;
        TraceLog.Write(kind, new { name, seconds = timer?.Elapsed.TotalSeconds });
    }
}
