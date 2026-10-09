# How it works

## Where a start goes

The game writes a log as it starts, with the time on every line, and names each file as it begins on it.
So a start can be taken apart from its log. This is one start of Kerbal Space Program 1.12.5 with both
expansions, Scatterer, EVE, TUFX, Waterfall and a few small mods (1677 texture files, 642 models, 489
parts), on an Apple-silicon Mac, with every file already in the operating system's memory:

| What the game is doing | As it ships | With Warm Start |
| --- | ---: | ---: |
| Starting, loading its plugins, their first frame | 5.1 s | 5.2 s |
| Sounds (131 files) | 0.9 | 0.9 |
| **Textures** (667 PNG, 972 DDS, 38 others) | **10.7** | **1.0** |
| Models (642) | 1.6 | 1.4 |
| **The small bundles' descriptions** (228 bundles) | **5.0** | **0.1** |
| Configs and patches | 0.7 | 0.7 |
| Parts (489) | 3.2 | 3.1 |
| **The expansions' files** (3) | **18.3** | **0.2** |
| Planets and space centre | 8.5 | 7.9 |
| The main menu itself | 0.9 | 0.9 |
| **From the log's first line to the main menu** | **54.9** | **21.4** |

(ModuleManager, which most installs have, counts a start its own way and says 53.1 and 19.7 seconds for
the same two.) The three lines in bold are 34 of the 55 seconds, and all three are the game working out
something that comes out the same every time.

## The four things, and how the game is handed each

Nothing of the game's code is changed or patched. Each part uses a door the game already has.

### The expansions' files

Making History and Breaking Ground keep their models, scenes and launch sites in three "asset bundles":
`makinghistory_assets` (133 MB), `makinghistory_scene` (34 MB) and `serenity_assets` (288 MB). Each is
packed as *one* block of LZMA, which packs small and cannot be read in part: to use anything in it, all
of it has to be unpacked. The game does that at every start. It reads each file whole into memory, takes
its checksum, and has Unity unpack it there: 1.2 GB comes out of 455 MB.

Unity can also keep a bundle in blocks of LZ4, which are read straight off the disk a block at a time, as
each thing in the bundle is wanted: opening such a bundle takes no time to speak of. And Unity will make
that kind from the other (`AssetBundle.RecompressAssetBundleAsync`). So a copy of each of the three is
made that way, once, in the background at the main menu, and kept (740 MB).

