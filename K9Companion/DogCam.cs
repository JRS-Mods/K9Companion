using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.CameraControl;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace K9Companion
{
    /// <summary>
    /// What the dog's camera sees, top left of your screen in a raid, like a bodycam feed. Only when the dog wears a
    /// camera: the camera vest's own, or the clip-on action camera on its jacket.
    /// Its key (Dog camera > Toggle key) shows or hides it.
    ///
    /// Its own camera, never enabled (so Unity never renders it by itself), is rendered by hand a few times a second, and
    /// only while the feed is showing. The game treats it as it treats your scope's camera: its clouds, water, wall decals
    /// and area lights are drawn for it, at its size. It has the grass, and your view's colour (see Grade). Dog camera >
    /// Resolution sets what each render costs: Low is small, without shadows or grass, with less detail and two lights per
    /// pixel; Medium is larger, with the game's atmospheric haze, grass and shadows close by; High is as many pixels as the
    /// feed covers on screen, in full detail, shadows further out. Your scope's lens (drawn for your eye only, a black disc
    /// from anywhere else) is left out. If the dog dies the feed shows NO SIGNAL and stops rendering.
    /// </summary>
    internal sealed class DogCam : MonoBehaviour
    {
        internal Companion Dog;
        private Camera cam;
        private RenderTexture picture;
        private GameObject overlay;
        private RawImage view;
        private TextMeshProUGUI label, signal;
        // The feed's frame, for the health bar's place on the HUD (DogHud): it goes under the feed when both share a spot.
        private static RectTransform shownFrame;
        private static readonly Vector3[] feedCorners = new Vector3[4];

        /// <summary>The feed's rectangle on screen in pixels from the bottom left, while it's showing.</summary>
        internal static bool FeedOnScreen(out Rect rect)
        {
            rect = default;
            if (shownFrame == null || !shownFrame.gameObject.activeInHierarchy) return false;
            shownFrame.GetWorldCorners(feedCorners);
            rect = Rect.MinMaxRect(feedCorners[0].x, feedCorners[0].y, feedCorners[2].x, feedCorners[2].y);
            return true;
        }
        private Transform mount;
        private float nextRender, shownSize = -1;
        private int shownResolution = -1, labelMetres = -1;
        private bool labelBlink;
        private string labelName;
        private DogCamPlace shownPlace;

        // Low and Medium; High follows the feed's size on screen.
        private static readonly Vector2Int[] Resolutions = { new Vector2Int(384, 216), new Vector2Int(640, 360) };
        private DogCamQuality Quality => (DogCamQuality)Mathf.Clamp((int)Plugin.DogCamQuality.Value, 0, 2);

        private void LateUpdate()
        {
            try
            {
                if (Dog == null) return;
                if (GameBinds.Pressed(Plugin.DogCamKey, Dog.Say, Dog.Owner != null && Dog.Owner.Side == EPlayerSide.Savage))
                {
                    Plugin.DogCamShow.Value = !Plugin.DogCamShow.Value;
                    Dog.Say("Dog camera " + (Plugin.DogCamShow.Value ? "on." : "off."));
                }
                mount = Dog.CameraMount();
                bool show = Plugin.DogCamShow.Value && mount != null && Dog.Owner != null && Dog.Owner.HealthController != null && Dog.Owner.HealthController.IsAlive;
                if (!show) { if (overlay != null && overlay.activeSelf) overlay.SetActive(false); return; }
                Build();
                Place();
                if (!overlay.activeSelf) overlay.SetActive(true);
                bool dead = Dog.IsDead;
                signal.gameObject.SetActive(dead);
                view.color = dead ? new Color(.12f, .12f, .12f) : Color.white;
                float metres = Vector3.Distance(Dog.Owner.Transform.position, mount.position);
                bool blink = Time.unscaledTime % 1.2f < .7f && !dead;
                // Only when it reads differently: setting a label's text rebuilds it, and this runs every frame.
                int shownMetres = Mathf.RoundToInt(metres);
                string name = Plugin.DogName.Value ?? "K9";
                if (blink != labelBlink || shownMetres != labelMetres || name != labelName)
                {
                    labelBlink = blink; labelMetres = shownMetres; labelName = name;
                    label.text = (blink ? "<color=#e03a2f>●</color> " : "   ") + "REC  K9 CAM  " + name.ToUpperInvariant() + $"  {shownMetres} m";
                }
                if (dead || Time.unscaledTime < nextRender) return;
                nextRender = Time.unscaledTime + 1f / Mathf.Clamp(Plugin.DogCamFps.Value, 5, 30);
                // Drawn once your own view has been (see Grass).
                renderDue = true;
                if (endOfFrame == null) endOfFrame = StartCoroutine(EndOfFrame());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Dog camera failed and is turned off for this raid: " + e);
                enabled = false;
                if (overlay != null) overlay.SetActive(false);
            }
        }

        private bool renderDue;
        private Coroutine endOfFrame;
        private static readonly WaitForEndOfFrame FrameDone = new WaitForEndOfFrame();
        private System.Collections.IEnumerator EndOfFrame()
        {
            while (true)
            {
                yield return FrameDone;
                if (!renderDue || cam == null || mount == null) continue;
                renderDue = false;
                try
                {
                    Render();
                    if (!reported && Time.unscaledTime >= reportAt) { reported = true; Report(); }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Dog camera failed and is turned off for this raid: " + e);
                    enabled = false;
                    if (overlay != null) overlay.SetActive(false);
                    endOfFrame = null;
                    yield break;
                }
            }
        }

        // Once a raid, 10 s after the feed first shows: what your view's camera, the scope camera and the dog's camera each
        // carry (their effects and the extra drawing the game hooks into them), so the feed can be given what yours has.
        private bool reported;
        private float reportAt = -1;
        private void Report()
        {
            try
            {
                Camera main = CameraManager.Instance.Camera, optic = CameraManager.Instance.OpticCameraManager?.Camera;
                foreach (var (label, c) in new[] { ("your view", main), ("scope", optic), ("dog", cam) })
                {
                    if (c == null) { Plugin.Log.LogInfo($"Camera report, {label}: none"); continue; }
                    var text = new System.Text.StringBuilder();
                    text.Append($"Camera report, {label} '{c.name}': on {c.enabled}, far {c.farClipPlane}, hdr {c.allowHDR}, depth {c.depthTextureMode}, path {c.actualRenderingPath}, clear {c.clearFlags}, mask {c.cullingMask}");
                    foreach (Component component in c.GetComponents<Component>())
                        text.AppendLine().Append("  component ").Append(component.GetType().FullName).Append(component is Behaviour b && !b.enabled ? " (off)" : "");
                    foreach (UnityEngine.Rendering.CameraEvent e in Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
                        foreach (UnityEngine.Rendering.CommandBuffer buffer in c.GetCommandBuffers(e))
                            text.AppendLine().Append("  commands ").Append(e).Append(": ").Append(buffer.name).Append(" (").Append(buffer.sizeInBytes).Append(" bytes)");
                    Plugin.Log.LogInfo(text.ToString());
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Camera report: " + e.Message); }
        }

        private void Render()
        {
            if (reportAt < 0) reportAt = Time.unscaledTime + 10;
            Camera main = Camera.main;
            cam.transform.SetPositionAndRotation(mount.position, mount.rotation);
            if (main != null) { cam.cullingMask = main.cullingMask; cam.renderingPath = main.actualRenderingPath; }
            Light();
            Haze(main);
            ShowBody(true);
            HideLenses(true);
            ShadowQuality shadows = QualitySettings.shadows;
            float lodBias = QualitySettings.lodBias, shadowDistance = QualitySettings.shadowDistance;
            int pixelLights = QualitySettings.pixelLightCount, cascades = QualitySettings.shadowCascades;
            DogCamQuality quality = Quality;
            // Shadows cost the most: the game's shadow maps are drawn again for every camera. Here they only reach a short
            // way (where a bodycam's eye notices them) with fewer cascades; on Low there are none.
            if (quality == DogCamQuality.Low) { QualitySettings.shadows = ShadowQuality.Disable; QualitySettings.lodBias = lodBias * .5f; QualitySettings.pixelLightCount = Mathf.Min(pixelLights, 2); }
            else
            {
                bool high = quality == DogCamQuality.High;
                QualitySettings.shadowDistance = Mathf.Min(shadowDistance, high ? 35 : 20);
                QualitySettings.shadowCascades = Mathf.Min(cascades, high ? 2 : 1);
                QualitySettings.lodBias = lodBias;
                QualitySettings.pixelLightCount = Mathf.Min(pixelLights, high ? 6 : 3);
            }
            cam.farClipPlane = quality == DogCamQuality.High ? 200 : quality == DogCamQuality.Medium ? 130 : 90;
            Grade(main);
            Mirror();
            if (quality != DogCamQuality.Low && Plugin.DogCamGrass.Value) Grass();
            Sky(true);
            Application.logMessageReceived += Watch;
            try { cam.Render(); }
            finally
            {
                Application.logMessageReceived -= Watch;
                Sky(false);
                GrassBack();
                ShowBody(false);
                HideLenses(false);
                QualitySettings.shadows = shadows;
                QualitySettings.lodBias = lodBias;
                QualitySettings.pixelLightCount = pixelLights;
                QualitySettings.shadowDistance = shadowDistance;
                QualitySettings.shadowCascades = cascades;
                LeaveOutBroken();
            }
        }

        // Grass, bushes and the rest of the game's instanced foliage are culled on the graphics card for your camera and
        // drawn to it alone. The feed renders at the very end of the frame, after your view has been drawn: there your
        // view's set is culled again for the dog's camera and drawn into the feed, and left so; the game culls it for your
        // camera afresh at the start of every frame, before your next view is drawn. (Drawn as it was, it showed grass only
        // where you looked; culled again mid-frame it made your view flicker.)
        //
        // The grass shader draws its colour only where its own depth pass has drawn first, at exactly that depth (a cheap
        // pass marks where the blades are, the full one then fills just those pixels). The game puts that first pass on your
        // camera's own drawing steps, so the feed only ever got the grass's shadows: the black patches. The feed has its own
        // step for it, run before its picture is drawn, which also writes the grass depth the game's ambient light reads.
        //
        // Grass on a terrain is kept in cells, and only the cells around the camera it's culled for have their grass made
        // (GPUInstancerDetailManager.UpdateSpatialPartitioningCells). Culled for the dog's camera, that was the cells round
        // the dog: whenever it stood in different cells from you (often a few metres off, always on a run out), every feed
        // frame threw your cells away and built the dog's, and your next frame did the reverse, each time in one go on the
        // graphics card: the hitching. For the dog's pass the cells stay as your camera has them (the rebuild's own "busy"
        // flag, set just for that call): what's within your grass distance is all the feed shows.
        private GPUInstancer.GPUInstancerCameraData grassView;
        private readonly List<(GPUInstancer.GPUInstancerManager manager, GPUInstancer.GPUInstancerCameraData cameraData, bool occlusion, Vector3 centre)> borrowed =
            new List<(GPUInstancer.GPUInstancerManager, GPUInstancer.GPUInstancerCameraData, bool, Vector3)>();
        private static HarmonyLib.AccessTools.FieldRef<GPUInstancer.GPUInstancerTerrainManager, bool> rebuildingFlag;
        private static bool rebuildingLooked;
        // Null if the game no longer has that flag: then a terrain's grass is left out of the feed rather than rebuilt for it.
        private static HarmonyLib.AccessTools.FieldRef<GPUInstancer.GPUInstancerTerrainManager, bool> Rebuilding
        {
            get
            {
                if (rebuildingLooked) return rebuildingFlag;
                rebuildingLooked = true;
                try { rebuildingFlag = HarmonyLib.AccessTools.FieldRefAccess<GPUInstancer.GPUInstancerTerrainManager, bool>("replacingInstances"); }
                catch (Exception e) { Plugin.Log.LogWarning("Dog camera: terrain grass is left out of the feed (it can't be kept from rebuilding): " + e.Message); }
                return rebuildingFlag;
            }
        }
        private bool grassFailed, grassLogged;
        private readonly List<GPUInstancer.GPUInstancerPrototype> fades = new List<GPUInstancer.GPUInstancerPrototype>();
        private UnityEngine.Rendering.CommandBuffer grassDepth;
        private RenderTexture grassDepthTexture;
        private Texture grassDepthBefore;
        private bool grassDepthSet;
        private static readonly int GrassDepthId = Shader.PropertyToID("_CameraMotionVectorsDepth");
        private void Grass()
        {
            borrowed.Clear();
            if (grassFailed || GPUInstancer.GPUInstancerManager.activeManagerList == null) return;
            try
            {
                if (grassView == null || grassView.mainCamera != cam) grassView = new GPUInstancer.GPUInstancerCameraData(cam);
                if (grassDepth == null)
                {
                    grassDepth = new UnityEngine.Rendering.CommandBuffer { name = "K9 dog camera grass depth" };
                    cam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeGBuffer, grassDepth);
                }
                // The size of the feed's picture: the depth it draws into is the feed camera's own.
                if (grassDepthTexture == null || grassDepthTexture.width != cam.pixelWidth || grassDepthTexture.height != cam.pixelHeight)
                {
                    if (grassDepthTexture != null) { grassDepthTexture.Release(); Destroy(grassDepthTexture); }
                    grassDepthTexture = new RenderTexture(cam.pixelWidth, cam.pixelHeight, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                    { name = "K9 dog camera grass depth", useMipMap = false, antiAliasing = 1, wrapMode = TextureWrapMode.Clamp };
                }
                grassDepth.Clear();
                grassDepth.SetRenderTarget(grassDepthTexture, UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget);
                grassDepth.ClearRenderTarget(false, true, Color.black);
                foreach (GPUInstancer.GPUInstancerManager manager in GPUInstancer.GPUInstancerManager.activeManagerList)
                {
                    if (manager == null || manager.IsOptic || !manager.isActiveAndEnabled || manager.runtimeDataList == null) continue;
                    Camera its = manager.Camera;
                    if (its == null || !its.isActiveAndEnabled) continue;
                    var terrain = manager as GPUInstancer.GPUInstancerTerrainManager;
                    var hold = Rebuilding;
                    if (terrain != null && hold == null) continue;
                    borrowed.Add((manager, manager.cameraData, manager.isOcclusionCulling, manager.instancingBounds.center));
                    // No occlusion test: that needs a depth pyramid the dog's camera hasn't got.
                    manager.cameraData = grassView; manager.isOcclusionCulling = false;
                    // Plants coming into view fade in over several frames; your camera resets that between the feed's frames,
                    // so for the dog's pass they're shown at once (by distance, without the fade-in), then it's back on.
                    fades.Clear();
                    foreach (var rd in manager.runtimeDataList)
                        if (rd?.prototype != null && rd.prototype.isLODCrossFadeAnimate) { fades.Add(rd.prototype); rd.prototype.isLODCrossFadeAnimate = false; }
                    // Your camera's grass cells kept (see above); a rebuild already under way stays as it was.
                    bool rebuilding = terrain != null && hold(terrain);
                    if (terrain != null) hold(terrain) = true;
                    try { manager.UpdateBuffers(); }
                    finally
                    {
                        if (terrain != null) hold(terrain) = rebuilding;
                        foreach (var prototype in fades) prototype.isLODCrossFadeAnimate = true;
                    }
                    GPUInstancer.GPUInstancerUtility.DrawPrePass(grassDepth, manager.runtimeDataList, manager.instancingBounds, grassView, false);
                    GPUInstancer.GPUInstancerUtility.GPUIDrawMeshInstancedIndirect(manager.runtimeDataList, manager.instancingBounds, grassView);
                }
                grassDepth.SetRenderTarget(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget);
                grassDepth.SetGlobalTexture(GrassDepthId, grassDepthTexture);
                grassDepthBefore = Shader.GetGlobalTexture(GrassDepthId); grassDepthSet = true;
                if (!grassLogged) { grassLogged = true; Plugin.Log.LogInfo($"Dog camera: grass drawn in the feed, with its depth pass ({borrowed.Count} foliage set(s))."); }
            }
            catch (Exception e)
            {
                grassFailed = true;
                grassDepth?.Clear();
                Plugin.Log.LogWarning("Dog camera: the game's foliage couldn't be drawn in the feed; it goes without: " + e.Message);
            }
        }

        // Your view's set handed back to your camera (it's culled for it again before your next frame), and the grass depth
        // your view's ambient light reads put back to yours.
        private void GrassBack()
        {
            foreach (var (manager, cameraData, occlusion, centre) in borrowed)
                if (manager != null) { manager.cameraData = cameraData; manager.isOcclusionCulling = occlusion; manager.instancingBounds.center = centre; }
            borrowed.Clear();
            grassDepth?.Clear();
            if (grassDepthSet) { grassDepthSet = false; Shader.SetGlobalTexture(GrassDepthId, grassDepthBefore); grassDepthBefore = null; }
        }

        // The game's sky (sun, moon, sky colour and its clouds) is a dome its time-of-day script sizes to your camera's range
        // and centres on it just before your view is drawn; the feed's camera sees only 90-200 m, so the dome was out of its
        // reach and it showed the plain backup sky. For the feed's frame the dome is sized to the feed's range and centred on
        // the dog's camera, then put back as it was (your camera sets it again before your own frame anyway).
        private TOD_Sky sky;
        private bool skySearched;
        private Vector3 domeScale, domePosition;
        private void Sky(bool on)
        {
            try
            {
                if (!skySearched) { skySearched = true; sky = UnityEngine.Object.FindObjectOfType<TOD_Sky>(); }
                Transform dome = sky != null && sky.Initialized ? sky.Components?.DomeTransform : null;
                if (dome == null) return;
                if (on)
                {
                    domeScale = dome.localScale; domePosition = dome.position;
                    dome.localScale = Vector3.one * (.95f * cam.farClipPlane);
                    dome.position = cam.transform.position;
                }
                else { dome.localScale = domeScale; dome.position = domePosition; }
            }
            catch (Exception e) { sky = null; Plugin.Log.LogWarning("Dog camera: the game's sky couldn't be shown in the feed: " + e.Message); }
        }

        // The game's colour, as your view has it and in the same order: Prism (tone mapping, exposure, colour correction,
        // bloom), the vintage tone, the weather's grey, sharpening, the post-processing layer's profile and the game's own
        // colour grading (clarity, colourfulness and the colour filter from your graphics settings). Not your blur, depth of
        // field, sun rays or night vision, nor the grey your own health or a mask puts on your view: it's the dog's camera.
        // Your scope's camera goes without these because its picture is seen through your view's; the feed isn't.
        private static readonly string[] Look = { "PrismEffects", "CC_Vintage", "DesaturateEffect", "CC_Sharpen", "GradingPostFX" };
        private readonly List<(Behaviour mine, Behaviour yours)> look = new List<(Behaviour, Behaviour)>();
        private bool graded;
        private void Grade(Camera main)
        {
            if (graded || main == null) return;
            graded = true;
            GameObject holder = cam.gameObject;
            var added = new List<string>();
            UnityEngine.Rendering.PostProcessing.PostProcessLayer ourLayer = null;
            try
            {
                cam.allowHDR = main.allowHDR;
                // Added while the camera's object is switched off, so each effect starts up (Awake) only once its settings
                // are in. GradingPostFX makes its material from its shader there: added live it had no shader yet, failed on
                // every frame and never handed the picture on, which is what turned the feed white.
                holder.SetActive(false);
                foreach (Behaviour yours in main.GetComponents<Behaviour>())
                {
                    string kind = yours != null ? yours.GetType().Name : null;
                    // DesaturateEffect is off on your camera (the sharpening does the weather's grey) but holds its settings.
                    if (kind == null || !Look.Contains(kind) || (!yours.enabled && kind != "DesaturateEffect")) continue;
                    var mine = (Behaviour)holder.AddComponent(yours.GetType());
                    CopySettings(yours, mine);
                    mine.enabled = yours.enabled;
                    if (mine is PrismEffects prism)
                    {
                        prism.useDof = prism.useNearDofBlur = prism.useFullScreenBlur = prism.useUIBlur = prism.useChromaticBlur = false;
                        prism.useNightVision = prism.useRays = false;
                    }
                    look.Add((mine, yours));
                    added.Add(kind);
                }
                var layer = main.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                var resources = layer != null ? HarmonyLib.AccessTools.Field(typeof(UnityEngine.Rendering.PostProcessing.PostProcessLayer), "m_Resources")?.GetValue(layer)
                                                as UnityEngine.Rendering.PostProcessing.PostProcessResources : null;
                if (resources != null)
                {
                    ourLayer = holder.AddComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                    ourLayer.Init(resources);
                    ourLayer.volumeLayer = layer.volumeLayer; ourLayer.volumeTrigger = cam.transform;
                    ourLayer.antialiasingMode = UnityEngine.Rendering.PostProcessing.PostProcessLayer.Antialiasing.None;
                    added.Add("post-processing");
                }
            }
            catch (Exception e)
            {
                foreach (var (mine, _) in look) if (mine != null) Destroy(mine);
                look.Clear();
                if (ourLayer != null) Destroy(ourLayer);
                Plugin.Log.LogWarning("Dog camera: the game's colour couldn't be added to the feed; it goes without: " + e.Message);
                return;
            }
            finally { holder.SetActive(true); }
            // One that still didn't start up (no material to draw with) would fail on every frame: left out.
            foreach (var (mine, _) in look.ToList())
            {
                FieldInfo material = mine != null ? mine.GetType().GetField("_material", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) : null;
                if (mine is GradingPostFX && material?.GetValue(mine) == null)
                {
                    Plugin.Log.LogWarning("Dog camera: GradingPostFX didn't start up in the feed; it goes without.");
                    look.RemoveAll(l => l.mine == mine);
                    added.Remove(mine.GetType().Name);
                    Destroy(mine);
                }
            }
            Mirror(true);
            Plugin.Log.LogInfo("Dog camera: the game's colour on (" + (added.Count > 0 ? string.Join(", ", added) : "nothing found") + ").");
        }

        // What changes during a raid, copied across once a second: the weather's grey (the game's weather sets it on your
        // view's sharpening as fog comes and goes) and GradingPostFX's settings, which your graphics settings keep in its
        // private fields and its material.
        private float nextMirror;
        private void Mirror(bool now = false)
        {
            if (!now && Time.unscaledTime < nextMirror) return;
            nextMirror = Time.unscaledTime + 1;
            foreach (var (mine, yours) in look)
            {
                if (mine == null || yours == null) continue;
                switch (mine.GetType().Name)
                {
                    case "CC_Sharpen":
                    case "DesaturateEffect":
                        CopySettings(yours, mine);
                        Zero(mine, "HealthDesaturate", "MaskDesaturate");
                        break;
                    case "GradingPostFX":
                        CopySettings(yours, mine);
                        CopyState(yours, mine);
                        break;
                }
            }
        }

        // An effect that throws while the feed is drawn never hands the picture on (the feed goes white or freezes): one
        // that does is left out for the rest of the raid, and the log says which. Anything else that fails while the feed
        // is drawn is written to the log once. Logged after the render, not from inside Unity's log.
        private readonly List<(Behaviour effect, string why)> broken = new List<(Behaviour, string)>();
        private string otherFailure;
        private bool otherFailureLogged;
        private void Watch(string message, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            foreach (var (mine, _) in look)
                if (mine != null && stack != null && stack.Contains(mine.GetType().Name + ".") && !broken.Any(b => b.effect == mine))
                {
                    broken.Add((mine, message));
                    return;
                }
            if (otherFailure == null) otherFailure = message + (string.IsNullOrEmpty(stack) ? "" : " at " + stack.Split('\n')[0].Trim());
        }

        private void LeaveOutBroken()
        {
            foreach (var (effect, why) in broken)
            {
                if (effect == null) continue;
                Plugin.Log.LogWarning($"Dog camera: the game's {effect.GetType().Name} failed in the feed and is left out: {why}");
                look.RemoveAll(l => l.mine == effect);
                Destroy(effect);
            }
            broken.Clear();
            if (otherFailure != null && !otherFailureLogged)
            {
                otherFailureLogged = true;
                Plugin.Log.LogWarning("Dog camera: something failed while the feed was drawn (logged once): " + otherFailure);
            }
        }

        // A component's saved settings onto another of its kind: not its working materials, render textures, 3D colour
        // tables (destroyed when it stops) or camera.
        private static void CopySettings(Component from, Component to)
        {
            for (Type t = from.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsNotSerialized || f.IsInitOnly || (!f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length == 0)) continue;
                    if (typeof(Material).IsAssignableFrom(f.FieldType) || typeof(RenderTexture).IsAssignableFrom(f.FieldType) ||
                        typeof(Texture3D).IsAssignableFrom(f.FieldType) || typeof(Camera).IsAssignableFrom(f.FieldType)) continue;
                    f.SetValue(to, f.GetValue(from));
                }
        }

        // A component's private working values (numbers, switches, colour tables from the game's resources) and its
        // material's properties; not the textures it draws into, its camera or anything else it points at.
        private static void CopyState(Component from, Component to)
        {
            foreach (FieldInfo f in from.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsInitOnly) continue;
                Type t = f.FieldType;
                if (t.IsPrimitive || t.IsEnum || t == typeof(Texture2D)) f.SetValue(to, f.GetValue(from));
                else if (t == typeof(Material) && f.GetValue(from) is Material theirs && f.GetValue(to) is Material ours && theirs.shader == ours.shader)
                    ours.CopyPropertiesFromMaterial(theirs);
            }
        }

        private static void Zero(Component c, params string[] fields)
        {
            foreach (string name in fields)
            {
                FieldInfo f = c.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (f != null && f.FieldType == typeof(float)) f.SetValue(c, 0f);
            }
        }

        // The game's atmospheric haze (its time-of-day scattering), as its own scope camera has it, set like the main
        // camera's; on Medium and High. Left off for good if it won't work here.
        private TOD_Scattering haze;
        private bool hazeFailed;
        private void Haze(Camera main)
        {
            bool wanted = Quality != DogCamQuality.Low && !hazeFailed;
            if (haze != null && !wanted) { haze.enabled = false; return; }
            if (!wanted || main == null) return;
            TOD_Scattering source = main.GetComponent<TOD_Scattering>();
            if (source == null) return;
            try
            {
                if (haze == null) haze = cam.gameObject.AddComponent<TOD_Scattering>();
                haze.ScatteringShader = source.ScatteringShader; haze.DitheringTexture = source.DitheringTexture;
                haze.GlobalDensity = source.GlobalDensity; haze.HeightFalloff = source.HeightFalloff; haze.SunrizeGlow = source.SunrizeGlow;
                haze.ZeroLevel = source.ZeroLevel; haze.FromLevelSettings = source.FromLevelSettings; haze.Lighten = source.Lighten;
                haze.enabled = source.enabled;
            }
            catch (Exception e)
            {
                hazeFailed = true;
                if (haze != null) { Destroy(haze); haze = null; }
                Plugin.Log.LogWarning("Dog camera: the game's haze couldn't be added; the feed goes without it: " + e.Message);
            }
        }

        // Your scope's lens shows what the scope camera sees, for your eye only; from the dog's camera it's a big black
        // disc over you. Hidden for the dog's render. Your sights are looked for on you, now and then, and the one you're
        // looking through is the game's own; never across the whole map, which stalled the game every time.
        private readonly List<OpticSight> optics = new List<OpticSight>();
        private float nextOpticLook;
        private readonly List<Renderer> lenses = new List<Renderer>();
        private void HideLenses(bool hide)
        {
            if (!hide)
            {
                foreach (Renderer r in lenses) if (r != null) r.enabled = true;
                lenses.Clear();
                return;
            }
            if (Time.unscaledTime >= nextOpticLook)
            {
                nextOpticLook = Time.unscaledTime + 2;
                optics.Clear();
                if (Dog.Owner != null) Dog.Owner.gameObject.GetComponentsInChildren(false, optics);
            }
            Hide(CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight);
            foreach (OpticSight optic in optics) Hide(optic);
        }

        private void Hide(OpticSight optic)
        {
            Renderer lens = optic != null ? optic.LensRenderer : null;
            if (lens == null || !lens.enabled) return;
            lens.enabled = false;
            lenses.Add(lens);
        }

        // The game lights its scenes partly through its own scripts (the indoor/outdoor ambient light, its light manager),
        // which only serve its main and scope cameras; without them the feed was black silhouettes. Each render they're
        // asked to serve this camera too, as they do the scope's: each builds its own drawing for it, at its size.
        // They're found once, as the raid starts (DogCam comes with the dog); after that the game announces each one that
        // comes or goes, as it does to its own list (OnRenderObjectManager). Searching the map for them every 10 s while
        // the feed showed was a stall each time.
        private readonly List<OnRenderObjectConnector> lighting = new List<OnRenderObjectConnector>();
        private bool lightingFound;
        private void Start() { try { if (Dog != null && Dog.CameraMount() != null) FindLighting(); } catch (Exception) { } }
        private void FindLighting()
        {
            if (lightingFound) return;
            lightingFound = true;
            try
            {
                OnRenderObjectConnector.OnRenderObjectConnectorAdd += LightingAdded;
                OnRenderObjectConnector.OnRenderObjectConnectorRemove += LightingRemoved;
                foreach (OnRenderObjectConnector part in FindObjectsOfType<OnRenderObjectConnector>()) LightingAdded(part);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dog camera: the game's lighting scripts couldn't be found for the feed: " + e.Message); }
        }

        private void Light()
        {
            FindLighting();
            for (int i = 0; i < lighting.Count; i++)
            {
                OnRenderObjectConnector part = lighting[i];
                if (part != null && part.isActiveAndEnabled)
                    try { part.ManualOnRenderObject(cam); } catch (Exception) { }
            }
        }

        private void LightingAdded(OnRenderObjectConnector part) { if (part != null && !lighting.Contains(part)) lighting.Add(part); }
        private void LightingRemoved(OnRenderObjectConnector part) => lighting.Remove(part);

        // In first person your own body isn't drawn whole (your head only casts a shadow); for the dog's camera it is.
        private readonly List<(Renderer renderer, bool enabled, UnityEngine.Rendering.ShadowCastingMode shadows)> body =
            new List<(Renderer, bool, UnityEngine.Rendering.ShadowCastingMode)>();
        // Your renderers, gathered again every 2 s (gear changes) into the same list rather than a new array each render.
        private readonly List<Renderer> yours = new List<Renderer>();
        private float nextBodyLook;
        private void ShowBody(bool show)
        {
            if (!show)
            {
                foreach (var (r, enabled, shadows) in body) if (r != null) { r.enabled = enabled; r.shadowCastingMode = shadows; }
                body.Clear();
                return;
            }
            Player you = Dog.Owner;
            if (you == null) return;
            // The whole player, not just its body: the gear you wear (a face cover, a helmet) hangs off the skeleton.
            if (Time.unscaledTime >= nextBodyLook) { nextBodyLook = Time.unscaledTime + 2; you.gameObject.GetComponentsInChildren(true, yours); }
            foreach (Renderer r in yours)
            {
                if (r == null) continue;
                // Only what the game draws as a shadow alone (your head and head gear in first person); anything it has
                // switched off (lower-detail copies, gear not worn) stays off.
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
                body.Add((r, r.enabled, r.shadowCastingMode));
                r.enabled = true; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }

        // The camera, its texture and the feed on screen; rebuilt when the size or resolution setting changes.
        private void Build()
        {
            int resolution = (int)Quality;
            float size = Plugin.DogCamSize.Value;
            if (overlay != null && resolution == shownResolution && Mathf.Approximately(size, shownSize) && Plugin.DogCamPosition.Value == shownPlace) return;
            shownResolution = resolution; shownSize = size; shownPlace = Plugin.DogCamPosition.Value;
            // High: a pixel for every pixel of the feed on screen (from 640x360 up to 960x540).
            float onScreen = Mathf.Clamp(Screen.height * Mathf.Clamp(size, .12f, .45f), 360, 540);
            Vector2Int res = resolution < Resolutions.Length ? Resolutions[resolution] : new Vector2Int(Mathf.RoundToInt(onScreen * 16 / 9), Mathf.RoundToInt(onScreen));
            if (picture != null) { picture.Release(); Destroy(picture); }
            picture = new RenderTexture(res.x, res.y, 24, RenderTextureFormat.ARGB32) { name = "K9 dog camera", anisoLevel = 0, filterMode = FilterMode.Bilinear };
            if (cam == null)
            {
                var go = new GameObject("K9 dog camera");
                go.transform.SetParent(transform, false);
                // Tagged as the game tags your scope's camera: its clouds, water, wall decals, far trees and area lights are
                // drawn only for cameras tagged so (or your view's), each with drawing built for that camera's own size.
                try { go.tag = "OpticCamera"; }
                catch (Exception e) { Plugin.Log.LogWarning("Dog camera: couldn't tag the feed's camera; it goes without clouds and water: " + e.Message); }
                cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.fieldOfView = 78; cam.nearClipPlane = .06f; cam.farClipPlane = 90;
                cam.allowHDR = false; cam.allowMSAA = false; cam.useOcclusionCulling = true;
                // Deferred like the game's own cameras: its indoor and outdoor ambient light only works that way.
                bool sky = RenderSettings.skybox != null;
                cam.clearFlags = sky ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(.45f, .47f, .5f);
            }
            cam.targetTexture = picture;

            if (overlay == null)
            {
                overlay = new GameObject("K9 dog camera feed");
                DontDestroyOnLoad(overlay);
                var canvas = overlay.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30;
                var frame = Box(overlay.transform, "Frame", new Color(0, 0, 0, .85f));
                shownFrame = frame as RectTransform;
                var feed = new GameObject("Feed", typeof(RectTransform));
                feed.transform.SetParent(frame, false);
                view = feed.AddComponent<RawImage>(); view.raycastTarget = false;
                label = Label(frame, 13, TextAlignmentOptions.TopLeft);
                signal = Label(frame, 22, TextAlignmentOptions.Center);
                signal.text = Lang.T("NO SIGNAL");
            }
            view.texture = picture;
            // 16:9, a share of the screen's height (Dog camera > Size), where Dog camera > Position says (see Place).
            float h = Screen.height * Mathf.Clamp(size, .12f, .45f), w = h * 16 / 9;
            var frameRect = (RectTransform)view.transform.parent;
            Vector2 corner = Plugin.DogCamPosition.Value == DogCamPlace.TopRight ? new Vector2(1, 1) : new Vector2(0, 1);
            frameRect.anchorMin = frameRect.anchorMax = frameRect.pivot = corner;
            frameRect.sizeDelta = new Vector2(w + 6, h + 6);
            placedAt = new Vector2(float.NaN, float.NaN);
            Place();
            Fill((RectTransform)view.transform, 3);
            Fill((RectTransform)label.transform, 8);
            Fill((RectTransform)signal.transform, 0);
            label.fontSize = Mathf.Max(11, h * .075f);
        }

        // By default top left under your health figure (beside it are your status effects); or in a corner. Checked every frame, moved only when it changes.
        private Vector2 placedAt;
        private void Place()
        {
            Vector2 at;
            switch (Plugin.DogCamPosition.Value)
            {
                case DogCamPlace.TopLeft: at = new Vector2(16, -16); break;
                case DogCamPlace.TopRight: at = new Vector2(-16, -16); break;
                default:
                    Vector2 under = HudLayout.UnderHealth;
                    at = new Vector2(under.x, -under.y);
                    break;
            }
            // Dog camera > Move right / Move down, then kept on the screen.
            at += new Vector2(Plugin.DogCamNudgeX.Value * Screen.width, -Plugin.DogCamNudgeY.Value * Screen.height);
            var frame = (RectTransform)view.transform.parent;
            Vector2 size = frame.sizeDelta;
            bool right = Plugin.DogCamPosition.Value == DogCamPlace.TopRight;
            at.x = right ? Mathf.Clamp(at.x, size.x - Screen.width, 0) : Mathf.Clamp(at.x, 0, Screen.width - size.x);
            at.y = Mathf.Clamp(at.y, size.y - Screen.height, 0);
            if (at == placedAt) return;
            placedAt = at;
            frame.anchoredPosition = at;
        }

        private static Transform Box(Transform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>(); image.color = colour; image.raycastTarget = false;
            return go.transform;
        }

        private static TextMeshProUGUI Label(Transform parent, float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size; text.color = new Color(.92f, .92f, .88f); text.alignment = alignment; text.raycastTarget = false;
            text.enableWordWrapping = false; text.richText = true;
            return text;
        }

        private static void Fill(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset); rect.offsetMax = new Vector2(-inset, -inset);
        }

        private void OnDestroy()
        {
            if (lightingFound)
            {
                OnRenderObjectConnector.OnRenderObjectConnectorAdd -= LightingAdded;
                OnRenderObjectConnector.OnRenderObjectConnectorRemove -= LightingRemoved;
            }
            if (overlay != null) Destroy(overlay);
            if (picture != null) { picture.Release(); Destroy(picture); }
            if (grassDepthTexture != null) { grassDepthTexture.Release(); Destroy(grassDepthTexture); }
        }
    }
}
