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
        public const string PluginVersion = "1.1.0";

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
                "Revert Legendary-tier skins (animated 3D portraits). Detected by the skin's CardDef " +
                "carrying a legendary 3D model or custom frame; the game's HERO_FRAME_TYPE marker only " +
                "covers Diamond and Mythic and is set on a single Legendary skin (Mecha'thun).");
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
    /// Entity.LoadCard prefix. Tier detection uses the game's own classifiers: Mythic via
    /// CORNER_REPLACEMENT_TYPE, Diamond via premium quality or the HERO_FRAME_TYPE marker,
    /// Legendary via the CardDef's legendary 3D model or custom frame, Pixel via a
    /// configurable id list. Only cardIds present in the CardHero DBF are treated as skins, so gameplay
    /// hero cards (Reno, Jaraxxus, bosses) are never touched, and Honored portraits are
    /// kept unless opted in. Hero powers are left alone.
    /// </summary>
    /// <remarks>
    /// Deliberately unpatched: Actor.LoadCustomFrame already unloads a stale custom frame
    /// on the frameless vanilla def a reverted hero loads, and Board.ApplyHeroTrayFromCard
    /// is the only path that ever textures the hero tray - cancelling it leaves an
    /// untextured mesh that renders as a black arc above the hero.
    /// </remarks>
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
        internal static ConfigEntry<bool> RevertSignatureHeroCards;
        internal static ConfigEntry<string> PixelSkinCardIds;
        internal static ConfigEntry<bool> KeepDefaultBoardFrame;


        internal static bool On(ConfigEntry<bool> e) => e != null && e.Value;

        // Original cardIds of skins reverted in the current game; used to suppress
        // their server-instructed transform ceremony sub-spells (prefabs are named
        // after the skin cardId).
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
        /// Legendary-tier detection. HERO_FRAME_TYPE does not mark the tier: the game's own
        /// reader, RewardUtils.IsShopPremiumHeroSkin, is true only for tag value 1 (Diamond),
        /// and every skin has RARITY FREE, so no tag distinguishes Legendary. Nor does
        /// CardHero.HeroType, whose only values are UNKNOWN, VANILLA, HONORED and the two
        /// Battlegrounds kinds. The CardDef asset does - Legendary skins
        /// carry a legendary 3D model or a custom hero frame prefab, while vanilla, Honored
        /// and ordinary 2D skins carry neither. GetCardDef instantiates the shared prefab
        /// synchronously, so this is asked only as a last resort after the tag checks fail,
        /// and the handle is disposed immediately.
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
        /// Skin transform ceremonies (e.g. Genn Greymane's low-health change) arrive as
        /// server-instructed sub-spells whose SpellPrefabGUID is prefixed with the skin's own
        /// cardId and an underscore; matching that prefix against the skins reverted this game
        /// is the suppression condition. Returning no sub-spell instance is a path the game
        /// already takes on its own: AddPowerSourceAndTargets bails on a null instance before
        /// anything is pushed onto the instance stack, so the transform itself still happens
        /// and only the ceremony is skipped.
        /// </summary>
        /// <remarks>
        /// Do not match on the transform's target cardId instead. The target gets its own
        /// cardId that does not share the source skin's prefix, and it is not known until its
        /// own LoadCard, which runs after this sub-spell is evaluated.
        /// </remarks>
        [HarmonyPatch(typeof(SubSpellController), "GetSubSpellInstanceForTasklist")]
        public static class TransformSubSpellPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(object taskList)
            {
                // On skip, Harmony leaves the return value at default (null),
                // which is exactly the no-sub-spell result we want.
                try
                {
                    if (!On(Enabled) || taskList == null)
                        return true;
                    // Gameplay only, matching the other patches.
                    if (SceneMgr.Get() == null || SceneMgr.Get().GetMode() != SceneMgr.Mode.GAMEPLAY)
                        return true;
                    // Own the set's lifetime here too, so a stale cardId from the last game cannot
                    // survive into this one if LoadCardPatch happens to run first.
                    TrackGame();
                    if (RevertedCardIds.Count == 0)
                        return true;

                    object hist = Traverse.Create(taskList).Method("GetSubSpellStart").GetValue();
                    if (hist == null)
                        return true;
                    // HistSubSpellStart.SpellPrefabGUID is a string.
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
        /// A Mythic skin's board theme is driven by CORNER_REPLACEMENT_TYPE on the PLAYER
        /// entity, so it survives a hero revert - which is wanted, the corner decorations are
        /// part of the board. Of the four calls UpdateCornerReplacements fans out to per side,
        /// only UpdateFrame is a problem: it swaps the arc above the hero portrait for the
        /// theme's frame texture, which some themes (Maiev) render black once the skin's large
        /// custom hero frame is gone. Skipping it leaves the board's default frame in place
        /// while the corner props, tabletop and play area still get the theme. There is no
        /// restore path to use instead - UpdateCornerSpellReplacements only ever applies a
        /// theme, never reverts one.
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
                    // Not gated on RevertedCardIds: UpdateFrame runs during board setup, before any
                    // hero LoadCard, so the set is always empty here. Skipping unconditionally for the
                    // opposing side is safe because a board theme only ever accompanies a Mythic skin,
                    // which is reverted by default; with RevertMythic off the opponent loses only the
                    // frame texture, a cosmetic mismatch rather than a bug.
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

                    // Side via the controller tag: present from the first LoadCard,
                    // before the Card object exists.
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

                    // SAFETY GATE: only cosmetic skins live in the CardHero DBF.
                    // Gameplay hero cards (Reno, Jaraxxus, bosses...) are not in it -> never touched.
                    int dbId = GameUtils.TranslateCardIdToDbId(cardId);
                    CardHeroDbfRecord heroRec =
                        GameDbf.CardHero.GetRecords().FirstOrDefault(r => r.CardId == dbId);
                    if (heroRec == null)
                    {
                        // Gameplay hero cards played from hand are not skins and keep their identity,
                        // but a Signature copy is still a shop cosmetic: drop it to the normal version
                        // of the same card. Every consumer reads the premium through GetPremiumType(),
                        // so rewriting the tag before the CardDef load is sufficient. The real-time
                        // copy is synced too, so later CHANGE_ENTITYs see no phantom premium change.
                        if (On(RevertSignatureHeroCards) &&
                            (TAG_PREMIUM)__instance.GetTag(GAME_TAG.PREMIUM) == TAG_PREMIUM.SIGNATURE)
                        {
                            Log?.LogInfo($"Reverting signature hero card {cardId} ({entityDef.GetName()}) -> normal");
                            __instance.SetTag(GAME_TAG.PREMIUM, (int)TAG_PREMIUM.NORMAL);
                            __instance.SetRealTimePremium(TAG_PREMIUM.NORMAL);
                        }

                        // Skin variants outside the skin database (e.g. some meta/ascended
                        // forms) are recognized by their reverted parent's cardId prefix.
                        // The correct identity is whatever the entity already shows (the
                        // vanilla hero, or a legitimately played hero card) - not a new
                        // class-vanilla mapping.
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

                    // Honored (1000-win) portraits are prestige, not shop cosmetics.
                    // They can carry tags that fool the tier filters, so classify them
                    // by their actual HeroType and give them their own toggle.
                    bool isHonored = heroRec.HeroType == Assets.CardHero.HeroType.HONORED;
                    if (isHonored && !On(RevertHonored) && !On(RevertAllSkins))
                    {
                        Log?.LogInfo($"Skin kept (honored): {cardId}");
                        return;
                    }

                    // IsShopPremiumHeroSkin is true only when HERO_FRAME_TYPE == 1, i.e.
                    // Diamond; Mythic (2) and Mecha'thun (3) read false. It is NOT a
                    // Legendary marker - see HasLegendaryCardDef.
                    bool isPremiumTier = RewardUtils.IsShopPremiumHeroSkin(entityDef);
                    bool isMythic = GameUtils.IsMythicHero(entityDef);
                    bool isDiamond =
                        __instance.GetPremiumType() == TAG_PREMIUM.DIAMOND ||
                        entityDef.HasTag(GAME_TAG.HAS_DIAMOND_QUALITY);
                    // Legendary: the CardDef probe is the real detection, and runs only
                    // when the cheap tag checks came up empty and the answer would matter.
                    // NOTE: isPremiumTier here means HERO_FRAME_TYPE == 1 (Diamond), so a
                    // Diamond skin lacking HAS_DIAMOND_QUALITY lands in this branch and
                    // needs RevertLegendary rather than RevertDiamond to be caught.
                    bool isLegendary = !isMythic && !isDiamond &&
                        (isPremiumTier || (On(RevertLegendary) && HasLegendaryCardDef(cardId)));

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
                    // HERO_FRAME_TYPE (tag 3495) has exactly one reader in the assembly,
                    // RewardUtils.IsShopPremiumHeroSkin; nothing in rendering consults it. Synced
                    // anyway so any tier query against the live entity sees the vanilla hero. Safe
                    // to write here because detection above already read it from the skin's EntityDef.
                    __instance.SetTag(GAME_TAG.HERO_FRAME_TYPE,
                        vanillaDef.GetTag(GAME_TAG.HERO_FRAME_TYPE));
                    // Skins can override individual emotes; sync the whole block.
                    for (int t = (int)GAME_TAG.OVERRIDE_EMOTE_0; t <= (int)GAME_TAG.OVERRIDE_EMOTE_5; t++)
                        __instance.SetTag((GAME_TAG)t, vanillaDef.GetTag((GAME_TAG)t));
                    // Diamond-quality marker feeds actor variant selection and tooltips.
                    __instance.SetTag(GAME_TAG.HAS_DIAMOND_QUALITY,
                        vanillaDef.GetTag(GAME_TAG.HAS_DIAMOND_QUALITY));
                    // Premium resolution is left to the game: GetPremiumType() treats the PREMIUM tag
                    // as an entitlement ceiling and walks it down to what the card can actually render
                    // (DIAMOND -> SIGNATURE -> GOLDEN, each step gated on the matching quality tag).
                    // Syncing the two quality tags to the vanilla hero is therefore enough; writing
                    // PREMIUM here would destroy the ceiling the cascade reads.
                    __instance.SetTag(GAME_TAG.HAS_SIGNATURE_QUALITY,
                        vanillaDef.GetTag(GAME_TAG.HAS_SIGNATURE_QUALITY));

                    // After a transform-variant revert, tooltips would still read the
                    // live entity's transformed-from marker and reference the skin.
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
