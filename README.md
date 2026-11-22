# The Long Dark - Steam-Railway

> A coal-fired steam railway for Great Bear, a mod for The Long Dark (MelonLoader). You stoke the firebox, build up
pressure, open the regulator and drive your own steam locomotive along the old railway lines of the region.

> The mod needs no DLC. The locomotive, the track and every effect are made by the mod itself;
the only recordings are a few sounds with their own licences (see Credits).

*(Note: This project is in active development.*


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
- The locomotive moves from one region to the next across the transition points of the lines.
- The fusible plug and the snow hatch can be clicked from the front, the back and both sides of the locomotive.
- Snow settles on the locomotive as it snows (four stages on the roof, boiler, tanks and running boards; a driven dusting
  on the front faces; snow packed into the cowcatcher while running) and melts away in the warmth.

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

- Craft **Blasting charge** at a workbench: cloth, rope, scrap metal and 0.3 kg of gunpowder, with a lit fire and light.
- In Hard mode, stand in a rockfall and look at a rock the clearing will remove; hold to place the charges
  (one charge per 25 m of radius). Lay the cable by setting the detonator box down 12 to 30 m away, then press its plunger.
  The rockfall is cleared; stay back, the blast burns anyone who is too close.

### Wagons

- Wagons stand where the region put them. The locomotive pushes them, they roll on the lines, cross switches and
  keep their momentum; a hard knock shakes the screen.
- Bring the locomotive's coupler against a wagon's coupler and hold on the coupling to couple or uncouple.
  Coupled wagons add their weight to the train and travel with it into the next region.
- A wagon (or a group of wagons coupled to each other) pushed into a region transition rolls out into the next region on
  its own: it comes in at the line's end there, in its old order, coupled again, and rolls on at the speed it left with.
- Loose items lying in or on a wagon (logs, loot in a crew speeder) move with it, also across regions, and are put back
  where they lay. Items you take or put into a container stay yours.
- The crew speeders can cross regions too; their doors work after the crossing.

### Wildlife

- Animals are afraid of the train and run away whether it is stationary or moving.
- The horn scares all animals within a 140-meter radius.
- Animals die if they are hit by the train.

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
| `ObjectEditor` | false | F11 turns the editors on (see below). Leave off for normal play. |
| `DeveloperTools` | false | F4 puts the locomotive, ready to run, on the nearest line where you look. |
| `RouteEditor` | false | F8 add point, F7 remove last point, F6 start a new route, F9 rebuild this scene, F10 save routes. |
| `Verbose` | false | Extra log messages. |

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
