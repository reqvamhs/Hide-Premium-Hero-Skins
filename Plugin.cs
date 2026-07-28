using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HsHidePremiumSkins
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reqvam.hshidepremiumskins";
        public const string PluginName = "Hide Premium Hero Skins";
        public const string PluginVersion = "1.0.0";

        private void Awake()
        {
            SkinPatches.Log = Logger;

            // Config file name must match the assembly name for Firestone's Mod Manager
            ConfigFile fsConfig = new ConfigFile(
                Path.Combine(Paths.ConfigPath, "HsHidePremiumSkins.cfg"), true);

            // Firestone Mod Manager metadata
            fsConfig.Bind("General", "Name", PluginName);
            fsConfig.Bind("General", "Guid", PluginGuid);
            fsConfig.Bind("General", "Version", PluginVersion);
            fsConfig.Bind("General", "DownloadLink", "https://github.com/reqvamhs/Hide-Premium-Hero-Skins");
            fsConfig.Bind("General", "Description",
                "Shows opponent Diamond, Legendary, and Mythic hero skins as the default class portraits during games.");

            SkinPatches.Enabled = fsConfig.Bind("Features", "HideOpponentSkins", true,
                "Master toggle: revert the opponent's hero skin to the default class hero during games.");
            SkinPatches.AlsoFriendly = fsConfig.Bind("Features", "AlsoHideOwnSkin", false,
                "Also revert your own hero skin.");
            SkinPatches.RevertMythic = fsConfig.Bind("Filters", "RevertMythic", true,
                "Revert Mythic-tier skins (fully animated 3D portraits).");
            SkinPatches.RevertDiamond = fsConfig.Bind("Filters", "RevertDiamond", true,
                "Revert Diamond-tier skins (diamond 3D portraits).");
            SkinPatches.RevertLegendary = fsConfig.Bind("Filters", "RevertLegendary", true,
                "Revert Legendary-tier skins (animated portraits, detected by Legendary rarity).");
            SkinPatches.RevertPixel = fsConfig.Bind("Filters", "RevertPixel", true,
                "Revert Pixel skins (card name containing 'Pixel', plus PixelSkinCardIds).");
            SkinPatches.RevertHonored = fsConfig.Bind("Filters", "RevertHonored", false,
                "Revert Honored (1000-win golden) portraits. Off by default.");
            SkinPatches.RevertAllSkins = fsConfig.Bind("Filters", "RevertAllSkins", false,
                "Revert EVERY non-default skin, ignoring the filters above.");
            SkinPatches.PixelSkinCardIds = fsConfig.Bind("Filters", "PixelSkinCardIds",
                "116078,116079,116080,116081",
                "Comma-separated cardIds (HERO_02ba) or dbfIds (116081) always treated as Pixel skins. " +
                "Defaults: Northrend Arthas, Eternal Malfurion, Hearthglen Jaina, Orgrimmar Thrall.");

            new Harmony(PluginGuid).PatchAll();
            SkinPatches.Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
    }

    /// <summary>
    /// Reverts hero skins to the default class hero at Entity.LoadCard time by rewriting
    /// the cardId before load. Tier detection: premium tier (Diamond/Legendary/Mythic,
    /// the custom-frame skins) via RewardUtils.IsShopPremiumHeroSkin, Mythic via
    /// GAME_TAG.MYTHIC, Diamond via premium quality, Legendary as the remainder; Pixel
    /// skins via a configurable cardId/dbfId list. Only cardIds present in the CardHero
    /// DBF are treated as skins, so gameplay hero cards (Reno, Jaraxxus, bosses) are
    /// never touched; Honored (1000-win) portraits are classified by HeroType and kept
    /// unless enabled. Skin transform variants arrive as a second LoadCard with their
    /// own cardId and are reverted the same way, with the transform showcase tags
    /// zeroed so the change renders as visual-less. Hero powers are deliberately
    /// left alone.
    /// </summary>
    public static class SkinPatches
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AlsoFriendly;
        internal static ConfigEntry<bool> RevertMythic;
        internal static ConfigEntry<bool> RevertDiamond;
        internal static ConfigEntry<bool> RevertLegendary;
        internal static ConfigEntry<bool> RevertPixel;
        internal static ConfigEntry<bool> RevertHonored;
        internal static ConfigEntry<bool> RevertAllSkins;
        internal static ConfigEntry<string> PixelSkinCardIds;

        internal static bool On(ConfigEntry<bool> e) => e != null && e.Value;

        internal static bool IsPixelSkin(EntityDef entityDef, string cardId, int dbId)
        {
            string name = entityDef?.GetName();
            if (!string.IsNullOrEmpty(name) &&
                name.IndexOf("pixel", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string list = PixelSkinCardIds?.Value;
            if (string.IsNullOrEmpty(list))
                return false;
            foreach (string raw in list.Split(','))
            {
                string id = raw.Trim();
                if (id.Length == 0)
                    continue;
                if (int.TryParse(id, out int numeric))
                {
                    if (numeric == dbId)
                        return true;
                }
                else if (id.Equals(cardId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The game leaves an existing custom hero frame in place when a def without a
        /// frame prefab loads (early return, no unload). After a skin is reverted, the
        /// vanilla def is frameless, so a frame from an earlier load of the skin def
        /// would survive under the vanilla portrait. This unloads it first.
        /// </summary>
        [HarmonyPatch(typeof(Actor), "LoadCustomFrame")]
        public static class StaleFramePatch
        {
            [HarmonyPrefix]
            public static void Prefix(Actor __instance, object cardDef)
            {
                try
                {
                    if (!On(Enabled))
                        return;
                    // Gameplay only: keep collection preview behavior untouched.
                    if (SceneMgr.Get() == null || SceneMgr.Get().GetMode() != SceneMgr.Mode.GAMEPLAY)
                        return;
                    // Only relevant when a frame is actually mounted on this actor.
                    if (Traverse.Create((object)__instance).Field("m_customFrameController").GetValue() == null)
                        return;
                    string framePrefab = cardDef == null
                        ? null
                        : Traverse.Create(cardDef).Field<string>("m_CustomHeroFramePrefab").Value;
                    if (string.IsNullOrEmpty(framePrefab))
                        AccessTools.Method(typeof(Actor), "UnloadCustomFrame")
                            ?.Invoke(__instance, null);
                }
                catch (System.Exception e)
                {
                    Log?.LogError($"StaleFrame prefix failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(Entity), nameof(Entity.LoadCard))]
        public static class LoadCardPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Entity __instance, ref string cardId)
            {
                try
                {
                    if (!On(Enabled) || string.IsNullOrEmpty(cardId) || __instance == null)
                        return;

                    // Only during an actual game, and never in Battlegrounds.
                    if (GameState.Get() == null)
                        return;
                    if (GameMgr.Get() != null && GameMgr.Get().IsBattlegrounds())
                        return;

                    // Side via the controller tag: present from the first LoadCard,
                    // before the Card object exists.
                    Player friendly = GameState.Get().GetFriendlySidePlayer();
                    if (friendly == null)
                        return;
                    bool isOwn = __instance.GetControllerId() == friendly.GetPlayerId();
                    if (isOwn ? !On(AlsoFriendly) : false)
                        return;

                    EntityDef entityDef = DefLoader.Get()?.GetEntityDef(cardId);
                    if (entityDef == null || entityDef.GetCardType() != TAG_CARDTYPE.HERO)
                        return;
                    if (GameUtils.IsVanillaHero(cardId))
                        return;

                    // SAFETY GATE: only cosmetic skins live in the CardHero DBF.
                    // Gameplay hero cards (Reno, Jaraxxus, bosses...) are not in it -> never touched.
                    int dbId = GameUtils.TranslateCardIdToDbId(cardId);
                    CardHeroDbfRecord heroRec =
                        GameDbf.CardHero.GetRecords().FirstOrDefault(r => r.CardId == dbId);
                    if (heroRec == null)
                        return;

                    // Honored (1000-win) portraits are prestige, not shop cosmetics.
                    // They can carry tags that fool the tier filters, so classify them
                    // by their actual HeroType and give them their own toggle.
                    bool isHonored = heroRec.HeroType == Assets.CardHero.HeroType.HONORED;
                    if (isHonored && !On(RevertHonored) && !On(RevertAllSkins))
                    {
                        Log?.LogInfo($"Skin kept (honored): {cardId}");
                        return;
                    }

                    // The game marks Diamond/Legendary/Mythic skins (the custom-frame tiers)
                    // via the HERO_FRAME_TYPE tag; IsShopPremiumHeroSkin is its own check for it.
                    bool isPremiumTier = RewardUtils.IsShopPremiumHeroSkin(entityDef);
                    bool isMythic = GameUtils.IsMythicHero(entityDef);
                    bool isDiamond =
                        __instance.GetPremiumType() == TAG_PREMIUM.DIAMOND ||
                        entityDef.HasTag(GAME_TAG.HAS_DIAMOND_QUALITY);
                    bool isLegendary = isPremiumTier && !isMythic && !isDiamond;

                    bool wanted =
                        On(RevertAllSkins) ||
                        (On(RevertMythic) && isMythic) ||
                        (On(RevertDiamond) && isDiamond) ||
                        (On(RevertLegendary) && isLegendary) ||
                        (On(RevertPixel) && IsPixelSkin(entityDef, cardId, dbId)) ||
                        (On(RevertHonored) && isHonored);
                    if (!wanted)
                    {
                        Log?.LogInfo($"Skin kept: {cardId} ({entityDef.GetName()})");
                        return;
                    }

                    string vanillaCardId = CollectionManager.GetHeroCardId(
                        entityDef.GetClass(), Assets.CardHero.HeroType.VANILLA);
                    if (string.IsNullOrEmpty(vanillaCardId))
                        return;

                    EntityDef vanillaDef = DefLoader.Get()?.GetEntityDef(vanillaCardId);
                    if (vanillaDef == null)
                        return;

                    // Re-sync skin behavior tags with the default hero.
                    __instance.SetTag(GAME_TAG.HERO_DOESNT_MOVE_ON_ATTACK,
                        vanillaDef.GetTag(GAME_TAG.HERO_DOESNT_MOVE_ON_ATTACK));
                    __instance.SetTag(GAME_TAG.EMOTECHARACTER,
                        vanillaDef.GetTag(GAME_TAG.EMOTECHARACTER));
                    __instance.SetTag(GAME_TAG.CORNER_REPLACEMENT_TYPE,
                        vanillaDef.GetTag(GAME_TAG.CORNER_REPLACEMENT_TYPE));
                    // Skins can override individual emotes; sync the whole block.
                    for (int t = (int)GAME_TAG.OVERRIDE_EMOTE_0; t <= (int)GAME_TAG.OVERRIDE_EMOTE_5; t++)
                        __instance.SetTag((GAME_TAG)t, vanillaDef.GetTag((GAME_TAG)t));
                    // Diamond-quality marker feeds actor variant selection and tooltips.
                    __instance.SetTag(GAME_TAG.HAS_DIAMOND_QUALITY,
                        vanillaDef.GetTag(GAME_TAG.HAS_DIAMOND_QUALITY));
                    // Diamond rendering must not survive onto the vanilla portrait.
                    // GOLDEN is the 500-win prestige status and is kept. A diamond skin
                    // hides the golden status in the premium tag, so the controller's
                    // hero power (golden independently of the skin) is used as the signal.
                    if (__instance.GetTag(GAME_TAG.PREMIUM) == (int)TAG_PREMIUM.DIAMOND)
                    {
                        int replacement = (int)TAG_PREMIUM.NORMAL;
                        Entity heroPower = __instance.GetController()?.GetHeroPower();
                        if (heroPower != null &&
                            heroPower.GetTag(GAME_TAG.PREMIUM) == (int)TAG_PREMIUM.GOLDEN)
                            replacement = (int)TAG_PREMIUM.GOLDEN;
                        __instance.SetTag(GAME_TAG.PREMIUM, replacement);
                    }

                    // Skin transforms arrive as a second LoadCard with a variant cardId;
                    // the transform showcase animation and history tile key off these tags,
                    // so zero them to make the change render as visual-less.
                    __instance.SetTag(GAME_TAG.TRANSFORMED_FROM_CARD, 0);
                    __instance.SetTag(GAME_TAG.TRANSFORMED_FROM_CARD_VISUAL_TYPE, 0);

                    Log?.LogInfo($"Reverting hero skin {cardId} -> {vanillaCardId}");
                    cardId = vanillaCardId;
                }
                catch (System.Exception e)
                {
                    Log?.LogError($"LoadCard skin revert failed for '{cardId}': {e}");
                }
            }
        }
    }
}
