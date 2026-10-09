using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace nAlbum.Localization;

public sealed class LanguageInfo
{
    public string Code { get; init; }
    public string Name { get; init; }
    public string Flag { get; init; }
    public IReadOnlyDictionary<string, string> Translations { get; init; }

    public string Label => string.IsNullOrEmpty(Flag) ? Name : $"{Flag} {Name}";
}

/// <summary>
/// Loads every Locales/*.json file at startup. A file name is the language code (ru.json -> "ru").
/// Keys are the PascalCase constants of <see cref="LocKey"/>. A missing or empty translation falls back to
/// English (en.json), and a key missing everywhere is returned as is.
/// </summary>
public sealed partial class LocalizationService
{
    public const string DefaultCode = "en";

    private readonly Dictionary<string, LanguageInfo> _languages;

    private LocalizationService(Dictionary<string, LanguageInfo> languages) { _languages = languages; }

    public IReadOnlyCollection<LanguageInfo> Languages =>
        _languages.Values.OrderBy(l => l.Code != DefaultCode).ThenBy(l => l.Code, StringComparer.Ordinal).ToList();

    public bool Has(string code) => code != null && _languages.ContainsKey(code);

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})*$")]
    private static partial Regex CodePattern();

    public static LocalizationService Load(string directory)
    {
        var languages = new Dictionary<string, LanguageInfo>();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (!CodePattern().IsMatch(code))
                {
                   Console.Error.WriteLine($"Locale '{path}' skipped: file name must be a language code such as 'de' or 'pt-br'.");
                    continue;
                }

                try
                {
                    var file = JsonSerializer.Deserialize<LocaleFile>(File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    languages[code] = new LanguageInfo
                    {
                        Code = code,
                        Name = string.IsNullOrWhiteSpace(file?.Name) ? code : file.Name,
                        Flag = file?.Flag,
                        Translations = file?.Translations ?? new Dictionary<string, string>()
                    };
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Locale '{path}' skipped: {ex.Message}");
                }
            }
        }
        else
        {
            Console.Error.WriteLine($"Locales directory '{directory}' not found; only English will be available.");
        }

        if (!languages.ContainsKey(DefaultCode))
        {
            languages[DefaultCode] = new LanguageInfo
            {
                Code = DefaultCode, Name = "English", Flag = "🇬🇧", Translations = new Dictionary<string, string>()
            };
        }

        Validate(languages);
        Console.WriteLine($"Languages loaded: {string.Join(", ", languages.Keys.Order())}");
        return new LocalizationService(languages);
    }

    /// <summary>Saved choice if that language still exists, otherwise detected from the Telegram language code.</summary>
    public string Resolve(string preference, string telegramLanguageCode) =>
        Has(preference) ? preference : Detect(telegramLanguageCode);

    public string Detect(string telegramLanguageCode)
    {
        if (string.IsNullOrEmpty(telegramLanguageCode)) return DefaultCode;
        var code = telegramLanguageCode.ToLowerInvariant().Replace('_', '-');
        if (_languages.ContainsKey(code)) return code;
        var primary = code.Split('-')[0];
        return _languages.ContainsKey(primary) ? primary : DefaultCode;
    }

    private static void Validate(Dictionary<string, LanguageInfo> languages)
    {
        var known = typeof(LocKey).GetFields().Select(f => (string)f.GetRawConstantValue()).ToHashSet();
        foreach (var language in languages.Values)
        {
            var unknown = language.Translations.Keys.Where(k => !known.Contains(k)).ToList();
            if (unknown.Count > 0)
                Console.Error.WriteLine($"Locale '{language.Code}': unknown keys ignored: {string.Join(", ", unknown)}");

            if (language.Code == DefaultCode) continue;
            var missing = known.Count(k => !language.Translations.TryGetValue(k, out var v) || string.IsNullOrEmpty(v));
            if (missing > 0)
                Console.WriteLine($"Locale '{language.Code}': {missing} key(s) not translated, English is used instead.");
        }

        var missingEnglish = known.Where(k => !languages[DefaultCode].Translations.ContainsKey(k)).ToList();
        if (missingEnglish.Count > 0)
            Console.Error.WriteLine($"Locale '{DefaultCode}' is missing keys: {string.Join(", ", missingEnglish)}");
    }

    public string T(string code, string key)
    {
        if (Lookup(code, key) is { } value) return value;
        return Lookup(DefaultCode, key) ?? key;
    }

    public string F(string code, string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, T(code, key), args);
        }
        catch (FormatException)
        {
            // a broken translation must not break the bot: use the English text
            return string.Format(CultureInfo.InvariantCulture, Lookup(DefaultCode, key) ?? key, args);
        }
    }

    private string Lookup(string code, string key) =>
        code != null && _languages.TryGetValue(code, out var l)
        && l.Translations.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;
    private sealed class LocaleFile
    {
        public string Name { get; set; }
        public string Flag { get; set; }
        public Dictionary<string, string> Translations { get; set; }
    }
}
