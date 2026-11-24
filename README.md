# The Long Dark - Steam-Railway

> A coal-fired steam railway for Great Bear, a mod for The Long Dark (MelonLoader). You stoke the firebox, build up
pressure, open the regulator and drive your own steam locomotive along the old railway lines of the region.

> The mod needs no DLC. The locomotive, the track and every effect are made by the mod itself;
the only recordings are a few sounds with their own licences (see Credits).

*(Note: This project is in active development.)*


# ⚠️**Warning:** This mod changes the terrain on the island: Closed passages will now be open; destroyed bridges will be repaired; and most of the train cars that were stranded on the tracks will no longer be there. Back up your save files!

---

## Requirements

- The Long Dark, MelonLoader 0.7.x
- `ModData.dll` (saves the mod's data with each save game)
- ModComponent (loads the dynamite item and the bridge repair recipes from the mod's item pack)
- Optional: ModSettings. With it the mod's options appear under Options > Mod Settings > Steam Railway; without it
  they stay in `UserData\MelonPreferences.cfg`.

## Install

Copy `SteamRailway.dll` into the game's `Mods` folder and start the game. That is all: the DLL carries the locomotive
model, the sounds, the item pack and the map data, and writes what it needs into `Mods\SteamRailway\` and
`Mods\SteamRailwayDynamite.modcomponent` on start. The item pack is written before ModComponent reads the Mods folder,
so the dynamite item is there from the very first start, with no restart.

### Updating

When a new version changes a data file (lines, edits, terrain, obstacles ...), the new file replaces the old one on the
next start. A file you edited by hand is copied to `Mods\SteamRailway\Backup\<date>\` first, so nothing is lost; a file
the new version did not change keeps your edits. `Mods\SteamRailway\ShippedData.txt` records what was written.

## Playing

### The locomotive

- The train is located on the coastal highway map where its tracks end.
- Climb into the cab through the right-hand door. Everything is operated by clicking the levers and valves.
- Shovel coal into the firebox and light it. Water in the boiler and the tender is turned into steam; pressure
  rises with the boiler temperature. Open the regulator to move, set the reverser to choose the direction,
  use the steam brake and the hand brake to stop.
- Fill the tender with water: pour your own water into the tender hatch, shovel snow in, or draw it with the steam
  pump from an ice hole you cut yourself (the train must stand within 40 m of the hole).
- Switches on the lines are thrown by holding on their stands.
- The locomotive moves from one region to the next across the transition points of the lines. On foot you can cross them
  too (for example Transfer Pass to ZOC): you are held still until you have been put on the ground at the line's end,
  so the crossing does not end in a fall.
- The fusible plug and the snow hatch can be clicked from the front, the back and both sides of the locomotive.
- Snow settles on the locomotive as it snows (four stages on the roof, boiler, tanks and running boards; a driven dusting
  on the front faces; snow packed into the cowcatcher while running) and melts away in the warmth.

### Driving guide

The cab has no keyboard controls: you look at a lever, valve or gauge and use it with the game's normal **interact**
click. Where a lever has two directions, its hover text tells you that the **alternative** click (the game's secondary
use) does the other one. Every control shows its state in its hover text.

#### Starting from cold

1. **Light the fire.** Click the fire hole (firebox door) to open the game's fire menu: put in coal (or wood) and light it.
   The hover text shows how long the fire will burn. A burning fire heats the boiler. The *heat gauge* shows the firebox
   temperature and the *pressure gauge* the steam pressure in PSI. Pressure rises slowly, so give it time: you can pass
   time in the cab, the boiler runs on game time.
2. **Check the water.** The *water glass* shows the boiler water level in percent and its temperature, the *tender gauge*
   the water in the tender in litres. Keep the boiler between about a third and nearly full (see Water below).
3. **Wait for pressure.** The engine pulls from about 5 PSI and the pull grows with the pressure up to 180 PSI, where the
   safety valve opens. The steam brake only holds with at least 20 PSI behind it.
4. **Release the brakes.** The locomotive starts with both brakes on: switch off the **hand brake** and the **steam brake**.
5. **Choose the direction with the reverser.** Click for FORWARD, alternative click for REVERSE; in the middle it is
   NEUTRAL and the engine does not pull. It can only be thrown while the train is (almost) stopped.
6. **Open the regulator** a notch at a time (10 notches): click to open, alternative click to close. The more notches,
   the harder it pulls and the more steam it uses. Top speed is 15 m/s (54 km/h); the pull weakens as speed grows.
7. **To stop:** close the regulator, apply the steam brake (and the hand brake to hold the train once it has stopped),
   and put the reverser in neutral.

#### The controls

| Control | What it does |
|---|---|
| Regulator | Lets steam to the cylinders: 0 to 10 notches. The pull is regulator x pressure x reverser. |
| Reverser | Forward, neutral or reverse. Only while the train is (almost) stopped. |
| Steam brake | On or off. Strong (1.5 m/s²), but it needs 20 PSI; with less, the hover text says it is not holding. |
| Hand brake | On or off. Weaker (0.8 m/s²), needs no steam: use it to hold the train once it has stopped. |
| Injector | Medium only. While open, feeds water from the tender into the boiler (about 6 L/min); the cold water cools the boiler a little. |
| Blower | Medium only. Draws more air through the fire: it burns hotter (the boiler heats faster) and uses its fuel up faster. |
| Fire hole (firebox) | Opens the fire menu to feed and light the fire. An open door lights up the cab but wastes some heat. |
| Damper | Puts the fire out on purpose: click twice. The coal that is left is kept (up to 24 h) and is added to the next fire. |
| Whistle | Sounds the whistle. Animals nearby run from it. |
| Lamps | The headlamps and the cab light. They run on the generator's steam: over 15 PSI is needed. |
| Folding bed | Unfolds or folds the bed in the cab. |
| Cupboard | The locker in the cab for your things. |
| Water pump | Draws water from an ice hole you cut into the tender (see Water). |
| Water tank filler | Pours the water you carry into the tender. |
| Snow hatch | Shovels snow into the tender (from outside the cab). |
| Fusible plug, boiler repair | Click them from outside to replace a melted plug or repair a worn boiler (materials needed). |

#### The gauges

| Gauge | Shows |
|---|---|
| Pressure | Steam pressure in PSI. In Medium also the boiler and safety valve condition in percent. |
| Water glass | Boiler water level (percent) and boiler temperature. |
| Tender | Water in the tender (litres out of 2000) and snow still melting. |
| Speed | Speed in km/h. |
| Heat | Firebox temperature, or "out". |

#### Water

- The boiler holds 600 L and uses water as it works. The tender holds 2000 L. The injector (Medium) or the automatic
  feed (Simple) moves water from the tender into the boiler.
- Fill the tender in one of three ways: pour water you carry into the **tank filler**; shovel **snow** into the snow hatch
  (10 kg a shovel, up to 300 kg; it melts with the boiler's heat); or use the **water pump** next to an ice hole you cut
  yourself (the train must stand still, within 40 m of the hole). The pump runs on steam above 30 PSI; below that it is
  worked by hand, slowly.

#### What can go wrong (Medium)

- **Low water.** With less than about 15 percent water in the boiler, the fire burning and the boiler hot, the
  **fusible plug melts**: the pressure is lost, the fire goes out, and the plug has to be replaced before the firebox burns
  again. Running the boiler dry at high pressure can burst it. In Simple mode a dry boiler only puts the fire out.
- **Over pressure.** The safety valve opens at 180 PSI and vents steam (and a little water). A worn valve opens later and
  strains the boiler. A boiler at 260 PSI or more, a dry one above 80 PSI, or a worn-out one above 60 PSI explodes.
  An explosion hurts badly but never kills.
- Open the regulator gradually and watch the water: the more steam is drawn, the faster the water and the heat go.

#### Hints

- Keep the boiler warm and the fire lit while you stop for a short while; a cold boiler takes long to get back to
  pressure. When you leave for longer, put the fire out with the damper to save the coal.
- A heavy train is slow to start and long to stop: only the locomotive has brakes, so brake early, especially downhill.
  Coupled wagons add their weight.
- Slopes pull the train: the engine climbs the 12 to 13 percent grades of the original rails, the steepest ones slowly,
  and it picks up speed downhill by itself.
- Throw a switch before you reach it. A rock, a wagon or another train in the way stops you.

### Realism (setting `Realism`)

- **Simple:** the boiler is fed automatically, you only handle regulator, reverser and brakes.
- **Medium:** you run the injector and the blower by hand; the safety valve wears out; a low water level melts the
  fusible plug and can end in a boiler explosion (it hurts badly but never kills).

### Difficulty (setting `Difficulty`, for new games)

- **Easy:** a new game starts with the regions as the mod changed them.
- **Hard:** the regions start as in the original game and you open the line yourself:
  - **Rockfalls** and fallen trees block the track. The train stops at the first rock it runs into.
  - **Stranded wagons** can be pushed by the locomotive, coupled and pulled.
  - **The broken bridge** in the Ravine is repaired in five stages.
  - The ground around the lines is smoothed from the start; only the ground inside the circles of the obstacles that are
    still in the way waits for them to be cleared.

The difficulty is fixed when a game is started and stays with that save.

### Dynamite

- Craft **Blasting charge** at a workbench: cloth, fishing line, scrap metal and 0.3 kg of gunpowder, with a lit fire and light.
- In Hard mode, stand in a rockfall and look at a rock the clearing will remove; hold to place the charges
  (one charge per 25 m of radius). Lay the cable by setting the detonator box down 12 to 30 m away, then press its plunger.
  The rockfall is cleared; stay back, the blast burns anyone who is too close, and a blast close by makes your ears ring
  (this can be turned off, see Settings).

### Wagons

- Wagons stand where the region put them. The locomotive pushes them, they roll on the lines, cross switches and
  keep their momentum; a hard knock shakes the screen. Wagons stop against each other and against the locomotive, also
  when they stand on lines running alongside each other (for example at the start of a branch).
- Bring the locomotive's coupler against a wagon's coupler and hold on the coupling to couple or uncouple.
  Coupled wagons add their weight to the train and travel with it into the next region.
- A wagon (or a group of wagons coupled to each other) pushed into a region transition rolls out into the next region on
  its own and keeps rolling there, even while you are elsewhere: when you get to that region it stands where it would have
  got to in the time that has passed (over the switches as they are set, stopped against what stands in its way). If you
  follow it at once it is still rolling on, at the speed it left with. Wagons that were coupled stay coupled.
- Loose items lying in or on a wagon (logs, loot in a crew speeder) move with it, also across regions, and are put back
  where they lay. Items you take or put into a container stay yours.
- The crew speeders can cross regions too; their doors work after the crossing.

### Wildlife

- A moving train frightens animals within 30 m. A train that stands still and quiet does not, so an animal that is
  hunting you does not lose you next to it.
- The horn scares all animals within a 140-meter radius.
- Animals die if they are hit by the moving train.

### Repairing the bridge

In Hard mode a construction board stands at the end of the broken bridge. Click it to open a workbench-style menu that
lists the stage you are on. Each crafted "hour" of work spends that hour's materials and passes one hour of game time;
choose how many hours to work with the quantity selector. The work can be cancelled and continued later.

| Stage | Hours | Materials per hour |
|---|---|---|
| Clear the debris | 3 | 1 cloth |
| Reinforce the footings | 4 | 2 reclaimed wood, 1 scrap metal |
| Set the main girders | 6 | 2 scrap metal |
| Lay the sleepers and deck | 5 | 2 reclaimed wood |
| Lay the rails | 4 | 2 scrap metal |

Every stage also needs a tool: you choose one of the usable tools in the menu (hatchets, hacksaw, tool kits).
The scaffold appears over the gap as the stages are completed. The last stage clears the bridge and lays the rails.

## Settings

With ModSettings installed, open Options > Mod Settings > Steam Railway. The same options are in
`UserData\MelonPreferences.cfg`, category `SteamRailway`:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | true | Build tracks and trains in Great Bear regions. |
| `Realism` | Medium | Simple or Medium (see above). |
| `Difficulty` | Easy | Easy or Hard, read when a new game starts. |
| `Tinnitus` | true | The ringing in the ears after a blast close by. Turn off if it bothers you. |
| `ObjectEditor` | false | F11 turns the editors on (see below). Leave off for normal play. |
| `DeveloperTools` | false | F4 puts the locomotive, ready to run, on the nearest line where you look. |
| `RouteEditor` | false | F8 add point, F7 remove last point, F6 start a new route, F9 rebuild this scene, F10 save routes. |
| `Verbose` | false | Extra log messages. |

The mod's sounds follow the game's master volume and sound-effects volume, and pause with the game.

## Editors (for authors)

F11 switches between the object editor (move, remove and copy objects, mark rockfalls, wagons and trees), the terrain
and track tools and the obstacle editor (circles for rockfalls and broken bridges). Edits are kept in
`Mods\SteamRailway\` as JSON files (`Lines`, `Edits`, `Terrain`, `Tunnels`, `Obstacles`, `Custom`); a file you edit
is kept (and backed up) when an update ships a new version of it, see Updating.

## Credits

- Locomotive, tracks, effects, and all code: Hamsi Bugulama (with occasional support from AI).
- Sounds: rolling, whistle and lever recordings by Work With Sounds (Konrad Gutkowski, Museum of Municipal
  Engineering) from Wikimedia Commons, CC BY 4.0, cut and looped for this mod. Details in `Assets\SOUND_CREDITS.txt`.
- Everything else, including the blast, crash, tree, wagon and bridge work sounds, is generated by the mod.

## If you'd like to support me and my mods:
### https://buymeacoffee.com/karsontr13
