namespace SimpleTranslatorCS;

/// <summary>
/// Classifies global-locale keys only when a loaded SPT database entity proves their domain.
/// Unknown keys stay out of automatic translation instead of being guessed from their shape.
/// </summary>
internal sealed class LocaleDomainIndex
{
    private static readonly HashSet<string> ItemSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "Name", "ShortName", "Description" };
    private static readonly HashSet<string> TraderSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "FullName", "FirstName", "Nickname", "Location", "Description" };
    private static readonly HashSet<string> CustomizationSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "Name", "ShortName", "Description" };

    private readonly HashSet<string> _itemIds;
    private readonly HashSet<string> _questKeys;
    private readonly HashSet<string> _achievementKeys;
    private readonly HashSet<string> _traderIds;
    private readonly HashSet<string> _customizationIds;
    private readonly HashSet<string> _handbookKeys;
    private readonly HashSet<string> _otherLocaleKeys;

    public LocaleDomainIndex(
        IEnumerable<string>? itemIds = null,
        IEnumerable<string>? questKeys = null,
        IEnumerable<string>? achievementKeys = null,
        IEnumerable<string>? traderIds = null,
        IEnumerable<string>? customizationIds = null,
        IEnumerable<string>? handbookKeys = null,
        IEnumerable<string>? otherLocaleKeys = null)
    {
        _itemIds = Set(itemIds);
        _questKeys = Set(questKeys);
        _achievementKeys = Set(achievementKeys);
        _traderIds = Set(traderIds);
        _customizationIds = Set(customizationIds);
        _handbookKeys = Set(handbookKeys);
        _otherLocaleKeys = Set(otherLocaleKeys);
    }

    public IEnumerable<string> CandidateKeys => _itemIds
        .Concat(_questKeys)
        .Concat(_achievementKeys)
        .Concat(_traderIds)
        .Concat(_customizationIds)
        .Concat(_handbookKeys);

    public string? Classify(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var split = key.IndexOf(' ');
        var id = split > 0 ? key[..split] : key;
        var suffix = split > 0 ? key[(split + 1)..] : string.Empty;

        if (_itemIds.Contains(id) && ItemSuffixes.Contains(suffix))
            return "item";
        // Quest models provide complete locale keys plus bare quest/condition IDs. Exact matching
        // avoids admitting an unrelated technical suffix that merely shares a quest ID prefix.
        if (_questKeys.Contains(key))
            return "quest";
        // Achievement keys are deliberately exact. This prevents unrelated keys that merely
        // begin with an achievement ID from being admitted to automatic translation.
        if (_achievementKeys.Contains(key))
            return "achievement";
        if (_traderIds.Contains(id) && TraderSuffixes.Contains(suffix))
            return "trader";
        if (_customizationIds.Contains(id) && CustomizationSuffixes.Contains(suffix))
            return "customization";
        if (_handbookKeys.Contains(key))
            return "handbook";
        if (_otherLocaleKeys.Contains(key)) return "locale";
        return null;
    }

    private static HashSet<string> Set(IEnumerable<string>? values) => new(
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)),
        StringComparer.OrdinalIgnoreCase);
}
