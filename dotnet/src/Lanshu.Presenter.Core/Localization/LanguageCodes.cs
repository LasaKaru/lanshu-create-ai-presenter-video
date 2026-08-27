namespace Lanshu.Presenter.Core.Localization;

/// <summary>
/// Turns a language a person typed into the code a speech engine expects.
///
/// People write "Spanish"; espeak-ng, ElevenLabs and Azure all want "es". Handing an engine the
/// display name is not a soft failure — espeak-ng exits with "the specified voice does not
/// exist" and the whole take is lost — so the translation happens once, here, rather than in
/// each engine.
///
/// Anything that already looks like a code or a locale is passed through untouched: an operator
/// who typed "pt-BR" on purpose knows more about what they want than this table does.
/// </summary>
public static class LanguageCodes
{
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en",
        ["spanish"] = "es",
        ["castilian"] = "es",
        ["french"] = "fr",
        ["german"] = "de",
        ["italian"] = "it",
        ["portuguese"] = "pt",
        ["brazilian portuguese"] = "pt-br",
        ["dutch"] = "nl",
        ["polish"] = "pl",
        ["russian"] = "ru",
        ["ukrainian"] = "uk",
        ["turkish"] = "tr",
        ["arabic"] = "ar",
        ["hebrew"] = "he",
        ["hindi"] = "hi",
        ["bengali"] = "bn",
        ["tamil"] = "ta",
        ["telugu"] = "te",
        ["sinhala"] = "si",
        ["urdu"] = "ur",
        ["indonesian"] = "id",
        ["malay"] = "ms",
        ["vietnamese"] = "vi",
        ["thai"] = "th",
        ["korean"] = "ko",
        ["japanese"] = "ja",
        ["chinese"] = "zh",
        ["mandarin"] = "zh",
        ["simplified chinese"] = "zh",
        ["traditional chinese"] = "zh-tw",
        ["cantonese"] = "yue",
        ["swedish"] = "sv",
        ["norwegian"] = "nb",
        ["danish"] = "da",
        ["finnish"] = "fi",
        ["greek"] = "el",
        ["czech"] = "cs",
        ["romanian"] = "ro",
        ["hungarian"] = "hu",
        ["swahili"] = "sw",
    };

    /// <summary>
    /// Returns an engine-ready code, or an empty string when the language is unknown or "auto" —
    /// an engine given nothing picks its own default, which is better than being given a name it
    /// will reject outright.
    /// </summary>
    public static string ToCode(string? language)
    {
        var value = (language ?? string.Empty).Trim();

        if (value.Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (ByName.TryGetValue(value, out var mapped))
        {
            return mapped;
        }

        // Already a code or locale: "en", "pt-BR", "zh_Hans".
        if (value.Length <= 8 && value.All(character => char.IsAsciiLetter(character) || character is '-' or '_'))
        {
            return value.Replace('_', '-').ToLowerInvariant();
        }

        // A name this table does not know. Saying nothing beats saying something wrong.
        return string.Empty;
    }
}
