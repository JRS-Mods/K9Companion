# Changelog

## 1.5.1

Install over 1.5.0 (client and server). With Fika, everyone updates, a headless host too, as the version is the same throughout.

### Fixed

- **Someone the dog killed could be left standing or walking off, with no body to loot**, and shooting them did nothing. In co-op it was only so for the dog's owner, and the others saw the body. An error while the kill was being counted (a mod that expects every kill to have a weapon, for one; a bite has none) stopped the death before the body was made. The death now finishes whatever goes wrong there, and the log says where the error came from.
- **Your dogs, their kennels and K9 progress could be lost** (server). The game sends some of the dog's requests together, when it dies for one, and the server could read the K9 file while it was half written, take it for a new player's and save that over it. The file is now written whole and one request at a time, and a file that can't be opened is left alone.
- **Adoption papers sold out for everyone** (server). Each paper's stock of one was shared by every player on the server until Dr. Yang restocked. The limit of one is now each player's own.
- **Playing the dog after you die, it couldn't attack far from your body**: it dropped the attack the moment it was sent. Fights are now measured from the dog.
- **The raid could never end** if the dog stopped with an error while you were playing it. The raid now ends as your death.
- **No dog on a Fika client** in the raid after one you'd left it out of (TAKE DOG off) ("No nearby navigation surface").
- **A hungry dog away from you** had your hand reach out, and asked to be called over, every three seconds. It now eats from its own jacket where it is.
- **Coming home from a raid healed the dog for the time it spent in the raid.** That time no longer counts as rest.
- **Opening the DOG tab in a raid put back the jacket's wear.**
- **The DOG tab showed every dog with the shepherd's health and nose** at home. It now goes by the dog's breed and how grown it is.

## 1.5.0

Needs SPT 4.1.x and now **Hideout Extensions 1.3.0 or later** (client and server). Install over 1.4.0 (client and server). With Fika, everyone updates, as the version is the same throughout.

### Added

