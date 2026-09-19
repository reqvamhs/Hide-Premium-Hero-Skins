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
        public const string PluginVersion = "1.2.0";

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
            SkinPatches.Revert3DPortraits = fsConfig.Bind("Filters", "Revert3DPortraits", true,
                "Revert skins with an animated 3D portrait: the Diamond and Legendary shop tiers. " +
                "One filter, because nothing in a match distinguishes them.");
            SkinPatches.RevertDiamond = fsConfig.Bind("Filters", "RevertDiamond", true,
                "Superseded by Revert3DPortraits; kept so existing configs keep working. " +
                "Setting it to false still turns 3D portraits off.");
            SkinPatches.RevertLegendary = fsConfig.Bind("Filters", "RevertLegendary", true,
                "Superseded by Revert3DPortraits; kept so existing configs keep working. " +
                "Setting it to false still turns 3D portraits off.");
            SkinPatches.RevertPixel = fsConfig.Bind("Filters", "RevertPixel", true,
                "Revert Pixel skins (card name containing 'Pixel', plus PixelSkinCardIds).");
            SkinPatches.RevertHonored = fsConfig.Bind("Filters", "RevertHonored", false,
                "Revert Honored (1000-win golden) portraits. Off by default.");
            SkinPatches.RevertAllSkins = fsConfig.Bind("Filters", "RevertAllSkins", false,
                "Revert EVERY non-default skin, ignoring the filters above.");
            SkinPatches.RevertSignatureHeroCards = fsConfig.Bind("Filters", "RevertSignatureHeroCards", true,
                "Show hero cards played from hand (e.g. Deathwing) that carry Signature quality as the " +
                "normal version of the same hero card. The hero itself is never replaced; only the " +
                "Signature art and frame are dropped. Golden hero cards are left alone.");
            SkinPatches.PixelSkinCardIds = fsConfig.Bind("Filters", "PixelSkinCardIds",
                "116078,116079,116080,116081",
                "Comma-separated cardIds (HERO_02ba) or dbfIds (116081) always treated as Pixel skins. " +
                "Defaults: Northrend Arthas, Eternal Malfurion, Hearthglen Jaina, Orgrimmar Thrall.");

            SkinPatches.KeepDefaultBoardFrame = fsConfig.Bind("Features", "KeepDefaultBoardFrame", true,
                "When an opponent skin is reverted, keep the board's default frame above their portrait " +
                "instead of the skin theme's frame texture, which some themes (e.g. Maiev) render black " +
                "once the large custom hero frame is gone. Corner decorations, tabletop and play area are " +
                "left untouched.");

            new Harmony(PluginGuid).PatchAll();
            SkinPatches.Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
    }

    /// <summary>
    /// Reverts opponent hero skins to the default class hero by rewriting the cardId in an
    /// Entity.LoadCard prefix. Only cardIds in the CardHero DBF count as skins, so gameplay
    /// heroes are never touched. Hero powers are left alone.
    /// </summary>
    /// <remarks>
    /// Do not patch Actor.LoadCustomFrame (already unloads on the vanilla def) or
    /// Board.ApplyHeroTrayFromCard (sole writer of the tray texture; skipping it leaves a
    /// black arc above the hero).
    /// </remarks>
    public static class SkinPatches
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AlsoFriendly;
        internal static ConfigEntry<bool> RevertMythic;
        internal static ConfigEntry<bool> Revert3DPortraits;
        internal static ConfigEntry<bool> RevertDiamond;
        internal static ConfigEntry<bool> RevertLegendary;
        internal static ConfigEntry<bool> RevertPixel;
        internal static ConfigEntry<bool> RevertHonored;
        internal static ConfigEntry<bool> RevertAllSkins;
        internal static ConfigEntry<bool> RevertSignatureHeroCards;
        internal static ConfigEntry<string> PixelSkinCardIds;
        internal static ConfigEntry<bool> KeepDefaultBoardFrame;


        internal static bool On(ConfigEntry<bool> e) => e != null && e.Value;

        // Merged Diamond + Legendary filter. Legacy keys fold in as extra opt-outs; AND,
        // so an update can only revert less than the old config did, never more.
        internal static bool Revert3D() =>
            On(Revert3DPortraits) && On(RevertDiamond) && On(RevertLegendary);

        // Skins reverted this game, matched against transform sub-spell prefabs,
        // which are named after the skin cardId.
        internal static readonly System.Collections.Generic.HashSet<string> RevertedCardIds =
            new System.Collections.Generic.HashSet<string>();
        private static object _lastGameState;

        internal static void TrackGame()
        {
            GameState gs = GameState.Get();
            if (!ReferenceEquals(gs, _lastGameState))
            {
                _lastGameState = gs;
                RevertedCardIds.Clear();
            }
        }

        /// <summary>
        /// Legendary-tier detection. No tag marks the tier: HERO_FRAME_TYPE is Diamond-only,
        /// rarity is FREE on every skin, and CardHero.HeroType has no tier values. The CardDef
        /// does - Legendary and Diamond carry a 3D model or custom frame that plainer skins
        /// lack. GetCardDef loads the prefab synchronously, so ask this last.
        /// </summary>
        internal static bool HasLegendaryCardDef(string cardId)
        {
            DefLoader loader = DefLoader.Get();
            if (loader == null)
                return false;
            using (DefLoader.DisposableCardDef def = loader.GetCardDef(cardId))
            {
                CardDef cardDef = def?.CardDef;
                if (cardDef == null)
                    return false;
                return !string.IsNullOrEmpty(cardDef.m_LegendaryModel) ||
                       !string.IsNullOrEmpty(cardDef.m_MobileLegendaryModel) ||
                       !string.IsNullOrEmpty(cardDef.m_CustomHeroFramePrefab);
            }
        }

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
        /// Suppresses skin transform ceremonies, whose SpellPrefabGUID starts with the skin's
        /// cardId. Returning no instance is a path the game already takes (AddPowerSourceAndTargets
        /// bails before pushing the stack), so the transform still happens without the ceremony.
        /// </summary>
        /// <remarks>
        /// Do not match the transform's target cardId: it shares no prefix with the source and
        /// is unknown until its own LoadCard, which runs later.
        /// </remarks>
        [HarmonyPatch(typeof(SubSpellController), "GetSubSpellInstanceForTasklist")]
        public static class TransformSubSpellPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(object taskList)
            {
                // On skip Harmony leaves the return at null - the no-sub-spell result we want.
                try
                {
                    if (!On(Enabled) || taskList == null)
                        return true;
                    // Gameplay only, matching the other patches.
                    if (SceneMgr.Get() == null || SceneMgr.Get().GetMode() != SceneMgr.Mode.GAMEPLAY)
                        return true;
                    // Own the set's lifetime here too, in case this runs before LoadCardPatch.
                    TrackGame();
                    if (RevertedCardIds.Count == 0)
                        return true;

                    object hist = Traverse.Create(taskList).Method("GetSubSpellStart").GetValue();
                    if (hist == null)
                        return true;
                    string guid = Traverse.Create(hist).Property("SpellPrefabGUID").GetValue<string>();
                    if (string.IsNullOrEmpty(guid))
                        return true;

                    foreach (string cid in RevertedCardIds)
                    {
                        if (guid.StartsWith(cid + "_", System.StringComparison.OrdinalIgnoreCase))
                        {
                            Log?.LogInfo($"Suppressing skin sub-spell '{guid}'");
                            return false;
                        }
                    }
                }
                catch (System.Exception e)
                {
                    // Failing open plays the ceremony, which is the safe direction.
                    Log?.LogError($"TransformSubSpell prefix failed: {e}");
                }
                return true;
            }
        }

        /// <summary>
        /// A Mythic board theme survives the hero revert, which is wanted, but its UpdateFrame
        /// swaps the arc above the portrait for a theme texture that some themes (Maiev) render
        /// black once the skin's custom frame is gone. Skipping only that call keeps the default
        /// frame; props, tabletop and play area still get the theme.
        /// </summary>
        [HarmonyPatch(typeof(CornerSpellReplacementManager), "UpdateFrame")]
        public static class BoardFramePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(CornerReplacementSpellType spellType, Player.Side side)
            {
                try
                {
                    if (!On(Enabled) || !On(KeepDefaultBoardFrame))
                        return true;
                    if (SceneMgr.Get() == null || SceneMgr.Get().GetMode() != SceneMgr.Mode.GAMEPLAY)
                        return true;
                    // Not gated on RevertedCardIds: UpdateFrame runs before any hero LoadCard, so
                    // the set is always empty. Unconditional for the opposing side is safe because
                    // board themes only accompany Mythic skins.
                    if (side != Player.Side.OPPOSING)
                        return true;
                    Log?.LogInfo($"Keeping default board frame for reverted opponent (theme={spellType})");
                    return false;
                }
                catch (System.Exception e)
                {
                    Log?.LogError($"BoardFrame prefix failed: {e}");
                }
                return true;
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
                    TrackGame();
                    if (GameMgr.Get() != null && GameMgr.Get().IsBattlegrounds())
                        return;

                    // Side via the controller tag, present before the Card object exists.
                    Player friendly = GameState.Get().GetFriendlySidePlayer();
                    if (friendly == null)
                        return;
                    bool isOwn = __instance.GetControllerId() == friendly.GetPlayerId();

                    if (isOwn && !On(AlsoFriendly))
                        return;

                    EntityDef entityDef = DefLoader.Get()?.GetEntityDef(cardId);
                    if (entityDef == null || entityDef.GetCardType() != TAG_CARDTYPE.HERO)
                        return;
                    if (GameUtils.IsVanillaHero(cardId))
                        return;

                    // SAFETY GATE: only cosmetic skins are in CardHero, so gameplay heroes
                    // (Reno, Jaraxxus, bosses) never match.
                    int dbId = GameUtils.TranslateCardIdToDbId(cardId);
                    CardHeroDbfRecord heroRec =
                        GameDbf.CardHero.GetRecords().FirstOrDefault(r => r.CardId == dbId);
                    if (heroRec == null)
                    {
                        // Not a skin, so identity is kept - but a Signature copy is a shop
                        // cosmetic: drop it to normal. Consumers read PREMIUM via GetPremiumType(),
                        // so the tag is enough; the real-time copy is synced for later CHANGE_ENTITYs.
                        if (On(RevertSignatureHeroCards) &&
                            (TAG_PREMIUM)__instance.GetTag(GAME_TAG.PREMIUM) == TAG_PREMIUM.SIGNATURE)
                        {
                            Log?.LogInfo($"Reverting signature hero card {cardId} ({entityDef.GetName()}) -> normal");
                            __instance.SetTag(GAME_TAG.PREMIUM, (int)TAG_PREMIUM.NORMAL);
                            __instance.SetRealTimePremium(TAG_PREMIUM.NORMAL);
                        }

                        // Skin variants outside the DBF (meta/ascended forms) match their reverted
                        // parent's prefix; the right identity is whatever the entity already shows.
                        foreach (string cid in RevertedCardIds)
                        {
                            if (cardId.StartsWith(cid, System.StringComparison.OrdinalIgnoreCase))
                            {
                                string current = __instance.GetCardId();
                                if (!string.IsNullOrEmpty(current) &&
                                    !current.StartsWith(cid, System.StringComparison.OrdinalIgnoreCase))
                                {
                                    Log?.LogInfo($"Reverting skin variant {cardId} -> {current}");
                                    RevertedCardIds.Add(cardId);
                                    __instance.SetTag(GAME_TAG.TRANSFORMED_FROM_CARD, 0);
                                    cardId = current;
                                }
                                return;
                            }
                        }
                        return;
                    }

                    // Honored (1000-win) portraits are prestige, not shop cosmetics, and can carry
                    // tags that fool the tier filters - classify by HeroType instead.
                    bool isHonored = heroRec.HeroType == Assets.CardHero.HeroType.HONORED;
                    if (isHonored && !On(RevertHonored) && !On(RevertAllSkins))
                    {
                        Log?.LogInfo($"Skin kept (honored): {cardId}");
                        return;
                    }

                    bool isMythic = GameUtils.IsMythicHero(entityDef);

                    // Diamond: HERO_FRAME_TYPE == 1, which patch 36.6.0 puts on all 22 Diamond
                    // skins and nothing else. The quality checks never fire today (no shipped skin
                    // has HAS_DIAMOND_QUALITY) but would catch a future one. Mythic excluded first,
                    // it has its own filter and would otherwise match via its CardDef.
                    bool isDiamond = !isMythic &&
                        (RewardUtils.IsShopPremiumHeroSkin(entityDef) ||
                         __instance.GetPremiumType() == TAG_PREMIUM.DIAMOND ||
                         entityDef.HasTag(GAME_TAG.HAS_DIAMOND_QUALITY));
                    // Legendary has no tier tag, so the CardDef probe is the only detection.
                    // Asked last and only when the filter is on - it loads a prefab synchronously.
                    bool is3DPortrait = isDiamond ||
                        (!isMythic && Revert3D() && HasLegendaryCardDef(cardId));

                    bool wanted =
                        On(RevertAllSkins) ||
                        (On(RevertMythic) && isMythic) ||
                        (Revert3D() && is3DPortrait) ||
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
                    // HERO_FRAME_TYPE's only reader is RewardUtils.IsShopPremiumHeroSkin; nothing
                    // in rendering consults it. Synced so tier queries see the vanilla hero.
                    __instance.SetTag(GAME_TAG.HERO_FRAME_TYPE,
                        vanillaDef.GetTag(GAME_TAG.HERO_FRAME_TYPE));
                    // Skins can override individual emotes; sync the whole block.
                    for (int t = (int)GAME_TAG.OVERRIDE_EMOTE_0; t <= (int)GAME_TAG.OVERRIDE_EMOTE_5; t++)
                        __instance.SetTag((GAME_TAG)t, vanillaDef.GetTag((GAME_TAG)t));
                    // Diamond-quality marker feeds actor variant selection and tooltips.
                    __instance.SetTag(GAME_TAG.HAS_DIAMOND_QUALITY,
                        vanillaDef.GetTag(GAME_TAG.HAS_DIAMOND_QUALITY));
                    // Premium is left to the game: GetPremiumType() treats PREMIUM as a ceiling and
                    // walks it down per quality tag (DIAMOND -> SIGNATURE -> GOLDEN). Syncing the
                    // quality tags is enough; writing PREMIUM would destroy that ceiling.
                    __instance.SetTag(GAME_TAG.HAS_SIGNATURE_QUALITY,
                        vanillaDef.GetTag(GAME_TAG.HAS_SIGNATURE_QUALITY));

                    // Otherwise tooltips still reference the skin through this marker.
                    __instance.SetTag(GAME_TAG.TRANSFORMED_FROM_CARD, 0);

                    RevertedCardIds.Add(cardId);
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
