# K9 Companion

By JRS.

The companion keeps stance-aware following, the collar, pet response, recall whistle, rare whining and the hideout bed/food/water features. Attacking exists only in CombatMode. The dog does not gather loot. The package contains 48 animations, including death, hit and bite.

## Translations

The mod's text can be translated without touching the code (since 1.3.0). Each half has a `lang` folder with an `en.json` listing every text in English: `BepInEx/plugins/K9Companion/lang` (messages, the DOG tab, the raid screens, the dog camera, the kennel, the Y menu) and `SPT_Runtime/user/mods/K9Companion.Server/lang` (quests, items, Dr. Yang; the server writes this one when it starts). Copy each to `<language>.json` with the game's language code (`ru`, `fr`, `ge`, `es`...) and replace the English on the right of each line. Keep `{0}`, `{1}`... where they are (a name, a number or another text goes there) and any `<color>`-style tags. Anything missing or left empty stays in English. The client follows the game's language; Advanced > Language in the config can pick a file instead. The F12 settings themselves stay in English.

## Movement

The dog walks when close, runs once it is more than 6 m behind you, and sprints to catch up beyond 15 m. Near you it matches your pace: it trots alongside a jog and runs with you when you sprint. Each gait plays at the speed it was animated for (measured from the original root-motion clips), scaled to the dog's actual ground speed so the feet don't slide. If the dog gets lost (over 45 m away, or stuck for 8 seconds), it reappears behind you rather than in front of you. Stuck before then (indoors, say)? **Shift+F8** (Controls > Bring dog to me, or Bring to me in the Y menu's DOG group) puts it straight in front of you, following again.

Settings (Movement section): FollowDistance (2 m), RunDistance (6 m), SprintDistance (15 m), and WalkSpeedMultiplier / RunSpeedMultiplier / SprintSpeedMultiplier (1.0). A follow distance you set in an earlier version is carried over.

## On raid day

- **Raid screens.** On the CONFIRMATION screen (the one with READY) the dog stands next to your character, in the gear it wears into the raid; drag it to turn it. Under it: its health, hunger and thirst, and **TAKE DOG** (yes/no, the same switch as the config's Enabled). While deploying, the dog is in the party list under you (a paw, its training level, its name): click its name and the big picture shows the dog, click yours and it's your character again; its eye opens its loadout (jacket, head gear, camera; the jacket opens like a container).
- **Treat the dog**: only the **K9 veterinary kit** heals the dog, and it's never used on you. Click USE or HEAL ALL on it (or double-click it, press its quick slot key, drag it onto your body, or press HEAL DOG in the DOG tab): the buttons are grey only if the dog can't be treated, and say why. A full kit restores the dog's whole health, a part-used one its share, and only what the dog needs is taken from it. In a raid the dog must be within 3 m and you standing still: your inventory closes and you kneel and work on it for 3-8 seconds with a countdown while it lies still beside you; any key calls it off. At home it's done at once.
- **Look at the dog and press your interact key**: open its jacket (named for what it is), feed it, give it water, or pet it. Feeding takes the best food or water from the dog's jacket or your own gear; you hold it out with your hand, as you do when petting. You can also right-click dog food or water: **Feed dog** / **Give water to dog** (in a raid it eats from your hand; in the hideout it has it at once).
- **Dog camera.** When the dog wears the camera headset, the camera vest or the clip-on action camera, what it sees shows top left of your screen, under the green health figure (since 1.2.0: beside it were your status effects) and the dog's health bar, like a bodycam (REC, its name, how far it is). F6 shows or hides it; position, size, frames per second, resolution and grass are in the config's Dog camera section, and **Move right** / **Move down** shift it from its position (since 1.3.0: top right sits over a raid timer there, Move down 0.06 clears it). It renders a picture a few times a second, only while it's showing. The game draws its clouds, water, wall decals and lamps for it as it does for your scope; it has the grass and bushes (Medium and High), your view's colour (tone mapping, exposure, colour grading, the weather's grey), the game's sky with its sun, and your own head gear. Wet surfaces in rain aren't in it. High gives the most detail. If the dog dies it shows NO SIGNAL.
- **Dog health bar.** In combat mode, the dog's name and health show under your health figure, top left: green, amber below 70%, red below 25%; DOWN with the time left to revive it, or KILLED. Display > Dog health bar turns it off.
- **Messages** from the dog ("Follow me.", "K9 growls: enemy ahead") show at the bottom centre of the screen, clear of your stance and stamina (before 1.2.0 they were bottom left, over them).
- **Over There** (DOG menu): aim at an enemy, or near one (through a scope at 40 m or more is fine), and the dog goes for it, up to the Ordered attack range (150 m by default), tracking it by scent for longer than a fight it starts itself. Aim where nobody is and it runs there and hunts the area: it runs a loop round the spot (about 7 m out), then runs back to you; the first enemy it picks up on the way, there or on the loop it goes for. Aiming at a wall or a roof is fine: it goes to the nearest ground it can reach along your line of aim. Without combat mode Over There just sends it there to wait. Recall calls it off.
- **How it goes in** (Approach: DOG tab, Y menu, or the config). **Auto** (default): straight at an enemy within 14 m (Combat > Auto approach: rush within), and working its way in to one further off, changing as the distance does. **Stealth**: always works out a way that keeps out of sight of the target and any other enemy about (behind buildings, along walls, through bushes, wide of where they're looking). Far off it runs; close in it trots while hidden and goes low where it can be seen, waits a moment at the edge of cover if someone is looking at the ground ahead, and crosses a short gap in one dash; inside about 9 m (18 m if spotted) it barks, sprints and bites. **Direct**: runs straight at it. Low, the dog is harder for bots to notice, and through bushes harder still.
- **When it attacks** (DOG tab, Y menu, or the config). **On order**: only on Over There; if you're shot at it tells you where from. **Defends you**: also goes for whoever shoots at you or it, near misses included, up to Combat > Defend range (100 m), with no order. **Any enemy**: also goes for anything it detects within the maximum engagement distance. In the Y menu's DOG group, picking **Attack** or **Approach** moves on to the next setting, and they can be bound to F-keys like the other commands.
- **Bites land, and they kill.** Until its jaws snap shut the dog closes in on the leg it's going for, so the bite meets it; once it has hold it goes for the throat, under the helmet and any collar, so armour doesn't stop it. A scav, PMC or player dies in two or three bites (Bites to kill, 2.5), a boss in four (Bites to kill a boss); guards count as everyone else. A bitten enemy is thrown off for a moment (Bite stagger, 0.5 s) and then turns on the dog and shoots at it, even with you in view, for a few seconds.
- **Petting** sits the dog for a moment without changing its orders: a following dog gets up and follows again.
- **Attacks only on command.** Like a patrol dog it alerts you to enemies (growl, bark, and where they are from you) and goes in when you order Over There. Set Dog aggression to Defensive to have it also go for anyone attacking you or it, or Aggressive to have it go for every enemy it detects.

## Playing alongside other mods

**Fika.** Everyone in the raid needs this mod (same version). Each player's game runs that player's own dog, and everyone else sees it, in its gear, and hears it. Bots on the host notice and shoot other players' dogs just as they do yours. A bite on a bot counts as yours (Fika's own damage messages carry it), the bot staggers and turns on the dog, and only the host decides which bots are hostile, so it's the same answer the bots themselves use. Team-mates' rounds don't hurt your dog. The dog's CONFIRMATION-screen panel stays hidden while Fika is installed, as Fika's server browser takes over that screen: TAKE DOG is the config's General > Enabled (F12). The file K9Companion.Fika.dll is only ever loaded when Fika is installed; a host or player without the mod just doesn't see dogs, and nothing is sent to them. Dogs are passed on through the host, so the host needs the mod too, a headless host included; if it hasn't got it, or has another version, nobody sees anyone else's dog, and since 1.1.2 each player is told so on screen a few seconds into the raid (with both versions, where the host has the mod). Nothing here goes through the SPT server: bots and navigation only exist in the game itself.

**Solo raids only.** With General > Solo raids only on (F12, or in the DOG tab under Behaviour: GROUP RAIDS > STAYS HOME), your dog stays home whenever anyone else is in the raid (a headless host doesn't count). That's your dog only: to keep every dog out of group raids, the server's owner sets `soloOnly` to `true` in `config.jsonc`. A host can't always tell before everyone has joined, so the dog may only be called off as the raid starts; its jacket, if it has one, then stays with your gear for that raid and goes back to the stash when you get out.

**SAIN and ORBIT.** The dog is one more layer in each bot's brain: above their combat layers and below their avoid-danger ones, so they carry on as usual until a bot notices the dog. Bots that ORBIT has parked or switched off aren't hunted, watched for or bitten. Other AI mods that add brain layers the same way (Questing Bots, Looting Bots) should behave the same, but haven't been checked. If another mod's damage handling throws an error, that bite is lost, not the dog.

**PitFireteam** (and other mods that make bots friendly): a bot whose group has you down as neutral or an ally leaves the dog alone, as it leaves you, and the dog leaves it alone. So PitFireteam's friendly PMCs outside your unit no longer go for the dog (before 1.1.2 they did).

**Navigation-mesh mods** (Waypoints): the dog asks whichever navigation mesh is there. **Spawn mods** only change who's about, so they shouldn't matter.

## The DOG tab

On the left: the dog itself in the gear it wears (drag to turn it; in a raid its outline), its head and collar slots, its load, health, thirst and hunger, the care buttons and how it behaves (combat, when to attack, and stealth, direct or auto). On the right: its jacket slot with the jacket's grid, what the jacket gives (armour class, durability, speed), and the kennel with its upgrades and jacket presets. Each button says underneath what it has to work with, or what it's waiting for. In a raid HEAL DOG still works, and treats the dog beside you.

## Carry limit

The jackets have slots but no weight limit, and what the dog carries never counts towards your own weight. Movement > Carry limit (kg) adds a limit: above it (jacket and contents together) the dog slows in proportion, down to half speed at most. Nothing is blocked or dropped. 0 (the default) means no limit.

## Settings for everyone on a server

The owner of the SPT server can fix dog settings for all its players in `SPT_Runtime/user/mods/K9Companion.Server/config.jsonc` (made the first time the server starts): combat mode, aggression, approach, Auto's rush distance, defend range, engagement distance, ordered range, bites to kill (and for bosses), bite cooldown and stagger, dog health, solo raids only, the downed state and its time limit, and the carry limit. Each one left `null` stays each player's own setting. With Fika that means everyone in the raid plays by the same rules, as they all use the same SPT server. Saved changes count from each player's next raid, with no restart. In the DOG tab a fixed setting is marked (SERVER), and F12 changes to it do nothing. A mistake in the file fixes nothing, and the server log says what's wrong.

## Combat settings (Combat section of the config)

Everything combat-related is decided in one place (DogRules), so no setting can contradict another. Immortality is not a separate option: the dog is immortal exactly when CombatMode is off.

| Setting | Default | Meaning |
| --- | --- | --- |
| CombatMode | false | Master switch. Never applies in the hideout. |
| Dog aggression | Passive | Passive / Defensive / Aggressive. Only used when CombatMode is on. |
| Approach | Auto | Auto / Stealth / Direct: see above. |
| Auto approach: rush within | 14 | Under Auto, how close an enemy has to be for a straight attack, in metres. |
| Defend range | 100 | With Defends you or Any enemy, how far from you the dog goes after someone shooting at you or it, in metres. |
| Maximum engagement distance | 25 m | How far from you the dog goes after an enemy (5-60). |
| Bites to kill | 2.5 | Throat bites that kill a scav, PMC or player, armour or not (1-10). A fraction mixes the two either side: 2.5 = two bites for half of them, three for the rest. Rolled once per enemy. Replaces Bite damage (0.38). |
| Bites to kill a boss | 4 | The same for bosses (1-20). |
| Bite cooldown | 1.5 s | Time between bites (0.8-5). |
| Dog health | 300 | Hit points per raid in combat mode (30-1000). An untouched old default of 150 is raised to 300 once. |
| Downed instead of killed | Off | At 0 health the dog collapses instead of dying. Revive it with a K9 veterinary kit (look at it and press your interact key, or use the kit) and it gets up at 35% health. Enemies leave it alone while it's down. Only you can revive your dog. If time runs out or the raid ends first, it counts as killed: its gear is lost and it comes home in critical condition. |
| Downed: time to revive | 180 | Seconds to revive a downed dog (0-600). Treating it stops the clock. 0 = as long as the raid lasts. |
| Bite stagger | 0.5 s | How long a bitten enemy is thrown off (0 turns it off). |
| Audio > Volume | 0.35 | Dog sound volume. |

**CombatMode off.** The dog follows you and obeys Come Here, Sit, Lie down and Stay. It never attacks, hostile AI ignore it, bullets pass through it, and it cannot be hurt or killed. Turning CombatMode off in a raid cancels any attack in progress and revives a dead dog.

**CombatMode on.** Hostile AI that spot the dog (by their own view distance, field of view and line of sight) can shoot it with their own aim and weapons. The dog has body, neck and head hitboxes, takes real damage with the usual blood impact, flinches, and dies for good at 0 HP. There is no invincibility option. Your own stray rounds draw blood but do no damage.
**How the dog fights.** One small state machine (CombatBrain) runs every fight: Idle, Chase, Strike (one bite), Recover (cooldown, facing the target), Return (back to you).
- Detection: the dog notices hostile AI it can see within the engagement distance, or smell and hear within 8 m. Walls hide anything further away.
- Factions: only AI that the game itself treats as hostile to you count. Rogues that would only warn a USEC, scavs that are neutral to a scav player, bots whose group has you down as neutral (another mod's friendly bots), your own group and the bot's allies are never attacked, not even on an order.
- Passive attacks only on an order (Over there in the DOG menu). Defensive also attacks AI that are targeting you, have hurt you or the dog in the last 30 s, or are shooting at the dog. Aggressive also attacks any hostile it detects within the engagement distance.
- Chasing: the dog runs to the target. It gives up and comes back if the target can't be reached on the navmesh, it gets stuck, it loses sight for 5 s, either of them gets too far from you, or after 30 s of chasing (60 s per fight). A target it gave up on is left alone for 20 s.
- Biting: only when standing, in physical contact (a body part within mouth reach), with no wall between. It takes hold of the legs first, then arms or body if nothing lower is in reach; the damage goes to the head (the throat), with no armour in the way, and is the head's maximum health divided by that enemy's bites to kill. Damage lands on the frame the jaw snaps shut, read from the bite animation (53% through). After each bite there is a cooldown (Bite cooldown, 1.5 s).
- Armour: the bite goes through the game's own hit handling, at the throat with no collider named, so no armour covers it. A bite that kills its target doesn't stagger it.
- Vulnerability: the dog's hitboxes stay active throughout, and a bitten bot immediately knows where the dog is. A hit makes the dog flinch, which cancels the bite and starts the cooldown. The dog can't bite while dead, sitting, lying, flinching or getting up. Below 20% health it stops starting fights of its own and breaks off automatic ones; it still obeys an order.
- Afterwards it returns to you and resumes following. Come Here, Sit, Stay or turning CombatMode off cancels a fight at once.

## Dog commands in the Y menu

Hold Y as usual: the game's gesture menu has a **DOG** hexagon at the bottom of the ring, opposite REACTION (lower down if the game's situational group is showing there). Hover it and pick a command, by clicking or by releasing Y over it, just like any phrase.

| Command | Available |
| --- | --- |
| Over there (attack) | CombatMode on, in a raid. Attacks the hostile under your crosshair; allies, your own group, unreachable and too-distant targets are refused with a message. |
| Come here | Always. Calls off any attack, ends sit or lie down and follows again (same as F8). For 30 seconds after, it starts no fight of its own (Defends you / Any enemy), so you can get it back to you while you're under fire; Over there still sends it in. |
| Bring to me | Always, unless it's dead or down. Puts the dog straight in front of you, following again: for when it's stuck (same as Shift+F8). Calls it off the same way as Come here. |
| Sit | Always. Sits as soon as it stops and stays until recalled. |
| Lie down | Always (same as Shift+F7). |
| Open saddlebag | In a raid, standing next to the dog (alive or dead). Opens your inventory with the dog's bag beside it. |
| Go to bed | Hideout only, so not in the menu since 0.31.2 (there's no Y menu in the hideout): Shift+F7 there, or the DOG tab. An F-key still bound to it, or to the old Search and Fetch commands, is unbound when the game starts. |
| Feed / Give water | Shows hunger or thirst. In a raid you hand over food or water (dog within 3 m); in the hideout these fill the dishes. |
| Follow | Always. The dog follows you again (same as F7 from Stay). |

While it follows you in a raid the dog also sniffs the loot round it (10 m, 2 m more per training level): something worth at least the **Valuable alert** price (config Search, 40,000 ₽, 0 = off), loose or in a container, gets one bark, a look its way and a message with what it is and where. Each find once a raid.

Commands that can't be given right now are greyed out with the reason next to them, for example "needs CombatMode" or "K9 is dead". Right-click a DOG command to bind it to an F-key, the same way as the game's phrases.

## Hunger and thirst

The dog has hunger and thirst (100% = fine). Both drop slowly while you play: 8% and 12% per hour of raid by default (HungerDecayRate, ThirstDecayRate), half that while resting in the hideout, and not at all while the game is closed. They are saved in your SPT profile by the server mod (with a local copy beside the plugin), so they carry over between raids and restarts. Saved values are checked on both sides; anything broken falls back to a fed dog with empty dishes.

**Food:** Can of dog food (55%) and Pack of dog meat (40%), both sold by Dr. Yang and found in raids (since 1.2.1). The can turns up wherever any can of food can (beef stew, green peas, squash spread, herring, salmon, saury, sprats), and the meat wherever Tarker dried meat or a Salty Dog sausage can: in containers, lying loose, and in scavs' pockets, vests and bags. Each is about as common as the commonest food in the game, around two of each a raid on Customs and more on Woods, Shoreline and Interchange. You can eat them yourself, but they do you little good: 15 and 10 energy, and 20 and 25 hydration lost (premium dog food 20/-20, treats 5/-10). Meat from other sources works too: Salty Dog beef sausage and large beef stew 35%, small beef stew 30%, Tarker dried meat 25%. **Water:** Bottle of water 60%, Aquamari 50%, Emergency Water Ration 40%, purified water canister 100%. Opened bottles count for what's left in them. Items are used up with the game's own discard, from the dog's saddlebag and your gear in a raid, or from your gear and stash in the hideout.

- **In a raid:** use Feed or Give water in the DOG menu with the dog near you; it eats from your hand (the item is only used once it has actually eaten). If hunger or thirst drops below 20% it helps itself from its own saddlebag, never from your gear.
- **In the hideout:** Feed / Give water (or Shift+F9 / Shift+F10 at the bed) fill the dishes. The dog goes to its dishes on its own when below 60%. With AutoFeedInHideout on, nearly empty dishes are topped up from your stash automatically.
- **Effects:** below 30% the dog says so. In combat mode a hungry or thirsty dog gets slower, down to 75% speed and a slower bite rhythm when empty. Hunger and thirst never hurt or kill the dog, in any mode.
- EnableSurvivalNeeds off: no decay, no slowdown, feeding disabled.

The dog has no eating or drinking animations of its own; meals use the nose-to-ground part of the dog's Stand_02 idle.

## DOG tab (character screen)

The character screen has a **DOG** tab after the game's own tabs (main menu, hideout and raid). It keeps your stash on the right and shows the dog in place of your equipment:

- Health (in a raid: its current health or DEAD; otherwise its full health, or "can't be hurt" when CombatMode is off), hunger and thirst.
- Combat mode on/off and the aggression level, changeable right there.
- **Vest (saddlebag):** which bag the dog wears, how much is in it, **Open vest** (the game's own item window, so you drag items in and out of it from your stash) and **Take vest off**. With several saddlebags you can choose which one to put on the dog. The worn bag is the one that goes into PMC raids, and the dog visibly wears a saddlebag harness while it has it. In a raid, Open vest works when you stand next to the dog.

## Hideout kennel

The dog's corner in the hideout is the hideout's own pet bed (stretched so the dog fits inside lying down, about 1.2 x 1.0 m) with two of its pet bowls, copied from the rest space and kitchen at runtime so they look like the rest of the hideout (see Kennel furniture below). If the game ever drops those props, the bed and bowl from `E:\SPT Mod\Assets` are used instead (converted by `tools/convert_props.py` into `kennel.props`, `kennel_bed.jpg` and `kennel_bowl.jpg`). Once the kennel's first level is built (see K9 Kennel below; existing owners have it already), the kennel is set up the first time in the hideout's rest area: the mod finds a clear, level patch of floor near it, facing into the room, and remembers it. If there's no room there it goes where the dog is.

- **Resting:** the dog lies on the bed (on an invisible cushion surface so it doesn't sink in), and after a while falls asleep. Go to bed (Shift+F7 in the hideout) sends it back; it walks there, or goes straight there if the hideout has no navmesh.
- **Eating and drinking:** it goes to the bowls on its own when hungry or thirsty, and walks back to bed afterwards (unless it's following you). Bowls show how full they are.
- **Sit / lie down / idle:** the usual commands; all of it uses the dog's existing animations (lying, sleeping, sitting, the nose-down part of an idle for eating and drinking).
- **Safety:** the kennel is made of new objects added to the hideout scene at runtime. The hideout's props are only copied, never changed, and the copies are destroyed when you leave the hideout. If neither the hideout props nor `kennel.props` are there, the kennel falls back to simple shapes.

### Where the kennel goes

Once the emergency wall is broken through, the kennel has its own corner in the room behind it: the room's floor clutter (stepladder, bunk bed, tyre, box and the torch on the bed) is switched off while you're in the hideout, and the kennel stands where the bunk bed was, back to the wall and facing into the room. The clutter comes back when the dog leaves the hideout; nothing else of the room is touched. Without that room the kennel looks for space in the gym, then the rest space, then the hall of fame, and only takes a spot where the whole kennel for its level fits (bed and bowls, plus the cabinet and barrel at level 2 and the vet table at level 3). Each piece stands on the floor under it. It can't be moved by hand (that could leave it somewhere that breaks the hideout); once the room is open the kennel always goes back to it.

The hideout overview shows the kennel's own hex (`kennel_icon.png`, the paw) over it, with its name and level. Hovering it outlines the kennel; clicking it opens the K9 KENNEL screen in place: just the kennel's three levels, with their materials, timers and Start buttons (it is the Rest Space's area screen underneath, since the game keeps the levels there, with everything else of the Rest Space hidden). The Rest Space's own screen no longer lists the kennel.

### Kennel furniture

Everything in the kennel is a copy of a prop the hideout scene already has, found by its model name, even in areas you haven't built (their objects are only switched off): the pet bed (rest space), pet bowls and dog food bag (kitchen), a steel cabinet (equipment presets stand), a food barrel (shooting range), and the med station's metal table, medical case and medical supplies. A copy keeps only the model and its levels of detail; its scripts, colliders and anything else are removed before it is switched on, so it can't be looted, opened or bumped into. The models are stood upright and turned to face into the room. Nothing is extracted from the game or shipped with the mod. A prop the game no longer has gets a simple stand-in, and a line in the BepInEx log.

Four pieces are custom models instead (`kennel_models.props`, cut down to a hideout budget by `tools/convert_kennel_models.py`): an ammo box of dog toys (any kennel), a sack of K9 working dog food (replacing the hideout's food bag) and a wooden dog house (Working Dog Station), and a K9 transport crate (Tactical K9 Facility). Like the rest, each goes down only where it clears the hideout's walls and furniture (or on the other side of the bed), and is left out where there's no room.

## Dr. Yang (trader, server mod)

Trader ID: `6a5d0c0c4fb9a3c5e1d0d0a0` (for other mods' configs; the trader is built in code, so there's no `base.json` to find it in).

A new trader, Dr. Wei Yang, the clinic vet. He is unlocked from the start, sells in roubles, buys food and drink back at half value, and restocks every hour. Her offers also show on the flea market.

| Item | Price | Stock per restock | |
| --- | --- | --- | --- |
| Can of dog food | 4,000 | 30 | new item (55% hunger) |
| Pack of dog meat | 2,500 | 40 | new item (40% hunger) |
| Bottle of water (0.6L) | 3,200 | 30 | vanilla |
| Emergency Water Ration | 18,000 | 10 | vanilla |
| Canister with purified water | 55,000 | 3 | vanilla |
| Dog saddlebag (spare) | 15,000 | 2 | the dog only takes one bag into a raid |

Her trader base starts as a copy of Jaeger's so every field the game expects is present; vanilla traders, assortments and item templates are not modified (dog food is no longer added to Jaeger). Her text is added for every game language (in English). The portrait is your picture (`E:\SPT Mod\Assets\Yang.png`, scaled to 400x400 as `res/dryang.jpg`).

**Item models:** new items are clones of vanilla items, but since 0.31 the dog's supplies show their own models and icons wherever the game shows an item (stash and trader icons, the inspect window, loot on the ground): dog food and premium dog food a K9 dog food can (label wrapped round it), dog meat a vacuum pack with a K9 label, the training kit a K9 duffel bag, the vet kit and the Veterinary supply case a K9 vet case, the K9 identification tag a stamped bone tag, and the three collars themselves. They're custom models with the artwork projected on, decimated to 5,000-14,000 triangles with colour, normal and metal/smoothness baked from the full model (`tools/convert_dog_items.py`; the collars `tools/convert_item_models.py`). Treats, bedding, the handler's jacket and the transport records keep their vanilla looks. In first person (eating a can yourself) the vanilla model still shows. Since 0.16 the kennel is built in the Rest Space (see K9 Kennel below).

## Saddlebag (needs the server mod)

The package includes a small server mod (`SPT_Runtime/user/mods/K9Companion.Server`). It adds a **Dog saddlebag** (4x4 case, 4x4 inside) and keeps one in your stash; a lost bag is replaced, empty, the next time you start the game. Fill it in your stash like any container.

When you start a PMC raid, the bag goes with the dog. In raid, stand next to the dog and choose **Open saddlebag** in the DOG group of the Y menu: your inventory opens with the bag beside it, and you move items in and out as usual.

- You extract with the dog alive: the bag comes home with whatever is in it, back in your stash.
- You die: the bag and everything in it are lost, like your gear.
- The dog dies: you can still open the bag at its body and take things out. Or take all of its gear: look at the body and choose **Take K9's gear** (your interact key). Its jacket, with what's in it, goes into your own gear (if there's room), and its head gear and camera come with it. Get out alive and it all goes back on the dog; die and it's lost with your gear. Whatever you leave on the dog is lost when the raid ends.
- Scav raids: the dog doesn't come at all (since 1.2.0), so neither does the bag.

How it stays consistent: the client tells the server mod that the bag is going into the raid, then moves it into the profile's raid quest-items container. The game saves that container when you extract and wipes it when you die, so the contents are handled by the game's own raid saving. After the raid the server mod puts the bag back in the stash and removes leftovers of the pre-raid copy, so nothing can be duplicated. If the server mod isn't installed, or doesn't confirm, the bag simply stays in the stash. Its log lines start with [K9Companion].

**Bites and the enemy's reaction.** A bite throws the bitten bot off through the game's own flashed reaction: it loses its aim, shoots badly for the Bite stagger time and tries to get clear. Bots aim at where they last saw the dog a fraction of a second earlier, so a running dog is much harder to hit than one standing still.

## Dr. Yang's K9 questline (server mod)

Five quests from Dr. Yang, in order, then five repeatable contracts. Each needs the one before it, even on an SPT Developer profile (which otherwise starts with every quest open), and a PMC level: A Man's Best Friend from level 1, The Missing Unit 5, Trust Is Earned and Teeth and Steel 12, The Last Handler 20 (the contracts open with The Last Handler). Nothing in the story needs Reserve or Streets.

| # | Quest | Objectives | Rewards |
| --- | --- | --- | --- |
| 1 | A Man's Best Friend | Hand over 3 dog food (can or meat), 2 drinking water, 1 medical kit, all found in raid | 2,500 XP, 18,000 ₽, a weapon |
| 2 | The Missing Unit | Find and hand over a K9 identification tag (scavs, PMCs, Customs) | 16,500 XP, 102,000 ₽, weapons |
| 3 | Trust Is Earned | Stash one dog food (can or meat) and one water in the ZB-016 bunker on Woods, then survive and extract | 8,500 XP, 50,000 ₽, **your dog** (new handlers), the kennel, a black collar |
| 4 | Teeth and Steel | Dog wears armour class 2+; survive 3 raids with it; 5 kills with it beside you; give Follow, Sit and Come here | 28,500 XP, 185,000 ₽, weapons, tactical collar, Cordura, aramid |
| 5 | The Last Handler | Find and hand over the K9 unit insignia; find and hand over a K9 veterinary kit (Customs crack house); hand over 2 found in raid medical kits; complete 5 raids with your dog alive | 40,000 XP, 260,000 ₽, weapons, **elite harness** |

Every item a quest or contract takes has to be found in raid: bought ones don't count. All quests also raise your standing with Dr. Yang. Contracts (after The Last Handler): **Daily Rations** (5 dog food: 20,000 ₽), **Safe Return** (extract twice with the dog: 35,000 ₽), **Supply Run** (3 medical supplies: a K9 veterinary kit) every 24 hours; **Working Partnership** (5 raids with the dog: 100,000 ₽) and **The Pack** (10 dog food: a tactical armour jacket) every 7 days.

- The bunker objective uses the game's own zone (the ZB-016 bunker from Jaeger's "Thrifty"), so the game checks it itself.
- Where to find the story items, found-in-raid: the **K9 tag** on 1 in 4 scavs and PMCs (any map), and on Customs in scav corpses, jacket pockets and drawers; the **insignia** on about 1 in 7 scavs and PMCs, or lying loose in the Customs Dorms; a **K9 veterinary kit** lying loose in the Customs crack house (about half of raids), or anywhere a Grizzly medkit can turn up, as often as one does, on any map. A kit bought from Dr. Yang isn't found-in-raid, so it doesn't count.
- The dog's own objectives (commands, finds, kills, extractions with the dog, armour) are counted by the server mod and show in the quest screen like any other objective. Each find, item and kill counts once per raid; an extraction counts only if the dog is alive at the end.
- Hand-ins and the dog's objectives are ticked off by the server mod too, so a quest whose last objective was a hand-in becomes ready to hand in even if the game client missed it.
- Contracts reset the next time you start the game after their cool-down, and only once completed: one in progress keeps its progress.

### Who has a dog

Profiles that had the dog before 0.16 keep it: anything of the dog's (gear, saddlebag record, needs file) counts. Their kennel starts at level 1, since they already had the bed and bowls. A new profile has no dog until Trust Is Earned. The dog from the bunker comes to you then, with her first harness and the kennel lockers, once only. If you already own a dog, she stays with Dr. Yang.

## K9 Kennel (Rest Space)

The game can't be given a new hideout area: it builds its areas from its own asset list, and server data only changes areas it already has. So the kennel is three **improvements of the Rest Space**, where the dog's bed already stands. Open the Rest Space in your hideout; each level shows its materials and timer, and the game takes the items itself. The Rest Space must be built (level 1).

| Level | Needs | Materials | Time | Adds |
| --- | --- | --- | --- | --- |
| 1 Makeshift Kennel | Trust Is Earned | 50,000 ₽, 2 corrugated hoses, 4 bolts, 4 screw nuts | 1 h | Bed and bowls in the hideout, food and water, the dog's care in the DOG tab |
| 2 Working Dog Station | Level 1, Dr. Yang LL2 | 175,000 ₽, 2 toolsets, 3 metal spare parts, 5 bolts | 3 h | Armour locker (6x6), jacket repairs, 2 equipment presets, better rest, an armour cabinet, a food barrel and a bag of dog food by the bed |
| 3 Tactical K9 Facility | Level 2, Dr. Yang LL4 | 450,000 ₽, 2 military cables, 1 LEDX, 2 toolsets | 8 h | Veterinary station (+50% healing), 4 presets, faster recovery, premium storage (8x8), a metal vet table with a medical case and supplies |

A level that isn't unlocked yet says why in the Rest Space screen and can't be started; the server mod checks the same rules. The extra pieces at levels 2 and 3 are hideout props (Kennel furniture above), each placed only where it clears the hideout's own geometry, or on the other side of the bed.

## DOG tab: kennel care

Under the dog's stats: the dog (health, hunger, thirst, mode, condition, whether it can deploy), its jacket (armour class, durability, load), your supplies (dog food, water, medical, repair materials, bowl levels, bed) and the kennel (level, what's next or how long the build has left). The buttons use your real items, from your stash, the kennel lockers, the dog's jacket and your gear, through the game's own transactions:

- **Feed dog / Give water**: the dog eats or drinks now. **Fill bowls**: fills the emptier bowl (kennel level 1).
- **Heal dog**: uses a K9 veterinary kit, the one nearest what the dog needs. A full kit restores 100%, a part-used one its share; only what the dog needs is taken. The vet station makes a kit go half as far again.
- **Repair jacket** (level 2): aramid +25, Cordura +15, ripstop +10, KEKTAPE +6. Class 4-5 jackets take only aramid or Cordura. Each repair costs a tenth of what it puts back off the jacket's maximum.
- **Upgrade bed** (after Learning the Ropes): fleece lining (K9 bed liner, or 1 fleece fabric), then thermal bedding (K9 thermal dog bed, or 2 ripstop and 1 paracord). Each adds 3% health per hour of rest.
- **Rename dog**.
- **Collar** (since 0.31 an item in the dog's collar slot, beside its head slot in the DOG tab): the coyote training collar it comes with (Dr. Yang LL1), the black training collar (Trust Is Earned's reward, LL2) and the tactical collar (Teeth and Steel's reward, LL4). The dog wears it into every raid and never loses it. A dog from before 0.31 is handed the collars it had earned once, wearing the one it had on.
- **Armour locker / Premium storage**: open like any container; drag gear in and out. Their contents stay home during raids.
- **Presets** (2 at level 2, 4 at level 3): click to put that jacket on the dog (the old one goes to the locker, or your stash). Shift-click, or click an empty preset, to save the jacket the dog wears.

## Injuries between raids

With CarryOverInjuries on (default), wounds from a combat-mode raid stay with the dog. A dog killed in a raid comes home in critical condition. The dog never comes on Scav raids (since 1.2.0; before, it came without its gear and brought its wounds home), so a Scav raid can't hurt it. Below 25% health it **stays home** (no dog and no jacket on raid day) until it heals. It heals while the game runs out of raids: 6% per hour without a kennel, 10/16/25% at kennel levels 1-3 (+3% per bed upgrade), or at once with a K9 veterinary kit. Better kennels also slow hunger and thirst while resting. Jacket armour wear carries over between raids too, until repaired. CombatMode off still means the dog can't be hurt.

## Equipment at Dr. Yang's

Dr. Yang has four loyalty levels. The questline's standing reaches LL2 by Trust Is Earned, LL3 by Teeth and Steel and LL4 with The Last Handler. What he sells at each level:

| Level | Stock |
| --- | --- |
| LL1 | Dog food and meat (bulk after Strays of Tarkov), water, K9 training treats (15% hunger), K9 training collar (Coyote), army and aseptic bandages, fleece fabric (after Learning the Ropes) |
| LL2 | K9 lightweight harness, K9 training collar (Black), K9 fleece bed liner and thermal dog bed (bed upgrades), K9 training kit, ripstop fabric, car first aid kits, Salewa |
| LL3 | Armour classes 2-3 (basic and reinforced jackets), K9 veterinary kit (2x1, for the dog only: using it, or its quick slot key, treats the dog), IFAK, AFAK, repair materials (Cordura, aramid, KEKTAPE), **with the WTT Content Backport**: Norinco QBZ-191 rifle, its magazine and 5.8x42 DBX95/DBP191; Type 56 Chicom chest harness, Samurai Menpo masks, the Korean-made SGM 50-round Glock drum |
| LL4 | Armour classes 4-5 (tactical vest, elite assault vest), K9 premium dog food (100% hunger), Grizzly, Surv12, K9 tactical collar, **with the WTT Content Backport**: Howa Type 20 rifle, 5.8x42 DVX12/DVC12; DevTac Ronin helmet and respirator |

Some of these also need a quest first: the dog's supplies (Trust Is Earned), Cordura and aramid (Teeth and Steel). The harnesses go by loyalty level alone, one per level, bigger ones higher up. His offers also show on the flea market.

| Jacket | Class | Durability | Speed | Size / load | Price | Available |
| --- | --- | --- | --- | --- | --- | --- |
| K9 Chest Rig (Black) | - | - | 100% | 2x2 / 4x4 | 12,000 | LL1 |
| K9 Pocket Vest (Black) | 3 | 40 | 95% | 2x3 / 5x4 | 48,000 | LL2 |
| K9 Tactical Vest (Olive) | 4 | 50 | 91% | 3x3 / 5x5 | 84,000 | LL3 (also The Pack's reward) |
| K9 Assault Vest (Camo) | 5 | 75 | 93% | 3x4 / 6x6 | 216,000 | LL4 (also The Last Handler's reward) |
| K9 Camera Vest (Coyote) | 4 | 55 | 92% | 3x4 / 6x5 | 180,000 | LL3 (full length, its own camera on top) |

Since 0.25 the jackets are armoured rigs to the game (copies of the Tasmanian Tiger SK plate carrier, with their one load grid): the pocket, tactical and camera vests take a front and a back plate, the assault vest side plates as well, any of the game's own plates (fit them in the jacket's inspect window, or drag them onto it). A plate where a round comes in takes it first, with its own class, and wears down its own durability (kept with the plate, repaired like any plate); the vest's own armour (the table below) is the rest. Every jacket but the camera vest has a slot for the K9 action camera. Your own rig slot won't take a dog's jacket.

In the inspect window and lying on the ground, the dog's gear shows as itself, in place of the game item it's copied from: the model as it was made (not the copy fitted round the dog, which looks stretched off it), at its real size, turned to face you in the inspect window, with the action camera on top of a jacket that has one (tools/convert_item_models.py). The inspect window also shows a jacket's own armour (class, chest and back, and its durability as the dog has worn it), and its empty camera slot pictures the camera.

The dog's jackets, head gear and camera can be insured with the traders like your own gear. Lost with you or with the dog, the insured pieces go to the insurer the game's own way (it may find them and mail them back), handed over by the server mod since the game only looks at your own gear.

Besides the jacket, the dog has a **Head** slot in the DOG tab (a camera still in its old Camera slot moves onto its jacket when the game starts). What's in them goes into PMC raids with the dog and is lost if you die, or if the dog is killed and you leave its gear on it (buy more from Dr. Yang by loyalty level).

| Gear | Slot | Size | Price | Available |
| --- | --- | --- | --- | --- |
| K9 Ballistic Goggles (Tan) | Head | 2x1 | 38,000 | LL2 |
| K9 Ballistic Goggles (Black) | Head | 2x1 | 42,000 | LL2 |
| K9 Camera Headset (Black) | Head | 2x2 | 140,000 | LL4 |
| K9 Action Camera (Black) | On the jacket | 1x1 | 60,000 | LL3 |

The goggles and headset are fitted to the dog's skull (lens over the eyes, strap under and behind the ears, shrink-wrapped onto the fur) and fixed to its head. The action camera clips onto the back panel of whichever jacket the dog wears, lens forward; the camera vest has one of its own, so the clip-on one doesn't show on it. All are custom models cut down by `tools/convert_gear.py` (head gear) and `tools/convert_attachment.py` (the camera, one copy per harness).

The harnesses are real models (decimated from about two million triangles each to 9,000 (collars 3,000), fitted to the dog, snugged down onto its fur (pouches and handles keep their height) and skinned to its skeleton in Blender by `tools/convert_gear.py`; icons by `tools/render_gear_icons.py`). Since 0.21 they replace the six coloured jackets: each took over one old jacket's item (so quests, offers and saved gear carry on), and the two left over (hi-vis basic jacket, coyote heavy armour) become the pocket vest and the assault vest when the game starts, with whatever's inside them.

### Asian rifles (WTT Content Backport)

With the WTT Content Backport installed, Dr. Yang also sells its two Asian rifles, built as WTT's own traders sell them: the **Norinco QBZ-191** (China, 5.8x42) at LL3 and the **Howa Type 20** (Japan, 5.56x45) at LL4, with the QBZ-191's magazine and the 5.8x42 rounds. Without it they're simply not on his shelves. There is no Chinese body armour in the game, so the Asian gear is what exists: the Type 56 Chicom harness, the Japanese-styled DevTac Ronin helmet and respirator, Samurai Menpo masks and the Korean-made Glock drum.

### Ammunition, magazines and medical stock

For the guns the K9 quests give he also sells their ammunition (7.62x39 HP/PS/BP, 7.62x25 TT LRNPC/Pst, 9x18 PM P/PBM, 7.62x54R T-46M/LPS/PS) and magazines (AK 10- and 30-round and 6L10, TT-105, PM, PPSh 35-round and 71-round drum, Mosin, SVD, RPD 100-round box, PK 100-round box), 5.56x45 M855/M855A1 and STANAG magazines for the Type 20, and more medical and veterinary supplies: Esmarch and CAT tourniquets, Analgin, Ibuprofen, Augmentin, Zagustin, morphine, a CMS kit, syringes, saline, hydrogen peroxide, Aquapeps, medical tools, a bloodset and an ophthalmoscope. Better rounds and bigger magazines unlock at higher loyalty. All are the game's own items at handbook price plus 15%.

### Dog supplies

- **Treats** (15%) and **premium dog food** (100%) feed the dog like any dog food.
- The **bed liner** and **thermal dog bed** each do one bed upgrade (instead of the fabrics).
- A **training kit** is used with TRAIN in the DOG tab (kennel level 1): each of 3 levels lets the dog search 5 m further (25 m, up to 40 m).
- **Bandages** heal the dog a little (army 5%, aseptic 4%); **Surv12** heals 40%.
- The **collars** go in the dog's collar slot in the DOG tab.

## Default controls

| Key | Action |
| --- | --- |
| F7 | Stay / follow |
| Shift+F7 | Lie down (in the hideout: go to bed) |
| F8 | Recall with whistle |
| Shift+F8 | Bring the dog to you: straight in front of you (when it's stuck) |
| F9 | Rename your dog in game (within 3 m) |
| F10 | Pet reaction (within 2.5 m) |
| Shift+F9 | Hideout: fill the food dish from your gear or stash (within 3 m of bed) |
| Shift+F10 | Hideout: fill the water dish from your gear or stash (within 3 m of bed) |

Existing user key settings are preserved. Shift shortcuts take precedence. Normal commands are ignored while the inventory is open or the cursor is unlocked.

## Hideout changes

The old setup depended on the raid GameWorld.MainPlayer reference and raid floor layers. Version 0.3.1 attaches directly to the initialized HideoutPlayer, finds floor colliders using the general physics mask, rejects player colliders/steep surfaces, and retries placement when needed. Since 0.18.1 the dog lives on its own object, made while the hideout loads: the dog and kennel are there in the hideout overview before you click Enter, are hidden while you're away from the hideout (menus, raids), and are cleaned up with the hideout player. No NavMesh is required for hideout placement. Where no NavMesh exists the dog rests at the bed; F8 repositions it near you.

## Retained behaviour and limits

Crouching/prone selects sneak movement and idle. Resting includes sitting, lying, sleeping and get-up transitions. The collar (one of three collar models, fitted mid-neck and skinned like the harnesses) moves with the neck.

Dish levels are real and saved (see Hunger and thirst). Petting plays a dog response without animating player hands. Whistles, happy sounds and rare whining are local. Recorded sounds ship in `sounds` beside the DLL (`tools/convert_sounds.py` cuts and levels them): four barks (one picked at random), a whine, a shake (sometimes, when it gets up from lying), and breathing: a seamless loop of the panting, cleaned of its background noise, that plays all the time, heard only close by, quiet and slow at rest, softer asleep, harder and faster after a run or when it's hurt (Audio > Breathing volume, 0.35). Recorded and synthesized sounds are all levelled to the same loudness, and the recordings are denoised. Whistle and happy are synthesized unless whistle.wav / happy.wav are added; any sound can have takes named <name>_1.wav ... (16-bit PCM WAV, mono/stereo, 8–96 kHz, under 10 MB).

## Moving the kennel

With Hideout Extensions installed, right-click the kennel's paw icon on the hideout's overview and the kennel follows your mouse (R turns it; Shift+R a little); left-click puts it down where it fits, right-click cancels. A kennel put somewhere by hand stays there, even outside its room behind the emergency wall.
