# Changelog

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
