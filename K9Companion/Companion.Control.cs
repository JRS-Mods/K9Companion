using System;
using System.Linq;
using System.Reflection;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.CameraControl;
using EFT.Communications;
using EFT.Interactive;
using EFT.UI;
using EFT.UI.Screens;
using EFT.UI.BattleTimer;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace K9Companion
{
    /// <summary>
    /// Carrying on as the dog when you're killed (Combat > Control the dog when you die). The raid's end is held back
    /// while the dog lives: the camera goes behind it, WASD moves it (Shift runs, Ctrl sneaks), the mouse looks round,
    /// Space jumps, left click sends it at the enemy you're looking at. It ends when you press the leave key, when the dog
    /// is killed, or when the dog gets out through an open extract: then its jacket, and what's in it, comes home with it
    /// (DogBag.DogExtracted). The raid always ends as your death, whichever way it ends: your own gear is lost as usual.
    /// With Fika the dog's death screen is Fika's own, held back the same way.
    /// </summary>
    public sealed partial class Companion
    {
        private bool controlled, controlEnding;
        private float menuSeen;
        private Action finishRaid;
        private PlayerCameraController ownerCamera;
        private DogFollowCamera followCamera;
        private float camYaw, camPitch, diedWhileControlled, extractSince;
        private ExfiltrationPoint extractAt;
        private float nextExtractCheck;
        private string controlHint;

        /// <summary>You're dead and playing as the dog.</summary>
        internal static bool Controlling => Raid != null && Raid.controlled;

        // ---- taking over and letting go -------------------------------------------------------------------------------

        // From the raid's death handling: true when the dog takes over (the raid's end waits for 'finish').
        // The dog would take over when you die now (or already has).
        internal static bool WillTakeOver
        {
            get
            {
                Companion dog = Raid;
                return dog != null && (dog.controlled || (Plugin.CarryOnAsDog && dog.dog != null && !dog.dead && !dog.failed && !dog.IsHideout));
            }
        }

        internal static bool TakeOver(Action finish)
        {
            Companion dog = Raid;
            if (dog == null || dog.controlled || !WillTakeOver) return false;
            try { dog.BeginControl(finish); return true; }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't hand you the dog; the raid ends as usual: " + e);
                dog.controlled = false;
                return false;
            }
        }

        private void BeginControl(Action finish)
        {
            controlled = true; controlEnding = false; finishRaid = finish;
            StopFight(); hunt = null; CancelMeal(); actionClip = null; roaming = false;
            command = Command.Follow; following = false; WakeUp();
            camYaw = yaw; camPitch = 14;
            extractAt = null; extractSince = 0; diedWhileControlled = 0;
            Player owner = CurrentPlayer;
            // The game's own camera stops following your body; this puts it behind the dog (LateUpdate).
            ownerCamera = owner != null ? owner.GetComponent<PlayerCameraController>() : null;
            if (ownerCamera != null) ownerCamera.enabled = false;
            followCamera = gameObject.AddComponent<DogFollowCamera>();
            followCamera.Dog = this;
            string name = Plugin.DogName.Value;
            controlHint = Lang.T($"You are {name}: WASD move, Shift run, Ctrl sneak, {Plugin.DogJump.Value} jump, {Plugin.DogDoor.Value} open a door, left click attack, Esc menu, {Plugin.LeaveDog.Value} end the raid.");
            HideYourHud(true);
            NotificationManager.DisplayMessageNotification(Lang.T($"You died. Carry on as {name}: get it to an extract and its gear comes home."), ENotificationDurationType.Long);
            Plugin.Log.LogInfo("You died: carrying on as the dog.");
        }

        // Back to the game's own end of the raid (your death).
        private void EndControl(string why)
        {
            if (!controlled || controlEnding) return;
            controlEnding = true;
            Plugin.Log.LogInfo("Done as the dog: " + why);
            Release();
            Action finish = finishRaid; finishRaid = null;
            try { finish?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError("Ending the raid after playing the dog: " + e); }
        }

        // The camera back to the game, whoever ends the raid (a raid timer running out, the Fika host).
        private void Release()
        {
            HideYourHud(false);
            if (extractAt != null) { ExtractUi(extractAt, false); extractAt = null; }
            if (ownerCamera != null) ownerCamera.enabled = true;
            ownerCamera = null;
            if (followCamera != null) Destroy(followCamera);
            followCamera = null;
            controlled = false;
        }

        internal static void RaidStopping()
        {
            Companion dog = Raid;
            if (dog != null && dog.controlled) { Plugin.Log.LogInfo("The raid ended while you played the dog."); dog.Release(); }
        }

        // ---- each frame -----------------------------------------------------------------------------------------------

        private void TickControlled(float dt, Player owner)
        {
            if (dog == null || owner == null) { EndControl("the dog or your body is gone"); return; }
            // A screen over the raid (the Esc menu, its settings): the mouse is the menu's and the dog stands.
            bool inRaidView = EftScreenManager.Instance == null || EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.BattleUI);
            // (Not on the same Esc that just closed it.)
            if (inRaidView && Input.GetKeyDown(KeyCode.Escape) && Time.unscaledTime - menuSeen > .3f) { OpenRaidMenu(); inRaidView = false; }
            if (!inRaidView)
            {
                menuSeen = Time.unscaledTime;
                speed = 0;
                if (!dead && !Jumping) { Animate(dt); dog.transform.rotation = Quaternion.Euler(0, yaw, 0); }
                return;
            }
            if (Cursor.lockState != CursorLockMode.Locked) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            HideYourHud(true);
            Look();
            if (Plugin.LeaveDog.Value.IsDown()) { EndControl("you left"); return; }
            if (dead)
            {
                TickCombat(dt, owner);
                if (diedWhileControlled == 0) diedWhileControlled = Time.time;
                if (Time.time - diedWhileControlled > 3.5f) EndControl("the dog was killed");
                return;
            }
            if (TickCombat(dt, owner)) return;
            if (Jumping) { TickJump(dt); return; }
            TickExtract(dt, owner);
            if (controlEnding) return;
            if (TickBite(dt)) return;
            // A fight it was sent into goes on until you move it yourself; once over, the dog is yours again (it doesn't
            // go back to your body).
            Vector3 wanted = Steering(out bool run, out bool sneak);
            if (attackTarget != null && wanted.sqrMagnitude > .01f) StopFight();
            if (attackTarget == null && brain.State != CombatState.Idle) StopFight();
            if (Input.GetMouseButtonDown(0)) SendAtLookedAt(owner);
            if (Input.GetKeyDown(KeyCode.O)) ShowExtracts();
            if (Plugin.DogDoor.Value.IsDown()) OpenDoorAhead();
            if (Plugin.DogJump.Value.IsDown() && stance == Stance.Stand)
            {
                Vector3 ahead = wanted.sqrMagnitude > .01f ? wanted : Quaternion.Euler(0, camYaw, 0) * Vector3.forward;
                JumpNow(ahead);
                return;
            }
            crouching = sneak;
            Vector3 start = dog.transform.position;
            Tuning tuning = Plugin.Tuning;
            float fitness = NeedsFactor * jacketSpeed;
            tuning.Walk *= fitness; tuning.Run *= fitness; tuning.Sprint *= fitness;
            float desired;
            if (attackTarget != null) desired = Chase(start, dt, tuning);
            else
            {
                desired = 0;
                if (wanted.sqrMagnitude > .01f)
                {
                    WakeUp();
                    float face = Mathf.Atan2(wanted.x, wanted.z) * Mathf.Rad2Deg;
                    float off = Mathf.DeltaAngle(yaw, face);
                    yawRate = Mathf.Clamp(off * 6f, -400, 400);
                    yaw += yawRate * dt;
                    desired = (sneak ? Locomotion.StalkSpeed(tuning) : run ? Locomotion.Speed(Gait.Run, tuning) : Locomotion.Speed(Gait.Trot, tuning))
                              * Mathf.Lerp(1f, .25f, Mathf.InverseLerp(30, 120, Mathf.Abs(off)));
                }
                else yawRate = 0;
                speed = Mathf.MoveTowards(speed, desired, (desired < speed ? 2.5f : 1.2f) * Accel * dt);
                if (stance != Stance.Stand) speed = 0;
                if (speed > .01f) Walk(speed * dt);
            }
            Vector3 moved = dog.transform.position - start; moved.y = 0;
            shownSpeed = Mathf.Lerp(shownSpeed, dt > 0 ? moved.magnitude / dt : 0, Mathf.Min(1, 10 * dt));
            if (shownSpeed < .15f && wanted.sqrMagnitude < .01f) idle += dt; else idle = 0;
            lastProgress = Time.time; progressPoint = dog.transform.position;
            TickBody(dog.transform.position, dt);
            Animate(dt, tuning);
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
        }

        private void Look()
        {
            camYaw += Input.GetAxis("Mouse X") * 2.4f;
            camPitch = Mathf.Clamp(camPitch - Input.GetAxis("Mouse Y") * 2f, -15, 65);
        }

        // Where the keys say to go, flat, relative to where the camera looks.
        private Vector3 Steering(out bool run, out bool sneak)
        {
            float forward = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            float side = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            sneak = !run && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C));
            Quaternion turn = Quaternion.Euler(0, camYaw, 0);
            Vector3 v = turn * Vector3.forward * forward + turn * Vector3.right * side;
            return v.sqrMagnitude > 1 ? v.normalized : v;
        }

        // A step along the dog's heading: on the navmesh it slides along the edge rather than off it; off it, over the
        // floor as players walk it (stairs too).
        private void Walk(float step)
        {
            Vector3 pos = dog.transform.position;
            Vector3 heading = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            Vector3 next = pos + heading * step;
            if (NavMesh.SamplePosition(pos, out NavMeshHit on, .6f, NavMesh.AllAreas))
            {
                if (NavMesh.Raycast(on.position, on.position + heading * step, out NavMeshHit edge, NavMesh.AllAreas))
                {
                    // Along the edge, as much as the heading has along it.
                    Vector3 along = heading - Vector3.Project(heading, new Vector3(edge.normal.x, 0, edge.normal.z).normalized);
                    next = edge.position + along * step * .9f;
                    if (NavMesh.Raycast(edge.position, next, out NavMeshHit stop, NavMesh.AllAreas)) next = stop.position;
                    // Off the edge of the mesh may still be floor (it doesn't cover everything you can walk).
                    if (Flat(next, pos) < step * .2f && Walkable(pos, pos + heading * step, true)) next = pos + heading * step;
                }
            }
            else if (!Walkable(pos, next, true)) return;
            // The navmesh runs through doorways whether the door is open or not: a shut one stops it.
            if (DoorInTheWay(pos, next, out _)) return;
            Place(next, true);
        }

        // ---- doors, the extracts, your HUD ------------------------------------------------------------------------------

        // A door that isn't open, in the way of the dog's body between two points.
        internal static bool DoorInTheWay(Vector3 from, Vector3 to, out Door door)
        {
            door = null;
            Vector3 d = to - from; d.y = 0;
            float length = d.magnitude;
            if (length < .0001f) return false;
            foreach (RaycastHit hit in Physics.CapsuleCastAll(from + Vector3.up * (.25f + DogRadius), from + Vector3.up * (.9f - DogRadius), DogRadius, d / length,
                                                              length + .05f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance <= 0) continue;   // already against it: moving off it is allowed
                Door found = hit.collider.GetComponentInParent<Door>();
                if (found != null && found.DoorState != EDoorState.Open) { door = found; return true; }
            }
            return false;
        }

        private static bool doorErrorLogged;

        // F: noses open the door in front of it, if it isn't locked.
        private void OpenDoorAhead()
        {
            Vector3 pos = dog.transform.position, ahead = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            if (!DoorInTheWay(pos, pos + ahead * 1.2f, out Door door))
            {
                // Or one beside it, within a metre.
                foreach (Collider c in Physics.OverlapSphere(pos + Vector3.up * .5f + ahead * .5f, 1f, ~0, QueryTriggerInteraction.Ignore))
                    if ((door = c.GetComponentInParent<Door>()) != null && door.DoorState != EDoorState.Open) break;
                    else door = null;
            }
            if (door == null) { Say("No door in front of " + Plugin.DogName.Value + "."); return; }
            if (door.DoorState == EDoorState.Locked) { Say("That door is locked."); return; }
            if (door.DoorState != EDoorState.Shut) return;
            audio?.Play("sniff");
            // The door's own Open (it swings open as for a player). Not Door.Interact: SAIN hooks that for door sounds and
            // expects a player doing it, and threw with the dog.
            try { door.Open(); return; }
            catch (Exception e) { if (!doorErrorLogged) { doorErrorLogged = true; Plugin.Log.LogWarning("Opening a door as the dog the game's way failed; swinging it open: " + e); } }
            // As the game's own Lock() sets a door: its state, then its angle.
            try
            {
                door.DoorState = EDoorState.Open;
                float angle = (float)AccessTools.Method(typeof(WorldInteractiveObject), "GetAngle", new[] { typeof(EDoorState) }).Invoke(door, new object[] { EDoorState.Open });
                AccessTools.Property(typeof(WorldInteractiveObject), "CurrentAngle").SetValue(door, angle);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Opening a door as the dog: " + e); Say("That door won't open."); }
        }

        // Esc: the game's raid menu (settings, leave), as when you're alive; the game stops taking your keys once you're
        // dead. Its Leave ends playing the dog, the raid ending as your death.
        private void OpenRaidMenu()
        {
            try
            {
                var menu = new MenuScreen.RaidMainMenuScreenController();
                menu.OnLeave += () => EndControl("you left from the menu");
                menu.ShowScreen(EScreenState.Queued);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Opening the raid menu as the dog: " + e.Message); }
        }

        // O: the raid time and the extracts, the game's own panel.
        private static void ShowExtracts()
        {
            try { MonoBehaviourSingleton<GameUI>.Instance?.TimerPanel?.ShowTimer(showExits: true, updateExits: true); }
            catch (Exception e) { Plugin.Log.LogWarning("Showing the extracts: " + e.Message); }
        }

        // Your quick slots, stance, ammo and body figure stay up after you die; they're not the dog's. Faded out while you
        // play it (each frame, as the game fades some of them in on its own), back when it ends. The timers and the
        // extracts (O) are another panel and stay.
        private static readonly System.Collections.Generic.List<CanvasGroup> hudFaded = new System.Collections.Generic.List<CanvasGroup>();
        private static void HideYourHud(bool hide)
        {
            try
            {
                if (!hide)
                {
                    foreach (CanvasGroup g in hudFaded) if (g != null) g.alpha = 1;
                    hudFaded.Clear();
                    return;
                }
                EftBattleUIScreen screen = MonoBehaviourSingleton<CommonUI>.Instantiated ? MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen : null;
                if (screen == null) return;
                if (hudFaded.Count == 0)
                    foreach (Component panel in new Component[] { screen._characterHealthPanel, screen._battleStancePanel, screen._quickAccessPanel, screen._ammoCountPanel, screen._gesturesPanel })
                        if (panel != null) hudFaded.Add(panel.GetComponent<CanvasGroup>() ?? panel.gameObject.AddComponent<CanvasGroup>());
                foreach (CanvasGroup g in hudFaded) if (g != null) g.alpha = 0;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hiding your HUD for the dog: " + e.Message); }
        }

        // After the enemy you sent it at: on the plain path, flat out, braking into the bite.
        private float Chase(Vector3 start, float dt, Tuning tuning)
        {
            Vector3 target = attackTarget.Transform.position;
            float distance = Vector3.Distance(start, target);
            if (Time.time >= nextPath && (corner >= corners.Length || (target - pathTarget).sqrMagnitude > 1 || Time.time >= pathExpires)) RefreshPath(target);
            float left = Mathf.Max(0, (corner < corners.Length ? PathLeft(start) : distance) - ChaseStop);
            float desired = Mathf.Min(Locomotion.Speed(Gait.Sprint, tuning), Mathf.Sqrt(2 * HardBraking * left));
            if (stance == Stance.Stand) Steer(start, dt);
            desired *= Mathf.Lerp(1f, .35f, Mathf.InverseLerp(20, 100, Mathf.Abs(headingError)));
            speed = Mathf.MoveTowards(speed, desired, Accel * dt);
            if (stance != Stance.Stand) speed = 0;
            if (speed > .01f) Move(Mathf.Min(speed * dt, Mathf.Max(0, distance - ChaseStop)));
            return desired;
        }

        // Left click: the enemy nearest the middle of the screen (within a few degrees, 60 m), even out of sight.
        private void SendAtLookedAt(Player owner)
        {
            if (!rules.CanAttack) { Say("Combat mode is off: " + Plugin.DogName.Value + " can't attack."); return; }
            Camera view = CameraManager.Instance?.Camera;
            if (view == null || world == null) return;
            Player best = null; float bestAngle = 8f;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                Vector3 to = Body(p) - view.transform.position;
                if (to.magnitude > 60) continue;
                float angle = Vector3.Angle(view.transform.forward, to);
                if (angle >= bestAngle || !Reachable(p.Transform.position)) continue;
                best = p; bestAngle = angle;
            }
            if (best == null) { Say("Look at an enemy to attack it."); return; }
            Engage(best, true);
            audio?.Play("bark");
            Say($"{Plugin.DogName.Value} goes for {best.Profile.Nickname}.");
        }

        // Standing in an open extract (one you could have used) for its usual time: the dog is out, with its gear.
        private void TickExtract(float dt, Player owner)
        {
            if (Time.time < nextExtractCheck && extractAt == null) return;
            nextExtractCheck = Time.time + .25f;
            ExfiltrationPoint here = null;
            try
            {
                ExfiltrationController exits = ExfiltrationController.Instance;
                Vector3 body = dog.transform.position + Vector3.up * .3f;
                foreach (ExfiltrationPoint point in exits?.EligiblePoints(owner.Profile) ?? Array.Empty<ExfiltrationPoint>())
                {
                    if (point == null || point.Status != EExfiltrationStatus.RegularMode) continue;
                    Collider zone = point.GetComponent<Collider>();
                    if (zone != null && (zone.ClosestPoint(body) - body).sqrMagnitude < .0001f) { here = point; break; }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Checking the extracts for the dog: " + e.Message); nextExtractCheck = Time.time + 5; }
            if (here != extractAt)
            {
                ExtractUi(extractAt, false);
                extractAt = here; extractSince = Time.time;
                ExtractUi(here, true);
                Plugin.Log.LogInfo(here != null ? $"The dog is in the extract {here.Settings.Name} ({here.Settings.ExfiltrationTime:0} s)." : "The dog left the extract.");
            }
            if (extractAt == null) return;
            float needed = Mathf.Max(3f, extractAt.Settings.ExfiltrationTime);
            if (Time.time - extractSince < needed) return;
            string name = Plugin.DogName.Value;
            DogBag.DogExtracted();
            NotificationManager.DisplayMessageNotification(Lang.T($"{name} made it out, with its gear."), ENotificationDurationType.Long);
            EndControl("the dog got out at " + extractAt.Settings.Name);
        }

        // In an extract, as for you alive: the green countdown banner, and its line in the extracts list lit green. Out of
        // it (or done), the banner goes and the list is as it was.
        private void ExtractUi(ExfiltrationPoint point, bool inside)
        {
            if (point == null) return;
            try
            {
                GameUI ui = MonoBehaviourSingleton<GameUI>.Instantiated ? MonoBehaviourSingleton<GameUI>.Instance : null;
                if (ui == null) return;
                string name = point.Settings.Name;
                if (inside)
                {
                    float needed = Mathf.Max(3f, point.Settings.ExfiltrationTime);
                    ui.BattleUiPanelExitTrigger?.Show(needed);
                    ui.TimerPanel?.UpdateExfiltrationTimers(point, true, true, false, needed, point.Status);
                    ui.TimerPanel?.SetMainTimerState(name, EMainTimerState.StayInEpInside);
                    ui.TimerPanel?.SwitchTimers(name, true);
                }
                else
                {
                    ui.BattleUiPanelExitTrigger?.Close();
                    ui.TimerPanel?.SetMainTimerState(name, EMainTimerState.FindEp);
                    ui.TimerPanel?.SwitchTimers(null, false);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Showing the extract for the dog: " + e.Message); }
        }

        // Your death closes your eyes on the main camera (the game's death fade to black, and its blur): the dog's eyes are
        // open. Held off for as long as you play the dog.
        private DeathFade deathFade;
        private FastBlur deathBlur;
        private void ClearDeathView(Camera view)
        {
            if (deathFade == null) deathFade = view.GetComponent<DeathFade>();
            if (deathBlur == null) deathBlur = view.GetComponent<FastBlur>();
            if (deathFade != null && deathFade.enabled) { deathFade.DisableEffect(); deathFade.enabled = false; }
            if (deathBlur != null && deathBlur.enabled) deathBlur.enabled = false;
        }

        // Behind the dog, a little above, pulled in where a wall would be in the way.
        internal void PlaceCamera()
        {
            Camera view = CameraManager.Instance?.Camera;
            if (view == null || dog == null) return;
            ClearDeathView(view);
            float s = Mathf.Max(.3f, Plugin.Scale.Value);
            Vector3 pivot = dog.transform.position + Vector3.up * (.8f * s);
            Quaternion look = Quaternion.Euler(camPitch, camYaw, 0);
            Vector3 back = look * Vector3.back;
            float distance = 2.8f * s;
            // Pulled in front of anything solid in between (pipes and beams too, which aren't all on the dog's own layers).
            foreach (RaycastHit hit in Physics.SphereCastAll(pivot, .2f, back, distance, ~0, QueryTriggerInteraction.Ignore))
                if (hit.distance > 0 && !Ignored(hit.collider))
                    distance = Mathf.Min(distance, Mathf.Max(.4f, hit.distance - .05f));
            view.transform.SetPositionAndRotation(pivot + back * distance, look);
        }

        private void OnControlGUI()
        {
            if (!controlled || controlEnding) return;
            var content = new GUIContent(controlHint);
            Vector2 size = GUI.skin.box.CalcSize(content);
            GUI.Box(new Rect((Screen.width - size.x - 24) / 2, Screen.height * .04f, size.x + 24, size.y + 8), content);
        }
    }

    /// <summary>The camera behind the dog you're playing: placed after everything else has moved this frame.</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class DogFollowCamera : MonoBehaviour
    {
        internal Companion Dog;
        private void LateUpdate() { if (Dog != null) Dog.PlaceCamera(); }
    }

    /// <summary>
    /// Where the raid ends on your death: the game's (SPT) and Fika's. While the dog is yours, those wait; and a raid
    /// that ends anyway (the timer) still ends as your death.
    /// </summary>
    internal static class DogControlPatches
    {
        private static bool passing;

        internal static void Patch(Harmony harmony)
        {
            MethodInfo stopping = AccessTools.Method(typeof(BaseLocalGame<EftGamePlayerOwner>), nameof(BaseLocalGame<EftGamePlayerOwner>.InitiateGameStopping))
                ?? throw new MissingMethodException("BaseLocalGame", "InitiateGameStopping");
            harmony.Patch(stopping, prefix: new HarmonyMethod(typeof(DogControlPatches), nameof(BeforeDeathScreen)));
            MethodInfo stop = AccessTools.Method(typeof(BaseLocalGame<EftGamePlayerOwner>), nameof(BaseLocalGame<EftGamePlayerOwner>.Stop))
                ?? throw new MissingMethodException("BaseLocalGame", "Stop");
            harmony.Patch(stop, prefix: new HarmonyMethod(typeof(DogControlPatches), nameof(BeforeStop)));
            // Fika's raid has its own death handling (and its own Stop, which calls the game's).
            Type coop = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame");
            MethodInfo died = coop != null ? AccessTools.Method(coop, "HealthController_DiedEvent") : null;
            if (died != null) harmony.Patch(died, prefix: new HarmonyMethod(typeof(DogControlPatches), nameof(BeforeFikaDeath)));
            else if (coop != null) Plugin.Log.LogWarning("Fika's death handling wasn't found: controlling the dog after you die won't work with this Fika.");
            MethodInfo coopStop = coop != null ? AccessTools.Method(coop, "Stop") : null;
            if (coopStop != null) harmony.Patch(coopStop, prefix: new HarmonyMethod(typeof(DogControlPatches), nameof(BeforeStop)));
            // Fika's spectator camera (with its fade to black) starts from its own handler of your death: it waits too.
            Type freecam = AccessTools.TypeByName("Fika.Core.Main.FreeCamera.FreeCameraController");
            MethodInfo spectate = freecam != null ? AccessTools.Method(freecam, "MainPlayer_DiedEvent") : null;
            if (spectate != null) harmony.Patch(spectate, prefix: new HarmonyMethod(typeof(DogControlPatches), nameof(BeforeFikaSpectator)));
        }

        // Fika's death handling held back while you play the dog, run once the dog's part is over.
        private static readonly System.Collections.Generic.List<Action> heldBack = new System.Collections.Generic.List<Action>();

        private static bool BeforeFikaSpectator(object __instance, EDamageType obj, MethodBase __originalMethod)
        {
            if (passing || !Companion.WillTakeOver) return true;
            heldBack.Add(() => __originalMethod.Invoke(__instance, new object[] { obj }));
            return false;
        }

        private static void RunHeldBack()
        {
            var held = heldBack.ToList(); heldBack.Clear();
            foreach (Action a in held) Pass(a);
        }

        private static bool BeforeDeathScreen(BaseLocalGame<EftGamePlayerOwner> __instance)
        {
            if (passing) return true;
            return !Companion.TakeOver(() => Pass(__instance.InitiateGameStopping));
        }

        // With Fika a death doesn't end the raid by itself: you spectate and leave with its extract key. When the dog's part is
        // over Fika's own handling runs as it would have; on your own the raid then ends at once (as that key would end it), in a
        // group you spectate the others as usual.
        private static bool BeforeFikaDeath(object __instance, EDamageType damageType, MethodBase __originalMethod)
        {
            if (passing) return true;
            return !Companion.TakeOver(() =>
            {
                Pass(() => __originalMethod.Invoke(__instance, new object[] { damageType }));
                RunHeldBack();
                if (!DogNet.InGroup && __instance is BaseLocalGame<EftGamePlayerOwner> game) Plugin.Runner.StartCoroutine(EndSoon(game));
            });
        }

        private static System.Collections.IEnumerator EndSoon(BaseLocalGame<EftGamePlayerOwner> game)
        {
            yield return new WaitForSeconds(1f);
            if (game == null || game.Status != GameStatus.Started) yield break;
            Player me = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null) yield break;
            Plugin.Log.LogInfo("Ending the raid (your death) after playing the dog.");
            game.Stop(me.ProfileId, ExitStatus.Killed, null);
        }

        private static void Pass(Action original)
        {
            passing = true;
            try { original(); }
            finally { passing = false; }
        }

        // However the raid ends while you play the dog, it ends as your death.
        private static void BeforeStop(ref ExitStatus exitStatus)
        {
            heldBack.Clear();
            if (!Companion.Controlling) return;
            Companion.RaidStopping();
            exitStatus = ExitStatus.Killed;
        }
    }
}
