# Building

`./build.sh` builds the mod and installs it into the game; `./build.sh check` only compiles; `./build.sh dist`
builds into `GameData/` here without touching the game. It needs the .NET SDK, a copy of the game (`KSP_DIR=/path`
if it is not where Steam puts it), and `Keystone.dll` from [Keystone](https://github.com/IshiakiZ/ksp-keystone):
the copy installed in the game, a built copy of that repository beside this one, or `KEYSTONE=/path/to/Keystone.dll`.

This repository is made from a workspace that holds several mods side by side; changes arrive here from there.

## Comparing one start with another

Set `list = some-name.txt` by hand in the mod's settings file (`GameData/Keystone/PluginData/WarmStart.cfg`)
and at the main menu two files are written into `GameData/WarmStart/PluginData`:

* `some-name.txt`: every texture the game has, in the order of its list, each with its size, format,
  number of smaller copies, wrap and filter, what the game notes of it (normal map, readable, squeezed)
  and, where its pixels can be read, a sum of them
* `defs_some-name.txt`: every description of a bundle the game has, with every asset in it, what each
  depends on, and the game's list of bundle files

Start once with `on = False` and once with it on, with different names, and compare the files: they should
be the same byte for byte. That is how the mod was checked (see [How it works](How-it-works.md)).

The game's log (`KSP.log`) has a line from Warm Start for each thing it hands over, with how long it took,
and one at the main menu with every wait of more than 0.3 s between two frames of the start.

## By hand

`readers = 1` (to 16) in the same settings file sets how many threads read texture files ahead. It is
worked out from the number of processors (one fewer, between 2 and 8) if not set. A disk that is slower
for being read in several places at once, as a spinning one may be, could do better with fewer: nobody has
tried.
