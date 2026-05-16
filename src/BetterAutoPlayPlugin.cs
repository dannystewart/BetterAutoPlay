using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Nosebleed.Pancake.GameLogic.GameStates;
using Nosebleed.Pancake.Models;
using UnityEngine;
using UnityEngine.UI;

namespace BetterAutoPlay
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BetterAutoPlayPlugin : BasePlugin
    {
        public const string PluginGuid    = "critical.vampirecrawlers.betterautoplay";
        public const string PluginName    = "AutoPlay Combo Mana Sorter";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource LogSource;

        public override void Load()
        {
            LogSource = Log;
            DevLog.Initialize(Log);
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(BetterAutoPlayPlugin).Assembly);
            ModalTracker.TryPatchModals(harmony);
            RewardModalTracker.TryPatchChooseCardModal(harmony);
            IL2CPPChainloader.AddUnityComponent<AutoPlayVisualUpdater>();
            Log.LogInfo(PluginName + " loaded");
        }
    }

    internal static class ModalTracker
    {
        private static int s_openCount;
        public static bool AnyOpen => s_openCount > 0;

        public static void TryPatchModals(Harmony harmony)
        {
            try
            {
                var modalType = AccessTools.TypeByName("Nosebleed.Pancake.Modal.Modal");
                if (modalType == null) { BetterAutoPlayPlugin.LogSource.LogWarning("[BAP] Modal type not found"); return; }

                var opened = AccessTools.Method(modalType, "OnOpened")
                    ?? AccessTools.Method(modalType, "OnModalOpened");
                var closed = AccessTools.Method(modalType, "OnClosed")
                    ?? AccessTools.Method(modalType, "OnModalClosed");
                var postfixOpened = new HarmonyMethod(typeof(ModalTracker), nameof(OnOpened));
                var postfixClosed  = new HarmonyMethod(typeof(ModalTracker), nameof(OnClosed));
                if (opened != null) harmony.Patch(opened, postfix: postfixOpened);
                if (closed != null) harmony.Patch(closed, postfix: postfixClosed);
                BetterAutoPlayPlugin.LogSource.LogInfo("[BAP] Modal patches applied");
            }
            catch (Exception ex) { BetterAutoPlayPlugin.LogSource.LogWarning("[BAP] Modal patch failed: " + ex.Message); }
        }

        public static void OnOpened() { s_openCount++; DevLog.Info("ModalTracker: opened, count=" + s_openCount); }
        public static void OnClosed()  { if (s_openCount > 0) s_openCount--; DevLog.Info("ModalTracker: closed, count=" + s_openCount); }
    }

    internal static class RewardModalTracker
    {
        public static void TryPatchChooseCardModal(Harmony harmony)
        {
            try
            {
                var modalType = AccessTools.TypeByName("Nosebleed.Pancake.Modal.ChooseCardModal");
                if (modalType == null)
                {
                    BetterAutoPlayPlugin.LogSource.LogWarning("[BAP] ChooseCardModal type not found");
                    return;
                }

                var closed = AccessTools.Method(modalType, "OnClosed");
                if (closed == null)
                {
                    BetterAutoPlayPlugin.LogSource.LogWarning("[BAP] ChooseCardModal.OnClosed not found");
                    return;
                }

                harmony.Patch(closed, postfix: new HarmonyMethod(typeof(RewardModalTracker), nameof(OnChooseCardModalClosed)));
                BetterAutoPlayPlugin.LogSource.LogInfo("[BAP] ChooseCardModal close patch applied");
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource.LogWarning("[BAP] ChooseCardModal patch failed: " + ex.Message);
            }
        }

        public static void OnChooseCardModalClosed()
        {
            try { AutoPlayUiController.OnChooseCardModalClosed(); }
            catch { }
        }
    }

    internal sealed class AutoPlayVisualUpdater : MonoBehaviour
    {
        public AutoPlayVisualUpdater(IntPtr ptr) : base(ptr) { }
        private void Update() { AutoPlayUiController.Tick(); }
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
                LiveHandCache.Set(cards);
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
            try { AutoPlayUiController.Refresh(__instance); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Selectable), "OnPointerEnter")]
    internal static class SelectablePointerEnterPatch
    {
        private static void Postfix(Selectable __instance)
        {
            try { AutoPlayUiController.OnButtonPointerEnter(__instance); } catch { }
        }
    }

    [HarmonyPatch(typeof(Selectable), "OnPointerExit")]
    internal static class SelectablePointerExitPatch
    {
        private static void Postfix(Selectable __instance)
        {
            try { AutoPlayUiController.OnButtonPointerExit(__instance); } catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerModel), "Button_AutoPlay")]
    internal static class PlayerModelButtonAutoPlayPatch
    {
        private static void Postfix(PlayerModel __instance)
        {
            try { AutoPlayUiController.SyncPersistentToggle(__instance); }
            catch { }
            try { AutoPlayUiController.Refresh(__instance); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerModel), "TryPlayCard")]
    internal static class PlayerModelTryPlayCardPatch
    {
        private static void Prefix(PlayerModel __instance, CardModel cardModel, bool isAutoPlay, out bool __state)
        {
            __state = false;
            if (!isAutoPlay)
                return;

            try { __state = __instance != null && cardModel != null && !__instance.CanAffordCard(cardModel); }
            catch { __state = false; }
            DevLog.Info("TryPlayCard Prefix: card=" + DescribeCard(cardModel) + ", isAutoPlay=true, cannotAffordBefore=" + __state);
        }

        private static void Postfix(PlayerModel __instance, CardModel cardModel, bool isAutoPlay, bool __result, bool __state)
        {
            if (!isAutoPlay)
                return;

            if (__result)
            {
                AutoPlayRetryCooldown.Clear(cardModel);
                SortOrderCache.MarkPlayed(cardModel);
                AutoPlayUiController.OnOrderStateChanged(cardModel);
                DevLog.Info("TryPlayCard Postfix: SUCCESS card=" + DescribeCard(cardModel) + " -> cooldown cleared");
                return;
            }

            bool cannotAffordAfter = false;
            try { cannotAffordAfter = __instance != null && cardModel != null && !__instance.CanAffordCard(cardModel); }
            catch { cannotAffordAfter = false; }

            if (__state || cannotAffordAfter)
            {
                AutoPlayRetryCooldown.MarkFailed(cardModel);
                AutoPlayUiController.OnOrderStateChanged(cardModel);
                DevLog.Info("TryPlayCard Postfix: FAIL card=" + DescribeCard(cardModel) + ", cannotAffordBefore=" + __state + ", cannotAffordAfter=" + cannotAffordAfter + " -> cooldown marked");
            }
            else
            {
                AutoPlayUiController.OnOrderStateChanged(cardModel);
                DevLog.Info("TryPlayCard Postfix: FAIL card=" + DescribeCard(cardModel) + ", non-mana failure -> no cooldown");
            }
        }

        private static string DescribeCard(CardModel cardModel)
        {
            if (cardModel == null)
                return "<null>";
            try
            {
                string name = cardModel.Name ?? "?";
                long ptr = cardModel.Pointer.ToInt64();
                return name + "@" + ptr;
            }
            catch
            {
                return "<card-ex>";
            }
        }
    }

    [HarmonyPatch(typeof(PlayerTurnState), "OnEnterState")]
    internal static class PlayerTurnStateOnEnterPatch
    {
        private static void Postfix()
        {
            try
            {
                AutoPlayUiController.OnTurnStarted(null);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerEndTurnState), "OnEnterState")]
    internal static class PlayerEndTurnStateOnEnterPatch
    {
        private static void Postfix()
        {
            try
            {
                AutoPlayUiController.OnTurnEnded(null);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(EncounterDefeatedState), "OnEnterState")]
    internal static class EncounterDefeatedStateOnEnterPatch
    {
        private static void Postfix()
        {
            try
            {
                AutoPlayUiController.OnEncounterDefeated(null);
            }
            catch { }
        }
    }
}