- **More dogs.** Five new breeds besides the German shepherd: Rottweiler, Siberian Husky, Beagle, Shiba Inu and Pug, each in three coats. Once you've finished Trust Is Earned, Dr. Yang sells adoption papers for each breed in each of its coats (15 in all, papers in your stash). You can have one dog of each breed: once you have one, or are holding its papers, that breed's papers are off his shelf. Throw the papers away and they're back. Every dog is its own: its name, needs, health, gear, training and its own kennel levels. They all fight, sniff out loot and wear the gear, with stats that go with their size (a Rottweiler takes more killing and bites harder; a Beagle smells loot further off). An unnamed dog goes by its breed's name until you name it.
- **The new breeds look and move like real dogs.** Grown, each stands a little over a real dog of its breed. Their coats have full-resolution surface detail, shading in the folds, and the fur strands their models come with along the belly, legs, rump and tail. Each breed steps at its own natural rate (small dogs a little quicker than big ones), jumps as gravity says, and runs up to a low wall and jumps it rather than going the long way round.
- **The DOGS panel** on the DOG tab lists your dogs and changes which one is active. The last dog's jacket and collar go into its own holder, and the new one puts on its own. Only the active dog comes on raids. **GIVE BACK** returns an adopted dog to Dr. Yang (its gear goes to your stash) so you can take a different coat of that breed. The K9 unit's shepherd stays.
- **Dogs grow up.** A dog from adoption papers comes home young: about 60% of its grown size, health and bite, and slower. It grows with every day you have it and every raid it comes back from (grown after 30 days, or 20 raids survived, sooner with both). Dogs you had before, and the K9 unit's shepherd, are grown. The DOGS panel shows how far along each one is.
- **Your other dogs live in the hideout**, each at its own kennel (at that dog's own kennel level). They lie on their beds, doze when nobody's about, and get up to watch you when you come close. The K9 Kennel screen builds the active dog's kennel, and each dog's kennel is built separately.
- **All the kennels together.** Your dogs' kennels stand in a group round the active dog's kennel (the paw on the overview), in the same room as it. Drag the paw and the whole group follows.
- **The K9 kennels room.** A 12.5 x 12.5 m room through the north wall of Hideout Extensions' east hall, built like its other rooms (150,000 roubles, 3 nails, 3 bolts, 1 hour). Once it's built all your dogs live there, the active one too, each at its own spot along the walls. A kennel you've moved by hand stays where you put it. This is why K9 Companion now needs Hideout Extensions.
- **Found-in-raid kennel materials** (F12, Hideout). On: a kennel level takes found-in-raid materials only (roubles aside), as the game's hardcore hideout does. Off by default. The server's owner can fix it for everyone with `kennelFoundInRaid` in the server mod's `config.jsonc`, and then the server refuses anything else.

### Changed

- **Every dog has its bed and bowls from the start.** The kennel's levels add the rest; before, the bed only came with the first level.
- **Kennel lamps follow the hideout's light switch** like its own lamps: off with the lights off or on candles, a tube's light with fluorescent tubes, a bulb's with bulbs and the holiday lights.
- **A shorter F12 list.** Most settings are now under F12's "Advanced settings". Shown without it: the dog on or off, solo raids only, combat mode, aggression, god mode, hunger and thirst, the hideout dog, found-in-raid kennel materials, the health bar, the scrolling tab bar, sound and its volume, and the recall, stay and dog camera keys. Nothing is reset: every setting keeps its value.
- **The scrolling tab bar can be turned off**: Display > Scrolling tab bar. Use this if another mod already scrolls the tabs on every screen. The DOG tab is still added, and the bar is left as the game and your other mods have it (from the next time the character screen opens).
- **A smaller download** with nothing lost: dog models store each vertex once instead of up to six times (a fifth of the work to animate each dog), pictures are packed tighter, normal maps leave out the channel the shader works out for itself, and the metal/smoothness maps the game's shader never used are no longer shipped. Kennels share one compressed copy of their pictures, and each breed's are cached once, so a hideout full of dogs doesn't stall.

### Fixed

- **The dog's health bar showed on the pause menu** (Esc) in a raid. It now shows only over the raid itself.
- **The character screen's tab bar narrowed as you scrolled your inventory.** The scrolling strip ends at the first button on the tabs' row, and it counted the inventory's rows as they scrolled up out of sight behind the bar. It now counts only what you can see.
- **A kennel could go down in a doorway**, where a doorway Hideout Extensions cut through a wall has no collider to stop it. Kennels now keep clear of its doorways.
- **The dog could sit on a ledge over your head** at the foot of a stair. Off the bots' navmesh, with its own route there used up, it went by the straight-line distance, 2 m, and took that for beside you. Now a floor above or below you, with no way to you that it can measure, never counts as beside you: it keeps coming, and if it can't, it comes to you as a lost dog does.

## 1.4.0

Needs SPT 4.1.x. Install over 1.3.2 (client and server). With Fika, everyone updates, as the version is the same throughout.

### Added

- **The dog jumps.** Over a fence, a crate, a low wall or a gap, and down off a ledge, using its own jump animations (from a stand, a trot and a run). It jumps when the way round is a long detour, when there's no way round at all, or when it's stuck against something low; if it's a few metres off, it runs up to it first. Anything taller than about 90 cm it doesn't try, and teleporting back to you when it's lost works as before.
- **Combat > God mode** (F12): the dog fights as usual but can't be hurt or killed. Bots still shoot at it. The server owner can fix it for everyone (`godMode` in the server mod's config.jsonc).
- **Combat > Control the dog when you die** (F12, off by default): when you're killed with your dog alive, you carry on as the dog instead of the raid ending. WASD moves it (Shift runs, Ctrl sneaks), the mouse looks round, Space jumps, left click sends it at the enemy you're looking at, and Return ends the raid (both keys can be changed). Get the dog to an open extract and it comes home with its jacket, what's in it and its kit; your own gear is lost as usual, and the raid always counts as your death. Works with Fika: Fika's spectator view waits until the dog's part is over (in a group you then spectate as usual; on your own the raid ends). The server owner can turn it off for everyone (`controlAfterDeath`).

### Changed

