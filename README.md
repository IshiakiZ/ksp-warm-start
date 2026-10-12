# Warm Start

Kerbal Space Program starts in less than half the time, because what it works out afresh at every start is kept from
the last one: the expansions' files unpacked, the textures converted, the small bundles' descriptions.

![From starting the game to its main menu: 55 seconds as the game ships, 21 with Warm Start (measured)](src/WarmStart/img/warm-start.gif)

For Kerbal Space Program 1.12.x. **Needs [Keystone](https://github.com/IshiakiZ/ksp-keystone)**, a separate download
([on SpaceDock](https://spacedock.info/mod/4665/Keystone)): this mod does not load without it.

## Install

Copy the `WarmStart` folder from the download into your KSP's `GameData`, beside `Keystone`. The first start after
that is as slow as ever, and a minute of work goes on in the background at its main menu. From the second start on
it is fast.

## Good to know

* **Measured:** to the main menu in 21 s instead of 55 on a Mac, and in 18 s instead of 47 on Windows 11.
* It takes about a gigabyte of disk (`GameData/WarmStart/PluginData`; deleting that folder loses only the time to make
  it again) and no memory.
* None of the game's code is changed. When a mod is updated, added or taken out, only what was kept of its files is
  made again.
* With **KSP Community Fixes** installed, which loads the game its own faster way, Warm Start does nothing: use one
  or the other.
* Its page in the Keystone window (the keystone button on the game's toolbar, or Alt-K; Option-K on a Mac) has a
  switch for each part, and says how long the start took and which mods the seconds went to.

## Works on

Windows 11 and Mac: measured. Linux: never run (it is plain C#, with nothing of one system's in it).

**More:** [the long description](https://github.com/IshiakiZ/ksp-warm-start/wiki/The-long-description) (everything in full, and what was
measured on each system) and the rest of the [wiki](https://github.com/IshiakiZ/ksp-warm-start/wiki).

## Licence

[CC BY 4.0](LICENSE): use it, change it and pass it on as you like, as long as you credit Ishiaki and link back here.
