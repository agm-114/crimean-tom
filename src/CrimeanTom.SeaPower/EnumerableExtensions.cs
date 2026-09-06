namespace CrimeanTom.SeaPower;

internal static class EnumerableExtensions
{
    // Runs immediately, once per item, in enumeration order.
    internal static void ForEach<T>(this IEnumerable<T> source, Action<T> action)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (action is null) throw new ArgumentNullException(nameof(action));
        foreach (T item in source) action(item);
    }

    internal static void ForEach<T>(this IEnumerable<T> source, Action<T, int> action)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (action is null) throw new ArgumentNullException(nameof(action));
        int index = 0;
        foreach (T item in source) action(item, index++);
    }
}