namespace SimpleTranslatorCS;

// Immutable published views survive SPT 4.0 uncached reads and another mod invalidating the 4.1 cache.
internal static class ServerLocaleOverlay
{
    private sealed record View(IReadOnlyDictionary<string, string> Values, string[] Removed);
    private static volatile View _view = new(new Dictionary<string, string>(), []);
    internal static void Publish(IReadOnlyDictionary<string, string> values, string[] removed) => _view = new(values, removed);
    internal static void Apply(IDictionary<string, string> target)
    {
        var view = _view;
        foreach (var pair in view.Values) target[pair.Key] = pair.Value;
        foreach (var key in view.Removed) target.Remove(key);
    }
}