- **The character screen's tab bar scrolls** when other mods' tabs (wrist accessories, weekend drops and so on) don't all fit: mouse wheel over the tabs, or the arrows at its right end. Nothing goes under the BACK button any more, and the game's tabs are no longer made narrower to fit.
- **No panting at rest.** The dog only pants after a hard run (keeping up with you sprinting, or a chase), and is quiet otherwise. Other players' dogs (Fika) don't pant.
- **No more snow on the dog.** On snowy maps the game paints snow over everything that isn't a person, so the dog came out covered. It now stays clear of it, as you and the bots do. **Appearance > Snow on the dog** (F12) brings the snow back if you want it.
- **Playing the dog after you die:** your HUD (quick slots, weapon, body figure) is hidden; in an extract you get the game's own green "Extraction in" countdown and its line in the extracts list lit, as when you're alive; **O** shows the raid time and the extracts; **Esc** opens the game's raid menu (settings, leave); **F** noses open the door in front of it (not a locked one: Controls > Dog: open door). Shut doors stop it, as they stop you. **Space always jumps** (from a stand, a trot or a run, straight on): over anything low enough, landing wherever the ground is, cut short only by a wall or a shut door. The camera keeps in front of pipes and beams instead of ending up behind them.

### Fixed

- **"The locker is set up the next time you start the game" every time, at any kennel level.** The DOG tab's ARMOUR LOCKER and PREMIUM STORAGE open hidden containers the server mod keeps for you. When one of them had ended up anywhere else in your profile, the server mod saw it was there and never set it up again, so the DOG tab never found it. A locker outside its place is now put back, with what's in it, and a missing one is made; and nothing else the server mod does at game start can stop that any more. Clicking the button now asks the server straight away and tells you what happened: one restart of the game and the locker opens.

- **Dr. Yang's quests completing themselves after a prestige** (reported with Teeth and Steel: taken, then marked done on opening the DOG tab). The dog's progress was kept per profile, and a prestige (or a wipe) makes a new character on the same profile, so the old progress was written straight back. A new character now starts over as a new handler; the old K9 files are kept beside the new ones (`.old-character-<time>`).
- **The dog getting lost on stairs and teleporting to you.** Tested by walking up and down 36 stairways in Factory and Customs, plus spots the bots' navmesh doesn't cover:
  - Following you down a switchback stair, it sat on the flight above your head: it judged "beside you" by straight-line distance. It now goes by how far it has to walk to you.
  - Down a steep flight it cut the corner, kept its height and stood in the air above the stairs until it was teleported. It now keeps to the ground, going down as well as up.
  - Off the navmesh (the steep outdoor stairs at the Customs construction site, round the end of a railing and down a step) it could only plan one or two straight legs. It now follows the way you walked, as a dog follows a trail, and searches the floor itself if that doesn't help. Steep stairs (up to 45°) count as floor.
  - A run-up to a jump that it then didn't take left it standing still for five seconds, then lost.
  - Cutting a corner at a switchback, it could step out over the edge onto a beam beside the flight below, or drop onto a platform underneath; and where two turns of a stair were one above the other it went 1.6 m up in one step. It now cuts a corner only where the ground is where the stairs are, and never climbs or drops faster than a steep stair.
  - It still teleports to you when there's really no way: a spot you vaulted onto that's too high to jump, or a room it can't get out of.

## 1.3.2

Needs SPT 4.1.x. Install over 1.3.1. With Fika, everyone updates, as the version is the same throughout.

### Fixed

