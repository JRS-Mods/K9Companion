using System.Text.Json;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace K9Companion.Server;

/// <summary>
/// Translations of the mod's server text (quests, items, Dr. Yang, the dog's slots): lang/&lt;language&gt;.json in the
/// mod's folder maps the English text to the translation for that game language (ru, fr, ge, es...). Each text goes
/// into that language's tables; anything missing stays in English. lang/en.json lists every text, to copy and translate.
/// </summary>
public static class K9Lang
{
    private static Dictionary<string, Dictionary<string, string>>? files;

    private static Dictionary<string, Dictionary<string, string>> Files
    {
        get
        {
            if (files != null) return files;
            files = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string folder = Path.Combine(Path.GetDirectoryName(typeof(K9Lang).Assembly.Location) ?? ".", "lang");
            if (!Directory.Exists(folder)) return files;
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                string language = Path.GetFileNameWithoutExtension(path);
                if (language.Equals("en", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    foreach (var entry in doc.RootElement.EnumerateObject())
                        if (!entry.Name.StartsWith('_') && entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } to && to != entry.Name)
                            map[entry.Name] = to;
                    files[language] = map;
                    Console.WriteLine($"[K9Companion] Translations: {map.Count} texts in {language}.");
                }
                catch (Exception e) { Console.WriteLine($"[K9Companion] {path} couldn't be read, so {language} stays in English: {e.Message}"); }
            }
            return files;
        }
    }

    private static readonly SortedSet<string> english = new(StringComparer.Ordinal);

    /// <summary>
    /// Lists texts in lang/en.json (written when the list grows), so a translator has every text the server adds:
    /// copy it to lang/&lt;language&gt;.json and replace the English on the right.
    /// </summary>
    public static void Note(IEnumerable<string?> texts)
    {
        int before = english.Count;
        foreach (string? t in texts) if (!string.IsNullOrWhiteSpace(t)) english.Add(t);
        if (english.Count == before) return;
        try
        {
            string folder = Path.Combine(Path.GetDirectoryName(typeof(K9Lang).Assembly.Location) ?? ".", "lang");
            Directory.CreateDirectory(folder);
            var template = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["_about"] = "K9 Companion server text: quests, items, Dr. Yang. Copy to <language>.json (ru, fr, ge, es...) and replace the English on the right of each line. Missing or empty lines stay in English."
            };
            foreach (string t in english) template[t] = t;
            string json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            string path = Path.Combine(folder, "en.json");
            if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        }
        catch (Exception) { }
    }

    /// <summary>The text in the given game language, or the English if there's no translation for it.</summary>
    public static string T(string language, string english)
        => english != null && Files.TryGetValue(language, out var map) && map.TryGetValue(english, out var to) ? to : english!;

    /// <summary>An item's name and description in English and every language there's a translation file for.</summary>
    public static Dictionary<string, LocaleDetails> Locales(LocaleDetails en)
    {
        Note(new[] { en.Name, en.ShortName, en.Description });
        var all = new Dictionary<string, LocaleDetails> { ["en"] = en };
        foreach (string language in Files.Keys)
            all[language] = new LocaleDetails { Name = T(language, en.Name!), ShortName = T(language, en.ShortName!), Description = T(language, en.Description!) };
        return all;
    }
}
