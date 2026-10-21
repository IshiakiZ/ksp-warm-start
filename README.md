# Warm Start

Kerbal Space Program starts in less than half the time, because what it works out afresh at every start is kept from
the last one: the expansions' files unpacked, the textures converted, the small bundles' descriptions, the
parts' drag cubes.

![From starting the game to its main menu: 55 seconds as the game ships, 21 with Warm Start (measured)](src/WarmStart/img/warm-start.gif)

For Kerbal Space Program 1.12.x. **Needs [Keystone](https://github.com/IshiakiZ/ksp-keystone)**, a separate download
([on SpaceDock](https://spacedock.info/mod/4665/Keystone)): this mod does not load without it.

## Install

Copy the `WarmStart` folder from the download into your KSP's `GameData`, beside `Keystone`. The first start after
that is as slow as ever, and some twenty seconds of work go on in the background at its main menu. From the second start on
it is fast.

## Good to know

* **Measured:** to the main menu in 21 s instead of 55 on a Mac, and in 18 s instead of 47 on Windows 11.
* It takes about a gigabyte of disk (`GameData/WarmStart/PluginData`; deleting that folder loses only the time to make
  it again) and no memory.
* **After a mod is added, updated or taken out,** ModuleManager makes the game work out every part's drag cubes again
  (half a minute for a thousand parts). From 0.3.0 the cubes of every part that has not changed are handed back: 15 s
  of part compiling came down to 3 s here, with 480 parts.
* **With the Harmony mod** (not needed), the game's look-ups of models, pictures and sounds go by a table instead
  of a walk through every one (a tenth off a start with thousands of parts), and from 0.4.0 each part's icon is made
  without copying and waking all that the game throws away (0.71 s of icons came down to 0.44 s here, with 474 parts).
  From 0.5.0 what the icons' checks learn is kept from one start to the next (33 parts checked instead of 87).
  These, the localiser's answers and the asks for a flight (below) are the only parts that change the game's code; each
  checks itself against the game's own and has a switch. Without Harmony none of the game's code is changed.
* **From 0.5.0 the localiser's answers are kept while the game loads** (with the Harmony mod): every part module's names
  and descriptions are looked up hundreds of thousands of times at a start, the same few thousand words. A tenth of a
  second here with 474 parts; let go at the main menu.
* **From 0.5.0 the game's asks for a flight while it loads are answered at once** (with the Harmony mod): every part asks
  three times whether a flight is going on, and each ask was a search through everything loaded, longer with every part.
  On a made-up install of 4,755 parts, 37 s of parts came down to 14 s; a few tenths of a second here, with 474.
* **From 0.5.0 the models are read one after another**, where the game waits a frame after every one: the models'
  stretch of a start 0.9 s instead of 1.6 s and more here (642 models), the main menu about a second sooner.
* With **KSP Community Fixes** installed, which loads the game its own faster way, Warm Start leaves the loading to
  it and, from 0.5.0, keeps only the parts' drag cubes across mod changes, the one thing it does not do (tried beside
  it: 431 parts' cubes handed back after a change).
* Its page in the Keystone window (the keystone button on the game's toolbar, or Alt-K; Option-K on a Mac) has a
  switch for each part, and says how long the start took and which mods the seconds went to.

## Works on

Windows 11 and Mac: measured (what 0.3.0 and 0.4.0 add ran on Windows too, 21 s to the main menu; 0.5.0's on the Mac only).
Linux: never run (it is plain C#, with nothing of one system's in it).

**More:** [the long description](https://github.com/IshiakiZ/ksp-warm-start/wiki/The-long-description) (everything in full, and what was
measured on each system) and the rest of the [wiki](https://github.com/IshiakiZ/ksp-warm-start/wiki).

## Licence

[CC BY 4.0](LICENSE): use it, change it and pass it on as you like, as long as you credit Ishiaki and link back here.
