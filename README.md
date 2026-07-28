# Hide Premium Hero Skins

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for Hearthstone that shows opponent **Diamond, Legendary, and Mythic hero skins** (and optionally Pixel skins) as the default class portraits during games.

Fancy 3D portraits, animated frames, and low-health metamorphosis showcases can be a lot of visual noise. With this mod, an opponent playing Mythic Tess or Diamond Jaina just looks like plain Valeera or Jaina on your screen — including the mulligan, history tiles, and end-of-game screen. Skin transform effects (some skins morph at low health) are reverted the same way, with the transform showcase animation suppressed.

Earned prestige is deliberately respected: **Honored (1000-win) portraits are kept** by default, and a **500-win golden hero** still shows as the golden default portrait next to its golden hero power — only shop cosmetics are stripped, not achievements. Battlegrounds is exempt (hero identity is gameplay information there), and collection previews are never touched, so browsing your own skins works normally.

Purely cosmetic and strictly local — only what *your* client renders changes. Nothing is unlocked, and the opponent sees their skin as usual.

> ⚠️ **Disclaimer:** All client-side mods technically violate Blizzard's Terms of Service and carry a ban risk. Use at your own risk. This project is not affiliated with or endorsed by Blizzard Entertainment.

## How it works

Every hero visual in a match loads through `Entity.LoadCard(cardId)`. This plugin installs a [Harmony](https://github.com/BepInEx/HarmonyX) prefix that rewrites the cardId to the class's default hero before the load, using the game's **own classifiers** for tier detection: `RewardUtils.IsShopPremiumHeroSkin` marks the custom-frame tiers (Diamond/Legendary/Mythic), the `MYTHIC` tag distinguishes Mythic, and premium quality marks Diamond. Pixel skins are matched by name plus a configurable cardId/dbfId list.

A safety gate keeps gameplay intact: only cardIds present in the game's cosmetic-skin database (`CardHero`) are ever treated as skins, so gameplay hero cards — Reno, Lord Jaraxxus, adventure bosses — are never touched. On revert, skin behavior tags (emotes, attack animation, corner decorations, diamond markers) are re-synced with the default hero, and a small companion patch cleans up custom hero frames the game would otherwise leave mounted. Skin transform variants arrive as a second `LoadCard` with their own cardId and are reverted identically, with the transform showcase tags zeroed so the change renders as visual-less.

## Installation

You need BepInEx 5 in your Hearthstone folder — via **either** route — then the mod DLL.

**Option A — via Firestone (easiest if you already use it):**
1. With Hearthstone closed, open Firestone → Settings → General → Mods and enable mods.
   This installs Firestone's integrated BepInEx into the game folder.
2. Launch Hearthstone once and quit, so `<GameDir>\BepInEx\plugins\` gets created.

**Option B — plain BepInEx:**
1. Download [BepInEx 5.4.x **x64**](https://github.com/BepInEx/BepInEx/releases) and extract
   the zip directly into your Hearthstone folder (next to `Hearthstone.exe`), so you end up
   with `<GameDir>\winhttp.dll` and `<GameDir>\BepInEx\`.
2. Launch Hearthstone once and quit.

**Then, for both routes:**
1. Download `HsHidePremiumSkins.dll` from [Releases](../../releases) and drop it into
   `<GameDir>\BepInEx\plugins\`.
2. Launch Hearthstone. Verify by finding `Hide Premium Hero Skins ... loaded.` in
   `<GameDir>\BepInEx\LogOutput.log`, or by queueing into an opponent with a premium skin.

## Configuration (optional)

Edit `<GameDir>\BepInEx\config\HsHidePremiumSkins.cfg` (created on first launch):

```ini
[Features]
## Master toggle: revert the opponent's hero skin to the default class hero during games.
HideOpponentSkins = true

## Also revert your own hero skin.
AlsoHideOwnSkin = false

[Filters]
## Revert Mythic-tier skins (fully animated 3D portraits).
RevertMythic = true

## Revert Diamond-tier skins (diamond 3D portraits).
RevertDiamond = true

## Revert Legendary-tier skins (animated portraits).
RevertLegendary = true

## Revert Pixel skins (card name containing 'Pixel', plus PixelSkinCardIds).
RevertPixel = true

## Revert Honored (1000-win golden) portraits. Off by default.
RevertHonored = false

## Revert EVERY non-default skin, ignoring the filters above.
RevertAllSkins = false

## Comma-separated cardIds (HERO_02ba) or dbfIds (116081) always treated as Pixel skins.
## Defaults: Northrend Arthas, Eternal Malfurion, Hearthglen Jaina, Orgrimmar Thrall.
PixelSkinCardIds = 116078,116079,116080,116081
```

Tip: when a skin is *not* reverted, the log prints `Skin kept: <cardId> (<name>)` — that's the cardId to add to `PixelSkinCardIds` (or a filter to enable) if you wanted it hidden.

## Building from source

Requirements: a .NET SDK (8/9/10), BepInEx 5 installed in the game folder (the project references its DLLs from there).

1. Clone the repo.
2. Edit `<GameDir>` in `HsHidePremiumSkins.csproj` to your Hearthstone install path.
3. `dotnet build -c Release` — the post-build step copies the DLL into `BepInEx\plugins` automatically.

No game files are included in this repository; the project compiles against `Assembly-CSharp.dll` from your own installation.

## Compatibility

- Built and verified against the July 2026 Hearthstone build.
- Hearthstone patches can rename or change the hooked methods (`Entity.LoadCard`, `Actor.LoadCustomFrame`). If the mod stops working after a game update, check `LogOutput.log` for Harmony errors and watch this repo for an updated release.
- Coexists with other BepInEx/Firestone mods, including the author's other Hearthstone mods.

## Uninstall

Delete `HsHidePremiumSkins.dll` from `BepInEx\plugins`. To remove BepInEx entirely, delete `winhttp.dll` from the game folder.

## License

[MIT](LICENSE)
