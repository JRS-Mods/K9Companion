using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace K9Companion
{
    /// <summary>
    /// The DOG tab's DOGS panel: every dog you have, which one comes on raids (the active dog, the one the rest of the tab
    /// shows), its coat and name, and the pet cards in your stash to adopt from. Out of raids only.
    /// </summary>
    internal sealed partial class DogScreen
    {
        private ItemsPanel itemsPanel;
        private readonly List<GameObject> packPanel = new List<GameObject>();
        private TextMeshProUGUI packStatus;
        private static bool reopenWithDogs;

        // The DOGS button, beside RENAME.
        private void BuildPackButton()
        {
            if (!AtHome) return;
            var (button, label) = ButtonWithLabel("DOGS", new Vector2(LeftX + LeftW - 92 - 100, -10), () => { if (packPanel.Count > 0) ClosePack(); else OpenPack(); }, new Vector2(92, 24));
            label.fontSize = 11.5f * s;
            label.text = Lang.T(Pack.Dogs.Count > 1 ? "DOGS (" + Pack.Dogs.Count + ")" : "DOGS");
            if (reopenWithDogs) { reopenWithDogs = false; OpenPack(); }
        }

        // The tab drawn again, after the active dog changed (its name, picture, gear and kennel are another dog's).
        private void Reopen(bool withDogs)
        {
            InventoryScreen sc = screen; ItemsPanel it = itemsPanel; ItemContext ctx = context; Profile pr = profile;
            var hidden = keepHidden.ToList();
            reopenWithDogs = withDogs;
            Close();
            Open(sc, it, ctx, pr, hidden);
        }

        private void ClosePack()
        {
            foreach (GameObject go in packPanel) if (go != null) Destroy(go);
            packPanel.Clear();
            packStatus = null;
        }

        private void OpenPack()
        {
            ClosePack();
            int first = transform.childCount;
            const float top = -40, width = RightX + RightW - LeftX, rowH = 66;
            // Over everything else, catching the clicks meant for what's underneath.
            RectTransform cover = Child("Dogs cover", new Vector2(LeftX - 8, top + 4), new Vector2(width + 16, 700));
            var block = cover.gameObject.AddComponent<Image>(); block.color = new Color(.02f, .025f, .03f, .97f); block.raycastTarget = true;
            Panel("Your dogs", new Vector2(LeftX, top), new Vector2(width, 690));
            Text("One dog comes on raids with you; the others live at their own kennels in your hideout. Each keeps its own name, needs, health, gear, bed, training and kennel.",
                12, FontStyles.Italic, Soft, new Vector2(LeftX + 10, top - 26), new Vector2(width - 20, 34));
            float y = top - 66;
            foreach (PackDog dog in Pack.Dogs)
            {
                PackRow(dog, y, width);
                y -= rowH;
            }

            // Adoption papers in your stash.
            y -= 6;
            var cards = DogPantry.AtHome(profile).Where(i => Pack.CardBreed(i) != null).ToList();
            Text("ADOPTION PAPERS", 13, FontStyles.Bold, Accent, new Vector2(LeftX + 10, y), new Vector2(200, 18));
            y -= 24;
            if (cards.Count == 0)
                Text(K9.QuestDone(profile, K9.TrustIsEarned) ? "No adoption papers in your stash. Dr. Yang has papers for each breed, in each of its coats: one dog of each breed."
                                                             : "Dr. Yang has adoption papers once you have your first dog (Trust Is Earned).",
                    12, FontStyles.Normal, Soft, new Vector2(LeftX + 10, y), new Vector2(width - 20, 20));
            float x = LeftX + 10;
            foreach (Item card in cards.GroupBy(c => Pack.CardBreed(c) + Pack.CardCoat(c)).Select(g => g.First()))
            {
                Item c = card;
                Breed breed = Breed.Get(Pack.CardBreed(c));
                string coat = breed.CoatName(Pack.CardCoat(c));
                // One dog of each breed: papers for a breed you have are only good for throwing away or selling back.
                if (Pack.Dogs.Any(d => d.BreedId == breed.Id))
                    Text($"{breed.Name} ({coat}): you have a {breed.Name} already", 11, FontStyles.Italic, Soft, new Vector2(x, y - 6), new Vector2(200, 30));
                else ButtonWithLabel($"ADOPT {breed.Name.ToUpperInvariant()}  <size=75%>{coat.ToUpperInvariant()}</size>", new Vector2(x, y), () => Adopt(c), new Vector2(200, 30)).Item2.richText = true;
                x += 208;
                if (x > LeftX + width - 210) { x = LeftX + 10; y -= 36; }
            }
            packStatus = Text("", 13, FontStyles.Italic, new Color(.9f, .75f, .5f), new Vector2(LeftX + 10, top - 650), new Vector2(width - 140, 22));
            ButtonWithLabel("CLOSE", new Vector2(LeftX + width - 110, top - 646), ClosePack, new Vector2(100, 28));
            for (int i = first; i < transform.childCount; i++) packPanel.Add(transform.GetChild(i).gameObject);
        }

        private void PackRow(PackDog dog, float y, float width)
        {
            Breed breed = dog.Breed;
            var back = Child("Dog row", new Vector2(LeftX + 6, y), new Vector2(width - 12, 60)).gameObject.AddComponent<Image>();
            back.color = dog.Active ? new Color(.16f, .17f, .12f, .9f) : new Color(.1f, .105f, .11f, .9f); back.raycastTarget = false;
            string name = dog.Shown.ToUpperInvariant() + (dog.Shown != breed.Name ? "  <size=70%><color=#9a9a94>" + breed.Name + "</color></size>" : "");
            var title = Text(name, 17, FontStyles.Bold, dog.Active ? Accent : Ink, new Vector2(LeftX + 16, y - 6), new Vector2(360, 26));
            title.richText = true;
            float health = dog.Active ? (NeedsStore.Current?.Health ?? 100) : (float?)dog.Needs?["health"] ?? 100;
            string kennel = dog.Kennel <= 0 ? "no kennel yet" : K9.KennelNames[Mathf.Clamp(dog.Kennel, 1, 3) - 1];
            string state = dog.Active ? "comes on raids" : "at its kennel";
            string coat = breed.IsShepherd ? "" : "  ·  " + breed.CoatName(dog.Coat);
            var line = Text($"{state}  ·  {Growth.Describe(dog)}  ·  {kennel}{(dog.Building > 0 ? " (building)" : "")}  ·  health {Mathf.RoundToInt(health)}%{coat}",
                12, FontStyles.Normal, Soft, new Vector2(LeftX + 16, y - 34), new Vector2(520, 20));
            line.enableWordWrapping = false;
            float bx = LeftX + width - 12 - 3 * 128;
            if (dog.Active) Text("ACTIVE DOG", 13, FontStyles.Bold, Good, new Vector2(bx + 14, y - 20), new Vector2(120, 20));
            else ButtonWithLabel("TAKE ON RAIDS", new Vector2(bx, y - 14), () => SwitchTo(dog), new Vector2(122, 32));
            // Given back to Dr. Yang (its gear to your stash), so you can take another of its breed: asked twice.
            if (!dog.Active && !breed.IsShepherd)
            {
                bool asking = releaseAsked == dog.Id;
                ButtonWithLabel(asking ? "SURE? GIVE BACK" : "GIVE BACK", new Vector2(bx + 128, y - 14), () => Release(dog), new Vector2(122, 32));
            }
            if (dog.Active) ButtonWithLabel("RENAME", new Vector2(bx + 256, y - 14), () => { ClosePack(); RenameDialog.OpenFromMenu(); }, new Vector2(122, 32));
        }

        private void PackSays(string text) { if (packStatus != null) packStatus.text = Lang.T(text); }

        private void SwitchTo(PackDog dog)
        {
            if (busy || Pack.Switching) { PackSays("One thing at a time."); return; }
            busy = true;
            PackSays("Changing dogs...");
            Pack.Switch(Controller, profile, dog, problem =>
            {
                busy = false;
                if (problem != null && Pack.Active?.Id != dog.Id) { PackSays(problem); return; }
                if (problem != null) NotificationManagerClass_Show(problem);
                EFT.Communications.NotificationManager.DisplayMessageNotification(dog.Shown + " is your active dog now: it comes on raids with you.");
                Reopen(true);
            });
        }

        private static void NotificationManagerClass_Show(string text) => EFT.Communications.NotificationManager.DisplayWarningNotification(text);

        private string releaseAsked;

        private void Release(PackDog dog)
        {
            if (busy) return;
            if (releaseAsked != dog.Id)
            {
                releaseAsked = dog.Id;
                OpenPack();
                PackSays($"Give {dog.Shown} back to Dr. Yang? Its gear goes to your stash, and its breed's papers are back on his shelf. Click again to give it back.");
                return;
            }
            releaseAsked = null;
            busy = true;
            PackSays("Giving " + dog.Shown + " back...");
            Pack.Release(Controller, profile, dog, problem =>
            {
                busy = false;
                if (problem != null) { OpenPack(); PackSays(problem); return; }
                EFT.Communications.NotificationManager.DisplayMessageNotification(dog.Shown + " has gone back to Dr. Yang.");
                OpenPack();
                PackSays(dog.Shown + " has gone back to Dr. Yang. Its breed's papers are on his shelf again.");
            });
        }

        private void Adopt(Item card)
        {
            if (busy) return;
            busy = true;
            Breed breed = Breed.Get(Pack.CardBreed(card));
            PackSays("Adopting...");
            Pack.Adopt(Controller, card, problem =>
            {
                busy = false;
                if (problem != null) { PackSays(problem); return; }
                EFT.Communications.NotificationManager.DisplayMessageNotification("A young " + breed.Name + " is yours: it lives at its own kennel in your hideout.");
                OpenPack();
                PackSays("Welcome home. It's young: it grows with every day you have it and every raid it comes back from. Make it your active dog to take it on raids.");
            });
        }
    }
}
