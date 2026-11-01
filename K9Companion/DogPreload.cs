using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The compressed form of the dog's textures, kept on disk. Compressing a 2048 x 2048 picture takes a large part of a
    /// second on the main thread; with a dozen of them that was the freeze when a dog appeared. It's done once, the first
    /// time a picture is needed, and afterwards the result only has to be read back. A picture that changes (an update
    /// of the mod) is done again.
    /// </summary>
    internal static class TextureCache
    {
        private static string Folder => Path.Combine(Plugin.AssetFolder, "cache");

        internal static Texture2D Load(string path)
        {
            string name = Path.GetFileName(path);
            var source = new FileInfo(path);
            string stamp = (source.LastWriteTimeUtc.Ticks ^ source.Length).ToString("x");
            Texture2D texture = ReadBack(name, stamp);
            if (texture != null) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException("Cannot decode " + name);
            }
            texture.Compress(true);
            Keep(name, stamp, texture);
            // On the graphics card (about a sixth of the memory), with no copy kept in RAM.
            texture.Apply(false, true);
            return texture;
        }

        // <picture>.<stamp>.<width>.<height>.<format>.tex
        private static Texture2D ReadBack(string name, string stamp)
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;
                string file = Directory.GetFiles(Folder, name + "." + stamp + ".*.tex").FirstOrDefault();
                if (file == null) return null;
                string[] parts = Path.GetFileName(file).Substring(name.Length + stamp.Length + 2).Split('.');
                var texture = new Texture2D(int.Parse(parts[0]), int.Parse(parts[1]), (TextureFormat)int.Parse(parts[2]), true);
                texture.LoadRawTextureData(File.ReadAllBytes(file));
                texture.Apply(false, true);
                return texture;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("A kept texture of " + name + " couldn't be read; it's made again: " + e.Message);
                return null;
            }
        }

        private static void Keep(string name, string stamp, Texture2D texture)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (string old in Directory.GetFiles(Folder, name + ".*.tex")) File.Delete(old);
                File.WriteAllBytes(Path.Combine(Folder, $"{name}.{stamp}.{texture.width}.{texture.height}.{(int)texture.format}.tex"), texture.GetRawTextureData());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't keep the texture of " + name + ": " + e.Message); }
        }
    }

    internal sealed class PreparedDog
    {
        internal GameObject Dog;
        internal DogModel Model;
        internal CompanionDecor Decor;

        internal void Dispose()
        {
            Decor?.Dispose(); Model?.Dispose();
            if (Dog != null) UnityEngine.Object.Destroy(Dog);
        }
    }

    /// <summary>
    /// The raid's dog is made ready during the loading screen, as your character is, a piece at a time, so that when the raid
    /// starts it only has to be put down beside you. Loaded when it spawned, it froze the game just as the raid began.
    /// </summary>
    internal static class DogPreload
    {
        private static PreparedDog ready;
        private static Coroutine running;

        // Still being made: the raid's dog waits for it rather than loading a second one.
        internal static bool Busy => running != null;

        internal static void Begin(string vest, string head, bool camera, string collar)
        {
            Release();
            running = Plugin.Runner.StartCoroutine(Prepare(vest, head, camera, collar));
        }

        internal static PreparedDog Take()
        {
            PreparedDog dog = ready;
            ready = null;
            return dog;
        }

        internal static void Release()
        {
            if (running != null) Plugin.Runner.StopCoroutine(running);
            running = null;
            ready?.Dispose();
            ready = null;
        }

        private static IEnumerator Prepare(string vest, string head, bool camera, string collar)
        {
            var dog = new PreparedDog();
            var steps = new Action[]
            {
                () =>
                {
                    dog.Dog = new GameObject("K9 Companion dog");
                    dog.Dog.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(dog.Dog);
                    dog.Model = DogModel.Load(Plugin.AssetFolder, dog.Dog.transform, Plugin.Scale.Value);
                },
                () => dog.Decor = new CompanionDecor(dog.Dog.transform, dog.Model),
                () => dog.Decor.SetVest(vest),
                () => dog.Decor.SetHead(head),
                () => dog.Decor.SetCamera(camera),
                () => dog.Decor.SetCosmetics(collar)
                // Not the sounds: the game restarts its audio (AudioSettings.Reset) while a raid loads, after this has run, and
                // that silences clips made before it. They take no time to make when the dog comes out.
            };
            while (!DogModel.ClipsReady) yield return null;
            foreach (Action step in steps)
            {
                try { step(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("The dog couldn't be made ready for the raid (it's tried again when the raid starts): " + e);
                    dog.Dispose();
                    running = null;
                    yield break;
                }
                yield return null;
            }
            ready = dog;
            running = null;
            Plugin.Log.LogInfo("The dog is ready for the raid.");
        }
    }
}
