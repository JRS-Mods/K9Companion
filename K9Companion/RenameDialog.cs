using System;
using System.Collections.Generic;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    internal sealed class RenameDialog : MonoBehaviour
    {
        private static RenameDialog dialog;
        internal static Companion Owner { get; private set; }
        internal static bool IsOpen => dialog != null;
        private CursorLockMode previousLock;
        private bool previousVisible, focusName = true;
        private string draft, error = "";
        private Rect window;

        internal static void PatchInput(Harmony harmony)
        {
            foreach (string name in new[] { "EFT.InputSystem.InputNode", "EFT.InputSystem.InputNodeAbstract" })
            {
                var type = AccessTools.TypeByName(name);
                var method = AccessTools.DeclaredMethod(type, "TranslateInput");
                if (method == null) throw new MissingMethodException(name, "TranslateInput");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RenameDialog), nameof(FilterInput)),
                    postfix: new HarmonyMethod(typeof(RenameDialog), nameof(KeepCursor)));
            }
        }
        private static void FilterInput(List<ECommand> __0, ref float[] __1)
        {
            if (!IsOpen) return;
            __0?.Clear();
            if (__1 != null) Array.Clear(__1, 0, __1.Length);
            // Allow zero axes through so movement is actually stopped, rather than retaining the last vector.
        }
        private static void KeepCursor(ref ECursorResult __2) { if (IsOpen) __2 = ECursorResult.ShowCursor; }

        // From the DOG tab, where there may be no dog in the scene to rename beside.
        internal static void OpenFromMenu() { Open(null); if (dialog != null) dialog.fromMenu = true; }

        internal static void Open(Companion owner)
        {
            if (IsOpen || Input.GetMouseButton(0) || Input.GetMouseButton(1)) return;
            Owner = owner;
            dialog = new GameObject("Name your dog").AddComponent<RenameDialog>();
            dialog.previousLock = Cursor.lockState; dialog.previousVisible = Cursor.visible;
            dialog.draft = Plugin.DogName.Value;
            dialog.window = new Rect((Screen.width - 440) / 2f, (Screen.height - 200) / 2f, 440, 200);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        internal static void Close()
        {
            if (dialog == null) { Owner = null; return; }
            var old = dialog; dialog = null; Owner = null;
            Cursor.lockState = old.previousLock; Cursor.visible = old.previousVisible;
            Destroy(old.gameObject);
        }
        private bool fromMenu;
        private void Update() { if (!fromMenu && (Owner == null || !Owner.isActiveAndEnabled)) Close(); }
        private void LateUpdate() { if (IsOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } }
        private void OnApplicationFocus(bool focused) { if (!focused) Close(); }
        private void OnGUI()
        {
            if (!IsOpen) return;
            GUI.depth = -1000;
            window = GUI.ModalWindow(GetInstanceID(), window, Draw, "Name your dog");
        }
        private void Draw(int id)
        {
            GUI.Label(new Rect(24, 35, 390, 25), "Choose a name (up to 16 characters).");
            GUI.SetNextControlName("K9DogName");
            draft = GUI.TextField(new Rect(24, 65, 390, 32), draft ?? "", 16);
            if (focusName) { GUI.FocusControl("K9DogName"); focusName = false; }
            GUI.Label(new Rect(24, 103, 390, 28), error);
            var ev = Event.current;
            bool save = ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter);
            bool cancel = ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape;
            if (save || cancel) ev.Use();
            save |= GUI.Button(new Rect(220, 145, 94, 32), "Save");
            cancel |= GUI.Button(new Rect(320, 145, 94, 32), "Cancel");
            if (cancel) { Close(); return; }
            if (save)
            {
                string name = DogNaming.Normalize(draft);
                if (name.Length == 0) { error = "Please enter a name."; return; }
                try { Plugin.SaveName(name); Close(); }
                catch (Exception e) { error = "Could not save the name. Please try again."; Plugin.Log.LogError(e); }
            }
        }
    }
}
