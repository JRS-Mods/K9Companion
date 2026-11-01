using System;
using System.IO;
using SPT.Common.Http;

namespace K9Companion
{
    /// <summary>
    /// Saves the dog's needs in your SPT profile data through the server mod, so they survive raids, game restarts
    /// and reinstalls of the client. A copy is also kept beside the plugin and used if the server mod can't be reached.
    /// Both sides validate the numbers.
    /// </summary>
    internal static class NeedsStore
    {
        private const string LoadRoute = "/k9companion/needs/load", SaveRoute = "/k9companion/needs/save";
        private static DogNeeds current;

        private static string LocalFile => Path.Combine(Plugin.AssetFolder, "needs.json");

        // Loaded once per game session; every dog (hideout or raid) shares the same state.
        internal static DogNeeds Current
        {
            get
            {
                if (current != null) return current;
                string json = null;
                try
                {
                    json = RequestHandler.PostJson(LoadRoute, "{}");
                    if (json == null || !json.Contains("\"hunger\"")) json = null;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't load the dog's needs from the server mod: " + e.Message); }
                if (json == null && File.Exists(LocalFile))
                {
                    try { json = File.ReadAllText(LocalFile); Plugin.Log.LogInfo("Dog's needs loaded from the local copy."); }
                    catch (Exception e) { Plugin.Log.LogWarning("Couldn't read " + LocalFile + ": " + e.Message); }
                }
                current = DogNeeds.FromJson(json);
                Plugin.Log.LogInfo("Dog's needs: " + current.ToJson());
                return current;
            }
        }

        // The loaded state, without loading it (null until something has).
        internal static DogNeeds Peek => current;

        internal static void Save()
        {
            if (current == null) return;
            current.Sanitize();
            string json = current.ToJson();
            try { File.WriteAllText(LocalFile, json); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't write " + LocalFile + ": " + e.Message); }
            try { RequestHandler.PostJson(SaveRoute, json); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't save the dog's needs to the server mod: " + e.Message); }
        }
    }
}