*The door:* the game's loader keeps a list of the bundles it has loaded
(`Expansions.BundleLoader.loadedBundles`, with each one's checksum) and skips any that is on it already:
"already loaded - skipping...", its log says. The list is open to mods. So at the very start the copies
are opened and put on that list, each with the checksum of the game's *own* file, taken when the copy was
made. The game then checks that checksum against the publisher's signature exactly as it always does, so
an expansion whose files are not the genuine ones is turned away as before.

This is also why the game uses less memory with the mod than without (7.0 GB against 8.2 here, in flight
on the launch pad): the files are no longer unpacked whole into memory.

### Textures

The game loads its textures one file at a time, each from start to finish before the next, all on the one
thread that also draws the screen:

| | Files | On disk | Time | Each |
| --- | ---: | ---: | ---: | ---: |
| DDS | 972 | 1.3 GB | 2.2 s | 2 ms: read, and handed to the graphics card as it is |
| PNG | 667 | 130 MB | 8.0 s | 12 ms: unpacked, squeezed into the card's own format (DXT), every time |

A tenth of the data takes four fifths of the time. So what the game makes of each PNG is kept, as the
finished pixels the card takes, in one file (220 MB), from the first start on; at later starts those are
read back and given to the card as they are. DDS files need no keeping, but they are read by several
threads at once, ahead of the game's own thread, which then only hands over: 1639 textures, 1.5 GB, in
0.9 seconds.

*The door:* for every texture file the game first asks its own list (`GameDatabase.databaseTexture`)
whether a texture of that name is there already and whether the file is newer than the last loading; if
it is there and not newer, the file is passed over. That is how the game reloads only what has changed
when asked to reload, and the list is open to mods. So just before the game reaches its textures they are
put on the list, each as the game's own loader would have left it. Each file that was seen to is given,
for as long as the game is going through them, the date the game itself gives a file whose date it
cannot read (the year 1, newer than nothing), and has its own date back afterwards. That date is the one
thing reached into that is not public: a private field, set through reflection.

What the game would have done is kept to in the corners:

* Where two files make the same name the game ends up with the later one. (Squad ships such a pair: a
  PNG and a DDS of one dust particle.) So does this.
* Where a mod brings its own loader for PNG or DDS files, the game would run that as well as its own.
  Files of that kind are then left to the game.
* The few PNG pictures the game treats as normal maps (names ending `NRM`) it makes unreadable, so they
  cannot be taken down afterwards; those are made again from the file, by the game's own rule.
* The list ends in the order the game would have made it in.
* TGA, JPG, MBM pictures, and DDS files in formats the game itself refuses, are left to the game.

### The small bundles' descriptions

The game ships a couple of hundred small bundles (the pages of its manual, the KSPedia, mostly) and mods
may add more. Each holds a short description of what is in it, and at every start the game opens every
one of them only to read that description, and closes it again. They are LZMA too, so opening one means
unpacking all of it: 228 bundles, 68 MB, 5.0 seconds, of which one bundle of 29 MB takes 1.7. Nothing else
is done with them while the game loads; the pages are opened again one at a time when read.

So after a start in which the game read them itself, every description is written down (100 KB) beside
the size and date of its file. At later starts, while every file is as it was, the game is given the
descriptions and opens nothing.

*The door:* the game's reader of these bundles (`KSPAssets.Loaders.AssetLoader.LoadDefinitionsAsync`)
writes a line in the log as the first thing it does, and another as the last but one. A mod may listen
to the log, and is called while the line is being written: in the middle of whatever wrote it. Between
its two lines the reader lists the files whose names end in `ksp` (an ending it holds in a field), opens
each, and puts what it reads on a list; then it names each description after its file, ties each to those
it depends on, and lists the assets.

* At the first line, if every file the game is about to open is as it was, that field is given an ending
  no file has. The game finds no files and opens none.
* At the second line the ending is put back, the kept descriptions go on the reader's list in the order
  of the files the game would have opened, the reader's list of files is made what it would have been,
  and the reader's own two last steps are run over the list. The game then goes on as always.

If anything is not as it was, nothing is touched: the game reads its bundles itself, and what it read is
kept afresh. That start is also when it is checked that this mod lists the files in the order the game
does; only while it does is anything handed over.

### Two small things about frames

The game's loader does a slice of work and then lets a frame be drawn. Two of its habits cost time for
nothing: while one loading picture fades into the next it does a single file per frame, to keep the fade
smooth; and it holds its frame rate to the limit in its settings even while loading, so that whatever
waits a frame for a file waits longer. Both are held off until the main menu is there. Together they
were worth 1.5 seconds before the rest was done, and less since.

## When a file changes

Each kept thing is good while the file it came from has the size and the date it had. Where only the
date is different, what is *in* the file decides (a checksum of it is kept too): so a mod put back in
place, or a game checked over by its shop, costs a moment and not a slow start. The game itself writes
two of its small bundles out afresh every time it reads them, with the same contents; that is the case
this was first needed for.

When a file really has changed, been added or gone: for textures, only that file is made again, by the
game, in the start that follows. For the small bundles, all of them are read by the game once more. For
an expansion's file, the game loads its own and a new copy is made at the main menu.

## How it was checked

A mod that hands the game ready-made things could quietly hand it the wrong ones. So the mod can write
down everything the game ended up with (see [Building](Building.md)), and that was compared between a
start with the mod off and starts with it on:

* **Textures:** all 1674, in the order of the game's list: name, size, format, number of smaller copies,
  wrap, filter, and what the game notes of each; and for the 695 whose pixels can be read (every one
  that came from a PNG among them), a sum of the pixels. Identical, byte for byte, in the first start
  (DDS read ahead, PNG made by the game and kept) and in later ones (everything handed over).
* **Descriptions:** all 226, with their 505 assets, what each depends on and the game's list of bundle
  files. Identical.
* The game's manual opens and shows its pages; the expansions' launch sites are where they were.
* The log has no more warnings or errors with the mod than without.

Also tried: a kept picture whose file's date changed (kept), whose file was replaced (made again, that one
only), and taken away; a start with the mod off in between (the two bundles the game rewrites are
recognised by their contents); each part switched off by itself.

## What is left, and why

Of the 21 seconds that remain here:

* **5 seconds** pass before any mod can do anything, or belong to other mods starting up.
* **8 seconds** are the game building its planets and space centre after everything is loaded: two long
  waits inside Unity (3.6 and 2.0 seconds), and a pause of two seconds that the game asks for by name
  (`WaitForSeconds(2)`, in `PSystemSetup.SetupLaunchSites`) so that terrain is there before a launch site
  of Making History is set down on it. That pause could be shortened only by running the game's clock
  fast, and what it protects is where a launch site ends up; it is left alone.
* **3 seconds** are parts, **1.4** models, **0.9** sounds, and **0.9** is this mod handing over textures.

## Tried, and not kept

* **All the sound files asked for at once.** The game asks for one, waits, asks for the next; Unity reads
  each on another thread. Asking for all 131 at once does away with the waiting, and the result was the
  same list of sounds. But most of the time is in taking each finished sound from Unity, which has to be
  done on the game's own thread either way, and the start was no faster (20.0 seconds with and without).
* **Unity's own loading settings turned up** (how much of each frame it gives to loading in the
  background, how big a buffer it sends textures to the card through). Once the three things above are
  kept, nothing is loaded that way during a start; seven starts with and three without differed by less
  than they differ among themselves.
* **A bundle with nothing in it**, for the small bundles: the game was to be kept busy opening it for a
  frame while the descriptions were put on its list. The game got through it before the next frame began.
  The log's two lines are exact, where a frame is not.

## Beside KSP Community Fixes

[KSP Community Fixes](https://github.com/KSPModdingLibs/KSPCommunityFixes) has a faster loader of its own
among the many things it does, by another road: it replaces the game's loading loop (with Harmony),
reads files on other threads and keeps PNG pictures squeezed. Version 1.41.1 took 31 seconds for the
start above on the same Mac, and 313 for the first one, in which it made its own kept pictures. The two
are not meant to be used together: where it is installed, Warm Start does nothing at all.

## On Windows and Linux

Everything here is plain C# and Unity calls that are the same on every platform, and the kept formats are
the ones every desktop graphics card takes. But it was made and measured on a Mac, and has not been run
anywhere else.
