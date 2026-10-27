# Thundergun

The **Thundergun** from Call of Duty Zombies, in SPT. The BO3 HD model with its real first-person animations, a blast of air that kills everything in front of you and throws the bodies, and a chalk wall buy in your hideout to get it.

Version 1.0.2, for **SPT 4.1.6**.

## Requirements

| Mod | Version | Why |
|---|---|---|
| SPT | 4.1.x (made on 4.1.6) | |
| [Hideout Uncensored](https://sp-mod.com/mod/3167/hideout-uncensored) by **trappuss** ([GitHub](https://github.com/trappuss/SPT-Mods-Release)) | 1.3.0 or newer, **both** its client and server parts | Weapons anywhere in the hideout: the wall buy hands you the Thundergun straight away, outside the shooting range |

Optional:

| Mod | What it adds |
|---|---|
| Kennerz-PaPHideoutModule (by Kennerz, same author) | Pack-a-Punch the Thundergun into the **Zeus Cannon**: four charges per cartridge, a bigger, purple blast and BO3's upgraded shot sound. |

## Installing

1. Close the game and the SPT server.
2. Extract the zip into your SPT folder (the one with `EscapeFromTarkov.exe`). You should end up with:
   - `BepInEx\plugins\Thundergun\` (`Thundergun.dll`, `icons\`, `sounds\`)
   - `SPT_Runtime\user\mods\Thundergun\` (`ThundergunServer.dll`, `bundles.json`, `bundles\`)
3. Start the server, then the game.

## Getting it

The mod gives you nothing for free and mails you nothing. Once your hideout's **side room is open** (the Emergency Wall upgrades done), there's a **chalk outline of the Thundergun** on a wall in your hideout, with a green **ammo case** beside it.

- **The wall:** look at it and buy the Thundergun for **935,000 ₽** (from your stash). It slides out of the wall, BO style, loaded with a full cartridge. If a primary weapon slot is free it's equipped and drawn; otherwise it goes to your stash. The wall sells it again on your first hideout visit after a raid.
- **The ammo case:** buy a full **Pressure Cartridge** for **10,000 ₽**.
- Not enough money or no room in your stash: nothing is charged.

## How it works

- **The shot:** no bullet. Everything in a cone in front of the gun, in clear line of sight, is hit at once: up to 12 m it dies (credited to you, so kills count for quests and stats) and its body is thrown back; up to 14 m it's stunned. Walls and cover stop it; chain-link, grates, glass, grass and other people don't. You and your group are never hit. A visible wave of bending air rushes out with every shot.
- **Pressure Cartridges:** two charges each, shown as FULL or EMPTY. A cartridge can't be swapped until every charge is fired; the spent one drops to the floor, and the fresh one fires once its tech beep ends. Checking your ammo shows exactly how many charges are left. Cartridges can't be unloaded, dragged out of the gun or destroyed while loaded.
- **Handling:** 9.35 kg, and while it's in your hands you move 15% slower and turn 12% slower.
- **Zeus Cannon** (with Kennerz-PaPHideoutModule): 4 charges per cartridge, up to 16 m kill range in a wider cone. Stripping its 115 powers makes it a Thundergun again.

## Settings (F12)

Volume sliders only: **Shot** (the blast), **Reload and handling** (draw, reload, tech beep, dry click, rattle), **Purchase** (BO3's cha-ching), **Wall buy** (the gun coming off the wall) and **Deny** (a refused purchase). The Zeus Cannon's BO3 shot layer follows Pack-a-Punch's own "Pack-a-Punch shot" slider.

Prices are in `SPT_Runtime\user\mods\Thundergun\config.json` (create it to change them):

```json
{ "WallBuyPrice": 935000, "CartridgePrice": 10000 }
```

## Uninstalling

Delete `BepInEx\plugins\Thundergun\` and `SPT_Runtime\user\mods\Thundergun\`. Your profile stays healthy: the mod keeps its items (and anything pointing at them: wishlist, fast panel, favourites, insurance, builds) out of your profile file and adds them back when the server loads. Without the mod they're simply gone.

## Credits

- **Thundergun and its sounds:** Call of Duty: Black Ops / Black Ops III Zombies (Treyarch / Activision). This is a free, non-commercial fan mod; all Call of Duty assets belong to their owners. The BO3 HD model, animations and sounds were exported with Greyhound.
- **BO1 Thundergun port for the BO3 mod tools by TheAllNightFall.** This mod uses two things from it: its sound set (the BO1 shot and the reload, reload-beep and ammo-eject sounds) and its arms rig (the skeleton the first-person animations are retargeted onto). The port's own credits, as it lists them:
  - TheAllNightFall: setting up the weapon files, animations, sounds and textures, and rigging the animations
  - Humphrey: ripping the models and animations
  - prov3ntus: helping make the partial reload animation
  - slimey: animation help and advice
  - MJPW: the Pack-a-Punch camo mask and the ammo light counter
  - Collie and Aszry: the Maya rig used for some of the animations
  - The original Treyarch developers: the weapon models and animations (2010)
- **Hideout Uncensored** by trappuss ([page](https://sp-mod.com/mod/3167/hideout-uncensored), [GitHub](https://github.com/trappuss/SPT-Mods-Release)): holding the gun anywhere in the hideout, beyond the shooting range. Required; not included, install it separately.
- Made by Kennerz.

## Changelog

- **1.0.0:** first release.
- **1.0.1:** hot patch. Traders pay a flat 935,000 RUB for the Thundergun (plus the Pack-a-Punch fee, 500,000 or 5,000 depending on PaP's mode, when infused). A pressure cartridge sells for 10,000 RUB, 2,000 less per spent charge, and 0 when empty. The Thundergun no longer loses durability.
- **1.0.2:** hot patch for the "hands are busy" glitch. The Thundergun can no longer jam, misfeed, misfire or overheat (a malfunction could leave the hands stuck, as its cartridge cannot be pulled out to clear it), and its reload and trigger rules now fall back to the game's own if they ever fail, instead of leaving a reload half done.