- **No paw icon, so no way to build the K9 Kennel, on the hideout overview.** Before the kennel's first level, the paw stands over the dog, and with no kennel yet and the room behind the emergency wall still closed, the dog started where your character was. On the overview your character is put away off the floor, so the dog (and the paw) didn't appear until you'd walked around the hideout once. The dog now starts by the Rest Space's pet bed.
- **The Rest Space screen showed an empty list headed "Mop the floor:"** (the game's unused placeholder text for improvements), left over from the kennel's levels, which are built from the kennel's own screen (the paw icon on the hideout overview, or K9 KENNEL in the bar along the bottom). That list is now hidden.
- **The kennel built out in the void, outside the hideout.** When no room had space for it, the kennel went next to your character, and on the overview your character is put away outside the hideout. It now goes by the Rest Space's pet bed instead, and a kennel already saved out there is brought back in the next time you open the hideout (one you moved by hand with Hideout Extensions stays where you put it).
- **The dog stopped at the door of some upstairs rooms in raids** (Tarbank, the grenade launcher room), and "Bring dog to me" there said "No room in front of you". The dog walks on the bots' navmesh, which doesn't cover every floor you can stand on. Where it runs out, the dog now crosses the floor itself, up and down stairs, when you're within 20 m, and "Bring dog to me" puts it on the floor beside you.
- **The DOG tab under the BACK button in a window or at a lower resolution**, worst with other mods' tabs on the bar (Weekend Drops Continued's DROPS). With no free place left, the bar's tabs are now made a little narrower (down to 80%) so the DOG tab fits; at full size they go back to normal.
- The Tactical K9 Facility (kennel level 3) listed "cosmetics, premium storage", which it never had. It now lists what it adds: the military kennel.
- The kennel's build buttons said "Mop the floor" and a level being built said "The floor is being mopped" (more of the same placeholder text). They now say BUILD and "Under construction (time left)".

## 1.3.1

Needs SPT 4.1.x. Install over 1.3.0. With Fika, everyone updates, as the version is the same throughout.

### Fixed

- **The K9 Kennel can be built alongside Hideout We Deserve - UI Improvements.** With its "open the station menu without zooming" setting on, clicking the kennel's paw opened the plain Rest Space screen, which doesn't list the kennel's levels. The paw now always opens the kennel.

### Added

- **Russian translation**, by Praxideke: the whole mod (messages, DOG tab, raid screens, kennel, Y menu, quests, items, Dr. Yang). It's used automatically when the game is in Russian.
- **Display > Dog health bar position:** over the dog (as before), or in one place on your HUD, under your health figure (and under the dog camera if that's there too).
- **Display > Fade health bar near your crosshair** (on by default): over the dog, the bar fades out as it nears the middle of the screen, and from further out while you aim down sights, so it never covers what the dog's fighting.

## 1.3.0

Needs SPT 4.1.x. Install over 1.2.x (both folders) and restart the SPT server; your dog, kennel, settings and quest progress carry over. With Fika, everyone needs this version, the host too (a headless host included).

### Added

- **Translations.** All of the mod's text can be translated without touching the code. Each half has a `lang` folder with an `en.json` listing every text: `BepInEx/plugins/K9Companion/lang` (messages, DOG tab, raid screens, dog camera, kennel, Y menu) and `SPT_Runtime/user/mods/K9Companion.Server/lang` (quests, items, Dr. Yang; the server rewrites this one when it starts). Copy each to `<language>.json` (`ru`, `fr`, `ge`, `es`...) and replace the English on the right. `{0}`, `{1}`... stand for names, numbers and other texts and stay in; anything left out stays in English. The client follows the game's language, or Display > Language (Advanced) in F12. F12's own settings stay in English. To share a translation, put your two `<language>.json` files on your own Forge page as an add-on with K9 Companion as a requirement; players drop them into the two `lang` folders.
- **Dog camera > Move right / Move down.** Shift the feed from its position. At top right, Move down 0.06 clears the raid timer.

### Changed

- **Come here calls the dog off.** Come here (F8) and Bring to me (Shift+F8) used to only set the dog following, so a fight it was in went on, and in Defends you mode the next shot sent it back out. Now both end its fight, and for 30 seconds it starts none of its own. Over there still sends it in straight away.
- **Trust Is Earned** no longer asks for found-in-raid food and water. The game never checked it for items left at a place.

### Fixed

- **The DOG tab could still vanish, or sit behind the back button at the top right,** with other mods' tabs on the bar (Weekend Drops Continued). It now looks for a free place on screen, clear of every tab and button, stays inside the screen, and goes left of OVERALL if the right of the bar is full.
- **A pack of dog meat lying loose in a building showed a Tarker dried meat pack through it.** The game's room culling switched the hidden model back on whenever the room came into view; it now stays hidden.

## 1.2.2

Needs SPT 4.1.x. Install over 1.2.1; nothing else changes. With Fika, everyone updates, as the version is the same throughout.

### Fixed

- **The DOG tab no longer disappears with other mods' tabs on the bar** (Weekend Drops Continued's, say). Each time the character screen opened, the DOG tab and the other mod's tab each moved right of the other, until the DOG tab was off the edge of the screen. It now always takes the first free place after the game's own tabs.

