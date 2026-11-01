# K9 Companion

By JRS.

A K9 dog for SPT 4.1 that lives in your hideout and comes into raid with you, with its own trader (Dr. Yang), kennel
upgrades, armoured gear and a K9 questline.

![The dog in game, in its kennel room](images/in-game-kennel.jpg)

| The DOG tab | Dr. Yang's shop |
| --- | --- |
| ![The DOG tab](images/dog-tab.jpg) | ![Dr. Yang's shop](images/trader.jpg) |

![The K9 Kennel upgrades in the hideout](images/kennel-upgrades.jpg)

Download the mod from its SPT Forge page or this repository's Releases. Everything about how it plays is in
[K9Companion/README.md](K9Companion/README.md).

## What's here

| Folder | What it is |
| --- | --- |
| `K9Companion` | The BepInEx plugin (client): the dog, its commands, the DOG tab and the kennel. |
| `K9Companion.Server` | The SPT server mod: Dr. Yang, the items, the questline, the kennel levels and the dog's saved data. |
| `K9Companion.Fika` | The Fika link, loaded only when Fika is installed. |

## Building

You need the .NET SDK and an SPT 4.1 install (default `F:\SPT`; pass `-SPTPath` to use another).

1. Copy `BepInEx/plugins/K9Companion` from the release zip into `K9Companion/assets`. It holds the dog's model,
   textures and sounds, which aren't in this repository (see LICENSE.txt).
2. Run `K9Companion/build.ps1 -NoInstall` to build all three parts and pack `K9Companion/dist/K9Companion-<version>.zip`,
   or without `-NoInstall` to install into your SPT folder as well.

## Licence

The code is MIT (LICENSE.txt). The dog's model, animations, textures, sounds and the item models are licensed assets
and aren't covered by it.
