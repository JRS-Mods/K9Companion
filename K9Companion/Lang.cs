using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace K9Companion
{
    /// <summary>
    /// Translations of the mod's own text (messages, the DOG tab, the lobby panel, the dog camera, the kennel, the Y menu).
    /// lang/&lt;language&gt;.json next to the DLL maps the English text to the translation, in the game's language (or
    /// the one set in Advanced > Language). lang/en.json lists every text, to copy and translate.
    ///
    /// A text with {0}, {1}... in it stands for any text there (a name, a number, another text): "{0} is here." matches
    /// "Rex is here.", and what stood in for {0} is translated in turn if it's in the file too. Colour and size tags, new
    /// lines and the " · " separators are kept as they are, and the pieces between them are translated one by one.
    /// Anything not in the file stays in English.
    /// </summary>
    internal static class Lang
    {
        private sealed class Pattern { public Regex Match; public string To; public int Weight; public int[] Slots; }

        private static readonly Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<Pattern> patterns = new List<Pattern>();
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Regex Slot = new Regex(@"\{(\d+)\}");
        private static readonly Regex Breaks = new Regex(@"(<[^>]*>|\n|  ·  )");
        private static string loaded;

        /// <summary>The language in use: Advanced > Language, or the game's.</summary>
        internal static string Language
        {
            get
            {
                string chosen = Plugin.Language?.Value?.Trim();
                if (!string.IsNullOrEmpty(chosen) && !chosen.Equals("auto", StringComparison.OrdinalIgnoreCase)) return chosen.ToLowerInvariant();
                try { return EFT.LocalizationManager.Instance.Culture ?? "en"; }
                catch (Exception) { return "en"; }
            }
        }

        /// <summary>The text in the language in use, or as it is if there's no translation for it.</summary>
        internal static string T(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            Load();
            if (exact.Count == 0 && patterns.Count == 0) return text;
            if (cache.TryGetValue(text, out string done)) return done;
            string result;
            try { result = Whole(text, 0); }
            catch (Exception) { result = text; }
            if (cache.Count > 4000) cache.Clear();
            return cache[text] = result;
        }

        private static void Load()
        {
            string language = Language;
            if (language == loaded) return;
            loaded = language;
            exact.Clear(); patterns.Clear(); cache.Clear();
            if (Plugin.AssetFolder == null) { loaded = null; return; }
            string path = Path.Combine(Plugin.AssetFolder, "lang", language + ".json");
            if (language == "en" || !File.Exists(path)) return;
            try
            {
                var file = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                foreach (var entry in file.Properties())
                {
                    if (entry.Name.StartsWith("_") || entry.Value.Type != JTokenType.String) continue;
                    string to = (string)entry.Value;
                    if (string.IsNullOrEmpty(to) || to == entry.Name) continue;
                    if (Slot.IsMatch(entry.Name)) AddPattern(entry.Name, to);
                    else exact[entry.Name] = to;
                }
                // The most specific pattern first: the one with the most text of its own.
                patterns.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                Plugin.Log.LogInfo($"Translations: {exact.Count + patterns.Count} texts in {language} from {path}.");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Translations in {path} couldn't be read, so the mod stays in English: {e.Message}"); }
        }

        private static void AddPattern(string english, string to)
        {
            var regex = new StringBuilder("^");
            var slots = new List<int>();
            int at = 0, weight = 0;
            foreach (Match m in Slot.Matches(english))
            {
                string literal = english.Substring(at, m.Index - at);
                regex.Append(Regex.Escape(literal)); weight += literal.Length;
                int n = int.Parse(m.Groups[1].Value);
                if (slots.Contains(n)) regex.Append(@"\k<s" + n + ">");
                else { regex.Append("(?<s" + n + ">.*?)"); slots.Add(n); }
                at = m.Index + m.Length;
            }
            string tail = english.Substring(at);
            regex.Append(Regex.Escape(tail)).Append('$'); weight += tail.Length;
            // A pattern that's nothing but stand-ins would match anything.
            if (weight < 2) return;
            patterns.Add(new Pattern { Match = new Regex(regex.ToString(), RegexOptions.Singleline), To = to, Weight = weight, Slots = slots.ToArray() });
        }

        private static string Whole(string text, int depth)
        {
            if (text.Length == 0) return text;
            if (Lookup(text, depth, out string whole)) return whole;
            string spaced = Piece(text, depth);
            if (!ReferenceEquals(spaced, text) && spaced != text) return spaced;
            string[] parts = Breaks.Split(text);
            if (parts.Length == 1) return text;
            var result = new StringBuilder(text.Length);
            foreach (string part in parts)
                result.Append(part.Length == 0 || IsBreak(part) ? part : Piece(part, depth));
            return result.ToString();
        }

        private static bool IsBreak(string part) => part == "\n" || part == "  ·  " || (part.StartsWith("<") && part.EndsWith(">"));

        // One piece between tags and line breaks; the spaces around it stay where they are.
        private static string Piece(string part, int depth)
        {
            int a = 0, b = part.Length;
            while (a < b && char.IsWhiteSpace(part[a])) a++;
            while (b > a && char.IsWhiteSpace(part[b - 1])) b--;
            if (a == b) return part;
            if (a == 0 && b == part.Length) return Lookup(part, depth, out string same) ? same : part;
            string core = part.Substring(a, b - a);
            return Lookup(core, depth, out string t) ? part.Substring(0, a) + t + part.Substring(b) : part;
        }

        private static bool Lookup(string text, int depth, out string result)
        {
            if (exact.TryGetValue(text, out result)) return true;
            result = null;
            if (depth >= 3 || text.Length == 0) return false;
            foreach (Pattern p in patterns)
            {
                Match m = p.Match.Match(text);
                if (!m.Success) continue;
                var values = p.Slots.ToDictionary(n => n, n => Whole(m.Groups["s" + n].Value, depth + 1));
                result = Slot.Replace(p.To, s => values.TryGetValue(int.Parse(s.Groups[1].Value), out string v) ? v : s.Value);
                return true;
            }
            return false;
        }
    }
}
