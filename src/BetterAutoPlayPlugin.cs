using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterAutoPlay
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BetterAutoPlayPlugin : BasePlugin
    {
        public const string PluginGuid = "critical.vampirecrawlers.betterautoplay";
        public const string PluginName = "AutoPlay Combo Mana Sorter";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource LogSource;

        public override void Load()
        {
            LogSource = Log;
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(BetterAutoPlayPlugin).Assembly);
            IL2CPPChainloader.AddUnityComponent<AutoPlayVisualUpdater>();
            Log.LogInfo(PluginName + " loaded");
        }
    }

    internal sealed class AutoPlayVisualUpdater : MonoBehaviour
    {
        public AutoPlayVisualUpdater(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            AutoPlayUiController.Tick();
        }
    }

    internal static class AutoPlayUiController
    {
        private const float IconRotationDegreesPerSecond = 180f;
        private const float RainbowHueCyclesPerSecond = 0.35f;
        private const string LogoFileName = "autoplay_logo.png";
        private const string LogoObjectName = "BetterAutoPlayLogo";

        private static bool s_persistentAutoPlay;
        private static float s_hue;

        private static PlayerModel s_player;
        private static Button s_button;
        private static TMP_Text s_label;
        private static Transform s_iconTransform;
        private static Image s_iconImage;
        private static Image s_logoImage;
        private static Sprite s_logoSprite;
        private static bool s_logoLoadAttempted;

        private static string s_defaultLabelText;
        private static Color s_defaultLabelColor = Color.white;
        private static Vector3 s_defaultIconEuler;
        private static Vector3 s_defaultLogoEuler;
        private static bool s_defaultsCaptured;

        public static void Bind(PlayerModel player)
        {
            if (player == null)
                return;

            s_player = player;
            var button = player.PlayAllButton;
            if (button == null)
                return;

            if (s_button != button)
            {
                s_button = button;
                s_label = TryFindLabel(button);
                s_iconImage = TryFindIconImage(button);
                s_logoImage = TryAttachOrGetLogo(button);
                s_iconTransform = s_logoImage != null
                    ? s_logoImage.transform
                    : (s_iconImage != null ? s_iconImage.transform : button.transform);
                CaptureDefaults();
            }

            ApplyStaticVisuals();
        }

        public static void OnAutoPlayButtonClicked(PlayerModel player)
        {
            Bind(player);

            if (s_persistentAutoPlay)
            {
                s_persistentAutoPlay = false;
                try { player?.StopAutoPlay(); } catch { }
            }
            else
            {
                s_persistentAutoPlay = true;
                TryStartAutoPlay(player);
            }

            ApplyStaticVisuals();
        }

        public static void Tick()
        {
            if (!s_persistentAutoPlay)
                return;

            var player = s_player;
            if (player == null)
                return;

            try
            {
                if (player.AutoPlayer != null && !player.AutoPlayer.IsPlaying)
                    player.AutoPlayer.Play();
            }
            catch { }

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
                return;

            if (s_iconTransform != null)
            {
                var euler = s_iconTransform.localEulerAngles;
                euler.z -= IconRotationDegreesPerSecond * dt;
                s_iconTransform.localEulerAngles = euler;
            }

            if (s_label != null)
            {
                s_hue += RainbowHueCyclesPerSecond * dt;
                s_hue -= Mathf.Floor(s_hue);
                var rainbow = Color.HSVToRGB(s_hue, 1f, 1f);
                rainbow.a = 1f;
                s_label.color = rainbow;
            }
        }

        private static void TryStartAutoPlay(PlayerModel player)
        {
            try
            {
                if (player?.AutoPlayer != null && !player.AutoPlayer.IsPlaying)
                    player.AutoPlayer.Play();
            }
            catch { }
        }

        private static void CaptureDefaults()
        {
            s_defaultsCaptured = true;
            s_defaultLabelText = s_label != null ? s_label.text : null;
            s_defaultLabelColor = s_label != null ? s_label.color : Color.white;
            s_defaultIconEuler = s_iconTransform != null ? s_iconTransform.localEulerAngles : Vector3.zero;
            s_defaultLogoEuler = s_logoImage != null ? s_logoImage.transform.localEulerAngles : Vector3.zero;
        }

        private static void ApplyStaticVisuals()
        {
            if (!s_defaultsCaptured)
                return;

            if (s_persistentAutoPlay)
            {
                if (s_label != null)
                    s_label.text = "Auto-playing";

                if (s_logoImage != null)
                    s_logoImage.gameObject.SetActive(true);
            }
            else
            {
                if (s_label != null)
                {
                    s_label.text = s_defaultLabelText;
                    s_label.color = s_defaultLabelColor;
                }

                if (s_iconTransform != null)
                    s_iconTransform.localEulerAngles = s_defaultIconEuler;

                if (s_logoImage != null)
                {
                    s_logoImage.transform.localEulerAngles = s_defaultLogoEuler;
                    s_logoImage.gameObject.SetActive(false);
                }
            }
        }

        private static TMP_Text TryFindLabel(Button button)
        {
            if (button == null) return null;
            try { return button.GetComponentInChildren<TMP_Text>(); }
            catch { return null; }
        }

        private static Image TryFindIconImage(Button button)
        {
            if (button == null) return null;

            try
            {
                var byName = button.transform.Find("Icon");
                if (byName != null)
                {
                    var namedImage = byName.GetComponent<Image>();
                    if (namedImage != null)
                        return namedImage;
                }
            }
            catch { }

            try
            {
                var direct = button.image;
                if (direct != null)
                    return direct;
            }
            catch { }

            try { return button.GetComponentInChildren<Image>(); }
            catch { return null; }
        }

        private static Image TryAttachOrGetLogo(Button button)
        {
            if (button == null)
                return null;

            var existing = button.transform.Find(LogoObjectName);
            if (existing != null)
            {
                try { return existing.GetComponent<Image>(); }
                catch { return null; }
            }

            var sprite = GetOrLoadLogoSprite();
            if (sprite == null)
                return null;

            try
            {
                var go = new GameObject(LogoObjectName);
                go.transform.SetParent(button.transform, false);

                var image = go.AddComponent<Image>();
                image.sprite = sprite;
                image.raycastTarget = false;
                image.preserveAspect = true;

                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(20f, 20f);

                go.SetActive(false);
                return image;
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource?.LogWarning("Could not create autoplay logo overlay: " + ex.Message);
                return null;
            }
        }

        private static Sprite GetOrLoadLogoSprite()
        {
            if (s_logoSprite != null)
                return s_logoSprite;
            if (s_logoLoadAttempted)
                return null;

            s_logoLoadAttempted = true;
            var path = System.IO.Path.Combine(Paths.PluginPath, "BetterAutoPlay", LogoFileName);
            if (!System.IO.File.Exists(path))
            {
                BetterAutoPlayPlugin.LogSource?.LogInfo("Autoplay logo not found at: " + path);
                return null;
            }

            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var il2cppBytes = new Il2CppStructArray<byte>(bytes.Length);
                for (int i = 0; i < bytes.Length; i++)
                    il2cppBytes[i] = bytes[i];

                if (!ImageConversion.LoadImage(texture, il2cppBytes, false))
                {
                    BetterAutoPlayPlugin.LogSource?.LogWarning("Failed to decode autoplay logo PNG: " + path);
                    return null;
                }

                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                s_logoSprite = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                return s_logoSprite;
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource?.LogWarning("Could not load autoplay logo: " + ex.Message);
                return null;
            }
        }
    }

    // Lower value = played earlier.
    // ManaGenerator (empty tome etc.) enables more plays — always play first.
    // Utility (candle etc.) buffs attack cards, so play them before attacks.
    // Crawler (FCC companion cards) play whenever they fit.
    // Attack (whip, runetracer etc.) play last after receiving buffs.
    internal enum CardRole { ManaGenerator = 0, Utility = 1, Crawler = 2, Attack = 3, Unknown = 4 }

    internal static class CardClassifier
    {
        // IL2CPP interop exposes native fields as properties, not C# fields — use AccessTools.Property.
        private static readonly PropertyInfo s_cardGroupProp =
            AccessTools.Property(typeof(CardConfig), "cardGroup");
        private static readonly PropertyInfo s_isWeaponProp =
            AccessTools.Property(typeof(CardGroup), "IsWeapon");

        public static CardRole Classify(CardModel card)
        {
            try
            {
                var config = card?.CardConfig;
                if (config == null) return CardRole.Unknown;

                // FccConfig subclass = crawler companion card
                if (config.GetType().Name == "FccConfig")
                    return CardRole.Crawler;

                // Cards that generate mana when played
                if (HasManaEffect(config))
                    return CardRole.ManaGenerator;

                // Weapon groups = attack/damage cards
                if (IsWeaponCard(config))
                    return CardRole.Attack;

                // Default: utility/support (buffs, stat upgrades, etc.)
                return CardRole.Utility;
            }
            catch
            {
                return CardRole.Unknown;
            }
        }

        private static bool HasManaEffect(CardConfig config)
        {
            try
            {
                var effectsProp = AccessTools.Property(typeof(CardConfig), "OnPlayEffects");
                var effects = effectsProp?.GetValue(config);
                if (effects == null) return false;

                var type = effects.GetType();
                var countProp = type.GetProperty("Count") ?? type.GetProperty("Length");
                var getItem = type.GetMethod("get_Item");
                if (countProp == null || getItem == null) return false;

                int count = Convert.ToInt32(countProp.GetValue(effects));
                for (int i = 0; i < count; i++)
                {
                    var effect = getItem.Invoke(effects, new object[] { i });
                    if (effect == null) continue;
                    string name = effect.GetType().Name;
                    if (name == "GainManaEffect" || name == "GainManaEqualToCostEffect")
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsWeaponCard(CardConfig config)
        {
            try
            {
                var group = s_cardGroupProp?.GetValue(config);
                if (group == null) return false;
                return (bool)(s_isWeaponProp?.GetValue(group) ?? false);
            }
            catch { return false; }
        }
    }

    [HarmonyPatch(typeof(AutoPlayer), "Play")]
    internal static class AutoPlayerPlayPatch
    {
        private static void Prefix(AutoPlayer __instance)
        {
            if (__instance == null)
                return;

            try
            {
                __instance.SetSortMode(AutoPlayer.AutoPlaySorting.Combo);
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource.LogWarning("Could not force combo sorting: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(AutoPlayer), "SortCardsByCombo")]
    internal static class AutoPlayerSortCardsByComboPatch
    {
        private static readonly PropertyInfo PlayerProp = AccessTools.Property(typeof(AutoPlayer), "_player");

        private static bool Prefix(AutoPlayer __instance, object cards)
        {
            try
            {
                var player = PlayerProp == null ? null : PlayerProp.GetValue(__instance) as PlayerModel;
                var original = Il2CppListAdapter.ToManaged(cards);
                if (original.Count <= 1)
                    return false;

                var ordered = ComboManaSorter.Sort(player, original);
                Il2CppListAdapter.Replace(cards, ordered);
                return false;
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource.LogError("Custom combo sort failed; falling back to original sorter: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerModel), "UpdatePlayerButtons")]
    internal static class PlayerModelUpdateButtonsPatch
    {
        private static void Postfix(PlayerModel __instance)
        {
            try { AutoPlayUiController.Bind(__instance); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerModel), "Button_AutoPlay")]
    internal static class PlayerModelButtonAutoPlayPatch
    {
        private static bool Prefix(PlayerModel __instance)
        {
            AutoPlayUiController.OnAutoPlayButtonClicked(__instance);
            return false;
        }
    }

    internal static class ComboManaSorter
    {
        private static readonly PropertyInfo s_gainAmountProp =
            AccessTools.Property(typeof(Nosebleed.Pancake.GameCommands.GainManaEffect), "_gainAmount");

        public static List<CardModel> Sort(PlayerModel player, List<CardModel> input)
        {
            var remaining = new List<CardModel>(input);
            var originalIndex = BuildOriginalIndex(input);
            var roles = BuildRoles(input);
            var manaGains = BuildManaGains(input, roles);
            var ordered = new List<CardModel>(input.Count);

            CardModel start = PickStartingCard(player, remaining, originalIndex, roles, manaGains);
            if (start == null)
            {
                AppendRoleSorted(ordered, remaining, originalIndex, roles, manaGains);
                return ordered;
            }

            ordered.Add(start);
            remaining.Remove(start);

            CardModel previous = start;
            int previousMana = GetManaCost(start);

            while (remaining.Count > 0)
            {
                CardModel next = PickNextComboCard(remaining, previous, previousMana, originalIndex, roles, manaGains);
                if (next == null)
                    break;

                ordered.Add(next);
                remaining.Remove(next);
                previous = next;
                previousMana = GetManaCost(next);
            }

            AppendRoleSorted(ordered, remaining, originalIndex, roles, manaGains);
            return ordered;
        }

        private static Dictionary<CardModel, CardRole> BuildRoles(List<CardModel> input)
        {
            var roles = new Dictionary<CardModel, CardRole>(input.Count);
            foreach (var card in input)
            {
                if (card == null || roles.ContainsKey(card))
                    continue;
                var role = CardClassifier.Classify(card);
                roles[card] = role;
                BetterAutoPlayPlugin.LogSource?.LogDebug(
                    $"[RoleClassify] '{SafeName(card)}' → {role}");
            }
            return roles;
        }

        // Compute total mana gain for each ManaGenerator card once per sort.
        private static Dictionary<CardModel, int> BuildManaGains(List<CardModel> input, Dictionary<CardModel, CardRole> roles)
        {
            var gains = new Dictionary<CardModel, int>(input.Count);
            foreach (var card in input)
            {
                if (card == null || gains.ContainsKey(card))
                    continue;
                CardRole role;
                int gain = (roles.TryGetValue(card, out role) && role == CardRole.ManaGenerator)
                    ? ComputeManaGain(card)
                    : 0;
                gains[card] = gain;
                if (gain > 0)
                    BetterAutoPlayPlugin.LogSource?.LogDebug(
                        $"[ManaGain] '{SafeName(card)}' gives {gain} mana");
            }
            return gains;
        }

        // Sum all mana-gain effects on a card's on-play list.
        private static int ComputeManaGain(CardModel card)
        {
            try
            {
                var effectsProp = AccessTools.Property(typeof(Nosebleed.Pancake.GameConfig.CardConfig), "OnPlayEffects");
                var effects = effectsProp?.GetValue(card.CardConfig);
                if (effects == null) return 0;

                var type = effects.GetType();
                var countProp = type.GetProperty("Count") ?? type.GetProperty("Length");
                var getItem = type.GetMethod("get_Item");
                if (countProp == null || getItem == null) return 0;

                int total = 0;
                int count = Convert.ToInt32(countProp.GetValue(effects));
                for (int i = 0; i < count; i++)
                {
                    var effect = getItem.Invoke(effects, new object[] { i });
                    if (effect == null) continue;
                    string name = effect.GetType().Name;
                    if (name == "GainManaEffect" && s_gainAmountProp != null)
                        total += Convert.ToInt32(s_gainAmountProp.GetValue(effect));
                    else if (name == "GainManaEqualToCostEffect")
                        total += GetManaCost(card);
                }
                return total;
            }
            catch { return 0; }
        }

        // Pick the starting card: prefer whatever is currently continuing an existing combo,
        // then fall back to the best role+mana card overall.
        private static CardModel PickStartingCard(PlayerModel player, List<CardModel> cards,
            Dictionary<CardModel, int> originalIndex, Dictionary<CardModel, CardRole> roles,
            Dictionary<CardModel, int> manaGains)
        {
            CardModel bestInCombo = null;
            CardModel bestAny = null;

            foreach (var card in cards)
            {
                if (card == null) continue;
                if (IsCurrentlyInCombo(player, card))
                    bestInCombo = BetterCard(bestInCombo, card, originalIndex, roles, manaGains);
                bestAny = BetterCard(bestAny, card, originalIndex, roles, manaGains);
            }

            return bestInCombo ?? bestAny;
        }

        // Among cards that can combo after `previous`:
        //   Tier 1 — ManaGenerators at any cost (always exempt from the flat penalty).
        //   Tier 2 — Non-generators that strictly increase mana (forms a proper combo step).
        //   Tier 3 — Non-generators that stay flat (same cost, last resort before decreasing).
        //   Tier 4 — Anything with decreasing cost (final fallback).
        // This prevents 0,0,0,0→1 chains by always pairing a 0-cost play with a 1-cost follow-up.
        private static CardModel PickNextComboCard(List<CardModel> cards, CardModel previous,
            int previousMana, Dictionary<CardModel, int> originalIndex, Dictionary<CardModel, CardRole> roles,
            Dictionary<CardModel, int> manaGains)
        {
            CardModel bestManaGen = null;
            CardModel bestStrictIncrease = null;
            CardModel bestFlatNonGen = null;
            CardModel bestDecreasing = null;

            foreach (var card in cards)
            {
                if (card == null || !CanComboAfter(card, previous))
                    continue;

                int mana = GetManaCost(card);
                CardRole role = GetRole(card, roles);

                if (role == CardRole.ManaGenerator)
                    bestManaGen = BetterCard(bestManaGen, card, originalIndex, roles, manaGains);
                else if (mana > previousMana)
                    bestStrictIncrease = BetterCard(bestStrictIncrease, card, originalIndex, roles, manaGains);
                else if (mana == previousMana)
                    bestFlatNonGen = BetterCard(bestFlatNonGen, card, originalIndex, roles, manaGains);
                else
                    bestDecreasing = BetterCard(bestDecreasing, card, originalIndex, roles, manaGains);
            }

            // When currently below zero mana-cost, prioritize climbing the combo ladder first.
            // This ensures -1 -> 0 style transitions are not skipped by role-first heuristics.
            if (previousMana < 0)
                return bestStrictIncrease ?? bestManaGen ?? bestFlatNonGen ?? bestDecreasing;

            return bestManaGen ?? bestStrictIncrease ?? bestFlatNonGen ?? bestDecreasing;
        }

        // Sort leftover (non-combo) cards: role first, then mana gain (desc for generators),
        // then mana cost, then combo cost, then original order.
        private static void AppendRoleSorted(List<CardModel> ordered, List<CardModel> remaining,
            Dictionary<CardModel, int> originalIndex, Dictionary<CardModel, CardRole> roles,
            Dictionary<CardModel, int> manaGains)
        {
            remaining.Sort(delegate(CardModel left, CardModel right)
            {
                int r = GetRole(left, roles).CompareTo(GetRole(right, roles));
                if (r != 0) return r;
                // For mana generators: higher gain plays first (descending)
                if (GetRole(left, roles) == CardRole.ManaGenerator)
                {
                    r = GetManaGain(right, manaGains).CompareTo(GetManaGain(left, manaGains));
                    if (r != 0) return r;
                }
                r = GetManaCost(left).CompareTo(GetManaCost(right));
                if (r != 0) return r;
                // Same mana: evolved cards play before non-evolved.
                bool le = IsEvolved(left);
                bool re2 = IsEvolved(right);
                if (le && !re2) return -1;
                if (!le && re2) return 1;
                r = GetComboCost(left).CompareTo(GetComboCost(right));
                if (r != 0) return r;
                return GetOriginalIndex(originalIndex, left).CompareTo(GetOriginalIndex(originalIndex, right));
            });
            ordered.AddRange(remaining);
        }

        // Role-first comparison; for ManaGenerator ties, prefer higher gain; otherwise lowest mana.
        private static CardModel BetterCard(CardModel current, CardModel candidate,
            Dictionary<CardModel, int> originalIndex, Dictionary<CardModel, CardRole> roles,
            Dictionary<CardModel, int> manaGains)
        {
            if (current == null) return candidate;

            // Prefer negative mana cards over non-negative cards to preserve
            // valid negative->zero combo openings.
            int candidateMana = GetManaCost(candidate);
            int currentMana = GetManaCost(current);
            bool candidateNegative = candidateMana < 0;
            bool currentNegative = currentMana < 0;
            if (candidateNegative != currentNegative)
                return candidateNegative ? candidate : current;

            int r = GetRole(candidate, roles).CompareTo(GetRole(current, roles));
            if (r < 0) return candidate;
            if (r > 0) return current;

            // Same role: ManaGenerators prefer higher gain (more mana = play first)
            if (GetRole(candidate, roles) == CardRole.ManaGenerator)
            {
                r = GetManaGain(candidate, manaGains).CompareTo(GetManaGain(current, manaGains));
                if (r > 0) return candidate; // candidate gives more mana
                if (r < 0) return current;
            }

            return BetterLowestMana(current, candidate, originalIndex);
        }

        private static int GetManaGain(CardModel card, Dictionary<CardModel, int> manaGains)
        {
            int gain;
            return card != null && manaGains.TryGetValue(card, out gain) ? gain : 0;
        }

        private static CardModel BetterLowestMana(CardModel current, CardModel candidate,
            Dictionary<CardModel, int> originalIndex)
        {
            if (current == null) return candidate;

            int r = GetManaCost(candidate).CompareTo(GetManaCost(current));
            if (r < 0) return candidate;
            if (r > 0) return current;

            // Same mana: evolved cards play before non-evolved.
            bool ce = IsEvolved(candidate);
            bool cu = IsEvolved(current);
            if (ce && !cu) return candidate;
            if (!ce && cu) return current;

            r = GetComboCost(candidate).CompareTo(GetComboCost(current));
            if (r < 0) return candidate;
            if (r > 0) return current;

            return GetOriginalIndex(originalIndex, candidate) < GetOriginalIndex(originalIndex, current)
                ? candidate : current;
        }

        private static bool IsEvolved(CardModel card)
        {
            try { return card != null && card.IsEvolved; }
            catch { return false; }
        }

        private static CardRole GetRole(CardModel card, Dictionary<CardModel, CardRole> roles)
        {
            if (card == null) return CardRole.Unknown;
            CardRole role;
            return roles.TryGetValue(card, out role) ? role : CardRole.Unknown;
        }

        private static bool IsCurrentlyInCombo(PlayerModel player, CardModel card)
        {
            if (player == null || card == null) return false;
            try { return player.IsCardInCombo(card, true); }
            catch { return false; }
        }

        private static bool CanComboAfter(CardModel card, CardModel previous)
        {
            if (card == null || previous == null) return false;
            try { return card.CanCardCostTypeCombo(previous); }
            catch { return false; }
        }

        private static int GetManaCost(CardModel card)
        {
            if (card == null) return int.MaxValue;
            try { return card.GetCardCostTypeManaCost(false); }
            catch { return int.MaxValue; }
        }

        private static int GetComboCost(CardModel card)
        {
            if (card == null) return int.MaxValue;
            try { return card.GetCardComboCost(false); }
            catch { return int.MaxValue; }
        }

        private static Dictionary<CardModel, int> BuildOriginalIndex(List<CardModel> input)
        {
            var index = new Dictionary<CardModel, int>();
            for (int i = 0; i < input.Count; i++)
            {
                if (input[i] != null && !index.ContainsKey(input[i]))
                    index.Add(input[i], i);
            }
            return index;
        }

        private static int GetOriginalIndex(Dictionary<CardModel, int> index, CardModel card)
        {
            int result;
            return card != null && index.TryGetValue(card, out result) ? result : int.MaxValue;
        }

        private static string SafeName(CardModel card)
        {
            try { return card?.Name ?? "?"; }
            catch { return "?"; }
        }
    }

    internal static class Il2CppListAdapter
    {
        public static List<CardModel> ToManaged(object il2CppList)
        {
            var result = new List<CardModel>();
            if (il2CppList == null)
                return result;

            Type type = il2CppList.GetType();
            PropertyInfo countProperty = type.GetProperty("Count");
            MethodInfo getItemMethod = type.GetMethod("get_Item");
            int count = Convert.ToInt32(countProperty.GetValue(il2CppList, null));

            for (int i = 0; i < count; i++)
            {
                result.Add(getItemMethod.Invoke(il2CppList, new object[] { i }) as CardModel);
            }

            return result;
        }

        public static void Replace(object il2CppList, List<CardModel> cards)
        {
            if (il2CppList == null)
                return;

            Type type = il2CppList.GetType();
            MethodInfo clearMethod = type.GetMethod("Clear");
            MethodInfo addMethod = type.GetMethod("Add");

            clearMethod.Invoke(il2CppList, null);
            foreach (var card in cards)
            {
                addMethod.Invoke(il2CppList, new object[] { card });
            }
        }
    }
}
