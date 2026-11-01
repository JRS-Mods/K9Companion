using System;
using System.Text;
namespace K9Companion
{
    internal static class DogNaming
    {
        internal static string Normalize(string value)
        {
            var result = new StringBuilder();
            foreach (char c in (value ?? "").Trim())
            {
                if (!char.IsControl(c) && !char.IsSurrogate(c)) result.Append(c);
                if (result.Length == 16) break;
            }
            return result.ToString().Trim();
        }
    }
}