## 1.2.1

Needs SPT 4.1.x. Install over 1.2.0 and restart the SPT server; your dog, kennel and quest progress carry over. Only the server's loot changes, but the version is the same throughout, so with Fika everyone updates.

### Changed

- **Dog food is easier to find.** A can of dog food now turns up wherever any can of food can (beef stew, green peas, squash spread, herring, salmon, saury, sprats), and a pack of dog meat wherever dried meat or a Salty Dog sausage can, instead of only alongside one item each. Each is now about as common as the commonest food in the game: around two cans and two packs a raid on Customs, more on Woods, Shoreline and Interchange, and scavs carry them too.
- **The K9 veterinary kit is rarer in the crack house, and found elsewhere too.** It used to turn up in the crack house about twice a raid, far more often than any medkit. Now there's one there in about half of raids, and it turns up anywhere a Grizzly can, as often as a Grizzly does, on every map.

## 1.2.0

Needs SPT 4.1.x. Install over 1.1.2 and restart the SPT server; your dog, kennel and quest progress carry over. With Fika, everyone needs this version, the host too (a headless host included).

### Added

- **A health bar over the dog's head.** It shows through walls, goes amber below 70% and red below 25%, and shows DOWN (with the time left to revive it) or KILLED. It shows in combat mode; Display > Dog health bar turns it off.
- **Take a killed dog's gear.** Look at its body and pick *Take its gear*. Its jacket, with everything in it, goes into your own gear if there's room, and its goggles and camera come with it. Get out alive and it's all back on the dog. Die and it's lost with the rest of your gear. Before, everything the dog wore was lost when it was killed.
- **Bring to me** (Shift+F8, or in the Y menu's DOG group): puts the dog right in front of you, following again. For when it's stuck indoors.
- **GROUP RAIDS in the DOG tab** (under Behaviour): COMES WITH YOU or STAYS HOME, the same as General > Solo raids only, which is no longer hidden under Advanced.

### Changed

- **No dog on Scav raids.** It used to come along without its gear, and wounds or a death there carried over to your PMC's dog.
- **The dog goes for the nearest threat.** An enemy in front of you now comes before one shooting at you from far off. A much nearer enemy pulls it off one it picked itself (never off one you ordered). If the nearest enemy has no path to it, it tries the next one instead of giving up.
- **The dog camera sits under your health figure.** Beside the figure, where it was, it covered your status effects. The setting is now Dog camera > Position > UnderHealth, and an old NextToHealth setting reads as that.
- **Dog messages show along the bottom of the screen.** Bottom left, where they were, covered your stance and stamina.
- **F-key binds don't double up.** An F-key you've bound in the game's Y-menu binds belongs to that bind: the dog's own key on the same F-key stands aside instead of both firing, and says so the first time.

### Fixed

- **The dog's loadout on the loading screen showed "None"** for everything after a Scav raid.
- **Clicking the dog's name on the loading screen** now shows the dog in the big picture (the copied player line's name kept the click).

## 1.1.2

Needs SPT 4.1.x. Install over 1.1.1 and restart the SPT server; your dog, kennel and quest progress carry over. With Fika, everyone needs this version, the host too (a headless host included).

### Fixed

- **The dog camera no longer makes the game hitch.** Each time the feed was drawn, the game rebuilt the grass around the dog, and then around you again for your own view. Whenever you and the dog were in different parts of the map, which is often and always on an Over There run, this happened several times a second. The feed now uses the grass already loaded around you. It also no longer searches the whole map every few seconds for scope lenses and lighting.
- **Friendly PMCs from other mods leave the dog alone.** PitFireteam's friendly PMCs outside your unit (and any bot another mod or the game sets as neutral to you) went for the dog all the same. Now a bot that leaves you alone leaves the dog alone, and the dog leaves it alone too.

