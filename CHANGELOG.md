# Changelog

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
