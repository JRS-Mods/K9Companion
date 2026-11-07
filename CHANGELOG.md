# Changelog

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