### Changed

- **Fika: you're told when dogs aren't shared.** If the host doesn't have K9 Companion, or has a different version, nobody sees anyone else's dog. A few seconds into the raid you now get a message on screen saying so, with both versions where the host has the mod.
- The README lists the level each Dr. Yang quest needs: Trust Is Earned and Teeth and Steel open at level 12, The Last Handler at 20.

## 1.1.1

Needs SPT 4.1.x. Install over 1.1.0 and restart the SPT server; your dog, kennel and quest progress carry over. With Fika, everyone needs this version.

### Added

All of these are off by default. Server owners can set them for everyone in `SPT_Runtime/user/mods/K9Companion.Server/config.jsonc`.

- **Downed instead of killed** (Combat): when the dog's health runs out in a raid it collapses instead of dying. Revive it with a K9 veterinary kit, by looking at it and pressing your interact key or by using the kit, and it gets up at 35% health. You have 180 seconds by default (Combat > Downed: time to revive), enemies leave it alone while it's down, and only its owner can revive it. If time runs out or the raid ends first, it counts as killed.
- **Solo raids only** (General): the dog stays home whenever anyone else is in the raid. A headless host doesn't count as a player. The F12 setting covers your own dog; `soloOnly` in `config.jsonc` covers every dog on the server. A host can't always tell before everyone has joined, so the dog may only be called off as the raid starts. Its jacket then stays with your gear for that raid: it comes back when you extract and is lost if you die.
- **Carry limit (kg)** (Movement): above this weight, jacket and contents together, the dog slows in proportion, down to half speed at most. Nothing is blocked or dropped.

### Fixed

- **The K9 veterinary kit's quick slot key now always treats the dog.** The game only let the key through if the kit would help you, so if you weren't hurt it did nothing.
- **No more small freeze every 2 seconds in the menus.** If you owned the dog, the mod searched the whole game for the hideout player until you first entered the hideout. It now only looks once the hideout has started loading.

## 1.1.0

Needs SPT 4.1.x. Install over 1.0.0; your dog, kennel and quest progress carry over.

### Fixed

- **Fika: the dog now comes into raids for players who join.** Fika doesn't load a map's AI scene on joining players, and that's where the map's walkable surface (the navmesh) the dog needs is kept, so the dog never appeared. When your dog is coming along, that scene now loads for you, as it does in single player and for the host.
- **The bottom menu bar works on the DOG tab.** Traders, Flea Market and the rest of the bar at the bottom did nothing while the DOG tab was open. Leaving from the DOG tab now takes the character screen back to GEAR.
- **The DOG tab no longer covers other mods' tabs** (such as Weekend Drops Continued's daily quests): it moves to the right of the last tab on the bar, whenever the other tab is added.
- **Less work in the menus:** once you'd been to the hideout, the mod searched the whole game for the hideout screen every 2 seconds. It now finds it once.

### Changed

- **Every quest and contract hand-in has to be found in raid:** dog food and meat, drinking water, medical kits and medical supplies, and the dog food and water you stash on Woods in Trust Is Earned. Bought items don't count. Items you'd already handed in still count.
- **Dog food and dog meat are found in raids,** on every map: the can wherever large tins of beef stew turn up and the meat wherever dried beef does, about as often, in containers, lying loose and in scavs' pockets, vests and bags. Dr. Yang still sells them for feeding your dog.
- **Dog food is no meal for a person.** You can still eat it, but it gives very little energy and leaves you thirsty: a can 15 energy and -20 hydration, a pack of meat 10 and -25, premium dog food 20 and -20, treats 5 and -10.

### Added

- Dr. Yang's trader ID, for mods and configs that ask for one: `6a5d0c0c4fb9a3c5e1d0d0a0`. It's also at the top of `SPT_Runtime/user/mods/K9Companion.Server/config.jsonc`.

## 1.0.0

First release.
