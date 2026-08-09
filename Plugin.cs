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
        public const string PluginVersion = "1.0.1";

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
    /// Reverts hero skins to the default class hero at Entity.LoadCard time by rewriting
    /// the cardId before load. Tier detection: premium tier (Diamond/Legendary/Mythic,
    /// the custom-frame skins) via RewardUtils.IsShopPremiumHeroSkin, Mythic via
    /// GAME_TAG.MYTHIC, Diamond via premium quality, Legendary as the remainder; Pixel
    /// skins via a configurable cardId/dbfId list. Only cardIds present in the CardHero
    /// DBF are treated as skins, so gameplay hero cards (Reno, Jaraxxus, bosses) are
    /// never touched; Honored (1000-win) portraits are classified by HeroType and kept
    /// unless enabled. Skin transform variants arrive as a second LoadCard with their
    /// own cardId and are reverted the same way; the transform ceremony sub-spell is
    /// suppressed separately (see TransformSubSpellPatch). Hero powers are deliberately
    /// left alone.
    ///
    /// The hero tray is also deliberately left alone - see the note on
    /// Board.ApplyHeroTrayFromCard below. Do not patch it. Actor.LoadCustomFrame
    /// needs no patch either, for the reasons in the second remarks block.
    /// </summary>
    /// <remarks>
    /// Do NOT patch Actor.LoadCustomFrame to unload a stale custom hero frame after a
    /// revert. The game already does it: the method's final branch, taken when cardDef
    /// is null or m_CustomHeroFramePrefab is empty - exactly the frameless vanilla def
    /// a reverted hero loads - calls UnloadCustomFrame() unconditionally. There is no
    /// early return to work around, and a prefix that unloads it again only makes the
    /// unload happen twice.
    /// </remarks>
    /// <remarks>
    /// Do NOT skip or cancel Board.ApplyHeroTrayFromCard for reverted/vanilla heroes.
    /// Board.ShowFriendlyHeroTray / ShowOpponentHeroTray destroy the board's built-in
    /// tray object and swap in a freshly instantiated prefab (the golden tray, or the
    /// card's HeroFrameEnemyPath frame) whose renderer has no main texture yet. The one
    /// and only writer of that texture is Board.OnHeroTrayTextureLoaded, reachable
    /// solely through ApplyHeroTrayFromCard, so cancelling it leaves an untextured mesh
    /// that renders as a black arc above the hero. ApplyHeroTrayFromCard already guards
    /// itself with String.IsNullOrEmpty(card.CustomHeroTray) and is a harmless no-op for
    /// cards without tray art; on a reverted hero it simply applies the vanilla hero's
    /// own tray, which is the desired result.
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
        /// Skin transform ceremonies (Genn Greymane's low-health worgen change, and the
        /// equivalent on other transforming skins) are played as server-instructed
        /// sub-spells: the task list carries a HistSubSpellStart whose SpellPrefabGUID is
        /// prefixed with the skin's own cardId and an underscore, e.g.
        /// "HERO_01az_GennGreymane_transformation_LowHealth:7ae43af7...", issued in the
        /// trigger block immediately before the transform CHANGE_ENTITY. Matching that
        /// prefix against the skins reverted this game is the suppression condition; the
        /// trailing underscore keeps a shorter cardId from matching a longer one.
        ///
        /// Returning no sub-spell instance is a path the game already takes on its own,
        /// not a forced error. SubSpellController.AddPowerSourceAndTargets is the only
        /// caller and bails on a null instance BEFORE reaching CheckForSubSpellEnd, so the
        /// m_subSpellInstanceStack push/pop pair stays balanced - the cancelled method
        /// never pushed, and nothing later pops. The false return then propagates through
        /// SpellController.AttachPowerTaskList to
        /// PowerProcessor.DoSubSpellTaskListWithController, which returns without calling
        /// DoPowerTaskList, so the task list runs on the normal no-spell path: the
        /// transform itself still happens and only the ceremony is skipped.
        /// </summary>
        /// <remarks>
        /// Do not try to match on the transform's target cardId instead of the prefab
        /// GUID. A transform target gets its own cardId that does not share the source
        /// skin's prefix (HERO_01az -> HERO_01ba), and it is not added to RevertedCardIds
        /// until its own LoadCard, which happens after this sub-spell is evaluated. A
        /// target-based check cannot fire; the GUID prefix is the only usable signal.
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
                    // Own the set's lifetime here too, so a stale cardId from the previous
                    // game cannot survive into this one on the strength of LoadCardPatch
                    // happening to run first.
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
        /// A Mythic skin's board theme is applied by CornerSpellReplacementManager, driven
        /// by CORNER_REPLACEMENT_TYPE on the PLAYER entity (not the hero), so it survives a
        /// hero revert - which is wanted, the corner decorations are part of the board and
        /// stay. UpdateCornerReplacements fans out to four calls per side:
        /// UpdateCornerReplacement (the corner props), UpdateTableTop, UpdateFrame and
        /// UpdatePlayArea. Only UpdateFrame is a problem.
        ///
        /// UpdateFrame swaps Board's per-side frame texture for the one on that theme's
        /// CornerReplacementSpellTableEntry.m_FrameTexture. That frame is the arc directly
        /// above the hero portrait. With the skin equipped its large custom hero frame sits
        /// in front of it; once the hero is reverted to the small vanilla portrait the frame
        /// is left exposed, and for some themes - Maiev (14) - it renders black.
        /// DEATHWING (9) and RAGNAROS (1) do not, which is why only some skins showed it.
        ///
        /// Skipping the call for the reverted side leaves the board's own frame texture in
        /// place - the default - while the corner props, tabletop and play area still get
        /// the theme. There is no restore path to use instead: UpdateCornerSpellReplacements
        /// gates its whole body on cornerReplacementSpellType != 0, so it only ever applies a
        /// theme and never reverts one.
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
                    // Deliberately NOT gated on RevertedCardIds: UpdateFrame runs during
                    // board setup, before any hero LoadCard, so the set is always empty here.
                    //
                    // Skipping unconditionally for the opposing side is safe in practice
                    // because a board theme only ever accompanies a Mythic-tier skin:
                    // CORNER_REPLACEMENT_TYPE is only set on heroes that also carry
                    // HERO_FRAME_TYPE=2, and those are reverted by default. If
                    // RevertMythic is turned off the opponent keeps their portrait and loses
                    // only the frame texture, which is a cosmetic mismatch rather than a bug.
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
                    // HERO_FRAME_TYPE is the custom-frame tier marker (Mythic reads 2) and
                    // is what RewardUtils.IsShopPremiumHeroSkin keys on. Left at the skin's
                    // value it survives the revert, and hero-attached spell overlays -
                    // Divine Shield, Freeze - are then sized for the skin's larger frame and
                    // render oversized and off-centre over the small vanilla portrait.
                    // Safe to write here because tier detection above already read
                    // IsShopPremiumHeroSkin from the skin's own EntityDef.
                    __instance.SetTag(GAME_TAG.HERO_FRAME_TYPE,
                        vanillaDef.GetTag(GAME_TAG.HERO_FRAME_TYPE));
                    // Skins can override individual emotes; sync the whole block.
                    for (int t = (int)GAME_TAG.OVERRIDE_EMOTE_0; t <= (int)GAME_TAG.OVERRIDE_EMOTE_5; t++)
                        __instance.SetTag((GAME_TAG)t, vanillaDef.GetTag((GAME_TAG)t));
                    // Diamond-quality marker feeds actor variant selection and tooltips.
                    __instance.SetTag(GAME_TAG.HAS_DIAMOND_QUALITY,
                        vanillaDef.GetTag(GAME_TAG.HAS_DIAMOND_QUALITY));
                    // Premium resolution is deliberately left to the game. EntityBase
                    // .GetPremiumType() treats the raw PREMIUM tag as an entitlement
                    // ceiling and walks it down to what the card can actually render:
                    //
                    //     p = GetTag(PREMIUM);
                    //     if (p == DIAMOND   && !HasTag(HAS_DIAMOND_QUALITY))   p = SIGNATURE;
                    //     if (p == SIGNATURE && !HasTag(HAS_SIGNATURE_QUALITY)) p = GOLDEN;
                    //
                    // Card.GetPremium() forwards to it, so every consumer - including
                    // Board.GoldenHeroes - sees the downgraded value. Syncing the two
                    // quality tags to the vanilla hero (both 0) is therefore enough:
                    // a Diamond or Signature skin resolves to GOLDEN on the default
                    // portrait by itself, and NORMAL/GOLDEN pass through untouched.
                    // Writing PREMIUM here would destroy the ceiling the cascade reads.
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
