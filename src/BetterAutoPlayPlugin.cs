using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
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
            DevLog.Initialize(Log);
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(BetterAutoPlayPlugin).Assembly);
            ModalTracker.TryPatchModals(harmony);
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
                var postfixClosed = new HarmonyMethod(typeof(ModalTracker), nameof(OnClosed));
                if (opened != null) harmony.Patch(opened, postfix: postfixOpened);
                if (closed != null) harmony.Patch(closed, postfix: postfixClosed);
                BetterAutoPlayPlugin.LogSource.LogInfo("[BAP] Modal patches applied");
            }
            catch (Exception ex) { BetterAutoPlayPlugin.LogSource.LogWarning("[BAP] Modal patch failed: " + ex.Message); }
        }

        public static void OnOpened() { s_openCount++; DevLog.Info("ModalTracker: opened, count=" + s_openCount); }
        public static void OnClosed() { if (s_openCount > 0) s_openCount--; DevLog.Info("ModalTracker: closed, count=" + s_openCount); }
    }

    internal static class DevLog
    {
        private const string MarkerFileName = "betterautoplay.devlog";
        private const string FullMarkerFileName = "betterautoplay.devlog.full";
        private const string LogFileName = "betterautoplay.dev.log";
        private static ManualLogSource s_log;
        private static string s_logFilePath;
        public static bool Enabled { get; private set; }
        public static bool FullEnabled { get; private set; }

        public static void Initialize(ManualLogSource log)
        {
            s_log = log;
            try
            {
                string asmPath = typeof(BetterAutoPlayPlugin).Assembly.Location;
                string dir = Path.GetDirectoryName(asmPath) ?? string.Empty;
                string markerPath = Path.Combine(dir, MarkerFileName);
                string fullMarkerPath = Path.Combine(dir, FullMarkerFileName);
                s_logFilePath = Path.Combine(dir, LogFileName);
                Enabled = File.Exists(markerPath);
                FullEnabled = Enabled && File.Exists(fullMarkerPath);
                if (Enabled)
                {
                    s_log.LogWarning("[DEVLOG] Enabled. Marker found: " + markerPath);
                    if (FullEnabled)
                        s_log.LogWarning("[DEVLOG] Full mode enabled. Marker found: " + fullMarkerPath);
                    AppendToFile("=== DEVLOG START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ===");
                }
            }
            catch (Exception ex)
            {
                Enabled = false;
                s_log?.LogWarning("[DEVLOG] Initialization failed: " + ex.Message);
            }
        }

        public static void Info(string message)
        {
            if (!Enabled || s_log == null)
                return;
            string line = "[DEVLOG] " + message;
            s_log.LogInfo(line);
            AppendToFile(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
        }

        private static void AppendToFile(string line)
        {
            if (string.IsNullOrEmpty(s_logFilePath))
                return;
            try
            {
                File.AppendAllText(s_logFilePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                s_log?.LogWarning("[DEVLOG] File write failed: " + ex.Message);
            }
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
        private const float RainbowHueCyclesPerSecond = 0.35f;
        private const float VisualRefreshIntervalSeconds = 0.05f;
        private const string AutoPlayLabel = "Auto Play";
        private const string AutoPlayingLabel = "Auto-Playing";
        private const float NoPlayableCardsBackoffSeconds = 0.5f;
        private static bool s_persistentAutoPlayIntent;
        private static PlayerModel s_lastPlayer;
        private static float s_nextResumeAttemptAt;
        private static float s_manualToggleCooldownUntil;
        private static string s_lastResumeSkipReason;
        private static float s_nextSkipRepeatLogAt;
        private static float s_nextVisualRefreshAt;
        private static TMP_Text s_cachedPlayAllLabel;
        private static string s_defaultPlayAllLabelText;
        private static Color s_defaultPlayAllLabelColor;
        private static bool s_labelOverridden;
        private static bool s_wasShowingAutoPlaying;

        private static readonly MethodInfo s_autoPlayerStopMethod = AccessTools.Method(typeof(AutoPlayer), "StopAutoPlay");
        private static readonly PropertyInfo s_handPileProp = AccessTools.Property(typeof(PlayerModel), "HandPile");
        private static PropertyInfo s_cardPileProp;        // HandPileModel -> CardPileModel
        private static PropertyInfo s_handCardsOnPileProp; // CardPileModel -> card list
        private static bool s_handPileDiscoveryLogged;

        // Order overlay
        private static Button s_orderButton;
        private static TMP_Text s_orderButtonLabel;
        private static GameObject s_overlayPanel;
        private static TMP_Text s_overlayContentText;
        private static bool s_overlayOpen;
        private static bool s_orderButtonCreated;
        private static Button s_lastSeenPlayAllButton;
        private static TMP_Text s_cachedPlayAllLabelForButton; // label cached per button instance
        private static readonly System.Text.StringBuilder s_overlaySb = new System.Text.StringBuilder(512);
        private static long s_lastHandFingerprint; // skip sort when hand unchanged

        public static void Tick()
        {
            if (DevLog.FullEnabled)
                DevLog.Info("Tick() -> TryResumePersistentAutoPlay()");
            TryResumePersistentAutoPlay();

            float now = Time.realtimeSinceStartup;
            if (now >= s_nextVisualRefreshAt)
            {
                s_nextVisualRefreshAt = now + VisualRefreshIntervalSeconds;
                Refresh(s_lastPlayer);
                if (s_overlayOpen)
                    UpdateOverlayContent();
            }
        }

        public static void SyncPersistentToggle(PlayerModel player)
        {
            if (player == null)
                return;

            s_persistentAutoPlayIntent = !s_persistentAutoPlayIntent;
            s_manualToggleCooldownUntil = Time.realtimeSinceStartup + 0.4f;
            DevLog.Info("SyncPersistentToggle: intent=" + s_persistentAutoPlayIntent + ", cooldownUntil=" + s_manualToggleCooldownUntil.ToString("F3"));
        }

        public static void Refresh(PlayerModel player)
        {
            if (player == null)
                return;
            s_lastPlayer = player;

            Button button = null;
            try { button = player.PlayAllButton; } catch { }

            if (button == null)
            {
                DevLog.Info("Refresh: PlayAll button is null, skipping visual update");
                return;
            }

            EnsureOrderButton(button);

            // Re-resolve label only when the button instance changes.
            TMP_Text label = s_cachedPlayAllLabelForButton;
            if (s_lastSeenPlayAllButton != button || label == null)
            {
                try { label = button.GetComponentInChildren<TMP_Text>(); } catch { }
                s_cachedPlayAllLabelForButton = label;
            }

            if (label == null)
            {
                DevLog.Info("Refresh: PlayAll label is null, skipping visual update");
                return;
            }

            string wantedOrderLabel = s_overlayOpen ? "Close" : "Order";
            if (s_orderButtonLabel != null && s_orderButtonLabel.text != wantedOrderLabel)
                s_orderButtonLabel.text = wantedOrderLabel;

            // Keep our button interactable; only write when it changed to avoid dirtying the canvas.
            if (s_orderButton != null)
                try { if (!s_orderButton.interactable) s_orderButton.interactable = true; } catch { }

            if (s_cachedPlayAllLabel != label)
            {
                s_cachedPlayAllLabel = label;
                s_defaultPlayAllLabelText = AutoPlayLabel;
                s_defaultPlayAllLabelColor = label.color;
                s_labelOverridden = false;
            }

            bool isPlaying = false;
            try
            {
                isPlaying = player.AutoPlayer != null && player.AutoPlayer.IsPlaying;
            }
            catch { }
            bool canActNow = false;
            try { canActNow = button != null && button.interactable; }
            catch { }

            bool shouldShowAutoPlaying = canActNow && (isPlaying || s_persistentAutoPlayIntent);
            if (shouldShowAutoPlaying)
            {
                if (!string.Equals(label.text, AutoPlayingLabel, StringComparison.Ordinal))
                    label.text = AutoPlayingLabel;
                float hue = Mathf.Repeat(Time.realtimeSinceStartup * RainbowHueCyclesPerSecond, 1f);
                var rainbow = Color.HSVToRGB(hue, 1f, 1f);
                rainbow.a = 1f;
                if (label.color != rainbow)
                    label.color = rainbow;
                s_labelOverridden = true;
                if (!s_wasShowingAutoPlaying)
                {
                    DevLog.Info("Refresh: entered Auto-Playing visual state");
                    s_wasShowingAutoPlaying = true;
                }
            }
            else if (s_labelOverridden)
            {
                label.text = s_defaultPlayAllLabelText;
                label.color = s_defaultPlayAllLabelColor;
                s_labelOverridden = false;
                if (s_wasShowingAutoPlaying)
                {
                    DevLog.Info("Refresh: exited Auto-Playing visual state");
                    s_wasShowingAutoPlaying = false;
                }
            }
            else if (!string.Equals(label.text, AutoPlayLabel, StringComparison.Ordinal))
            {
                label.text = AutoPlayLabel;
                if (s_wasShowingAutoPlaying)
                {
                    DevLog.Info("Refresh: exited Auto-Playing visual state");
                    s_wasShowingAutoPlaying = false;
                }
            }
        }

        private static void TryResumePersistentAutoPlay()
        {
            if (s_overlayOpen)
            {
                LogResumeSkip("order overlay is open");
                return;
            }

            if (ModalTracker.AnyOpen)
            {
                LogResumeSkip("modal is open");
                return;
            }

            if (!s_persistentAutoPlayIntent)
            {
                LogResumeSkip("persistent intent is off");
                return;
            }

            if (Time.realtimeSinceStartup < s_manualToggleCooldownUntil)
            {
                LogResumeSkip("manual toggle cooldown active");
                return;
            }

            if (Time.realtimeSinceStartup < s_nextResumeAttemptAt)
            {
                if (DevLog.FullEnabled)
                    LogResumeSkip("resume interval cooldown active");
                return;
            }
            s_nextResumeAttemptAt = Time.realtimeSinceStartup + 0.12f;

            var player = s_lastPlayer;
            if (player == null)
            {
                LogResumeSkip("last player is null");
                return;
            }

            bool isPlaying = false;
            try { isPlaying = player.AutoPlayer != null && player.AutoPlayer.IsPlaying; }
            catch { }
            if (isPlaying)
            {
                LogResumeSkip("autoplay already playing");
                return;
            }

            bool canPlayNow = false;
            try
            {
                var button = player.PlayAllButton;
                canPlayNow = button != null && button.interactable;
            }
            catch { }
            if (!canPlayNow)
            {
                LogResumeSkip("PlayAll button not interactable");
                return;
            }

            bool hasPlayableCardsNow = true;
            try { hasPlayableCardsNow = player.CanPlayerKeepPlaying(); }
            catch { }
            if (!hasPlayableCardsNow)
            {
                s_nextResumeAttemptAt = Time.realtimeSinceStartup + NoPlayableCardsBackoffSeconds;
                LogResumeSkip("no playable cards right now");
                return;
            }

            try
            {
                s_lastResumeSkipReason = null;
                DevLog.Info("TryResume: calling AutoPlayer.Play()");
                player.AutoPlayer?.Play();
            }
            catch { }
        }

        private static void EnsureOrderButton(Button playAllButton)
        {
            if (playAllButton == null) return;

            // If the PlayAll button changed (scene reload), tear down and rebuild.
            if (s_orderButtonCreated && s_lastSeenPlayAllButton != playAllButton)
            {
                if (s_orderButton != null)
                    try { GameObject.Destroy(s_orderButton.gameObject); } catch { }
                if (s_overlayPanel != null)
                    try { GameObject.Destroy(s_overlayPanel); } catch { }
                s_orderButton = null;
                s_orderButtonLabel = null;
                s_overlayPanel = null;
                s_overlayContentText = null;
                s_overlayOpen = false;
                s_orderButtonCreated = false;
            }

            if (s_orderButtonCreated) return;
            s_orderButtonCreated = true;
            s_lastSeenPlayAllButton = playAllButton;

            try
            {
                var origRt = playAllButton.GetComponent<RectTransform>();

                // Build button from scratch in the same parent so scale/depth are correct.
                var orderGo = new GameObject("BAPOrderButton");
                orderGo.transform.SetParent(playAllButton.transform.parent, false);
                orderGo.transform.SetAsLastSibling();

                var rt = orderGo.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                float w = origRt != null ? origRt.sizeDelta.x : 128f;
                float h = origRt != null ? origRt.sizeDelta.y : 64f;
                rt.sizeDelta = new Vector2(w, h);
                rt.anchoredPosition = new Vector2(-(w + 6f), 0f);

                // Copy background image style from PlayAll button.
                var srcImg = playAllButton.GetComponent<Image>();
                var img = orderGo.AddComponent<Image>();
                if (srcImg != null)
                {
                    img.sprite   = srcImg.sprite;
                    img.material = srcImg.material;
                    img.type     = srcImg.type;
                    img.color    = srcImg.color;
                }

                s_orderButton = orderGo.AddComponent<Button>();
                s_orderButton.targetGraphic = img;
                if (srcImg != null)
                {
                    s_orderButton.transition = playAllButton.transition;
                    s_orderButton.colors     = playAllButton.colors;
                }
                s_orderButton.onClick.AddListener(new System.Action(ToggleOverlay));

                var textGo = new GameObject("Label");
                textGo.transform.SetParent(orderGo.transform, false);
                var textRt = textGo.AddComponent<RectTransform>();
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = Vector2.zero;
                textRt.offsetMax = Vector2.zero;
                s_orderButtonLabel = textGo.AddComponent<TextMeshProUGUI>();
                // Copy font/size from the PlayAll label.
                var srcLabel = playAllButton.GetComponentInChildren<TMP_Text>();
                if (srcLabel != null)
                {
                    s_orderButtonLabel.font      = srcLabel.font;
                    s_orderButtonLabel.fontSize  = srcLabel.fontSize;
                    s_orderButtonLabel.fontStyle = srcLabel.fontStyle;
                }
                s_orderButtonLabel.text      = "Order";
                s_orderButtonLabel.alignment = TextAlignmentOptions.Center;
                s_orderButtonLabel.color     = Color.white;

                CreateOverlayPanel(playAllButton, h);
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource.LogWarning("EnsureOrderButton failed: " + ex.Message);
            }
        }

        private static Transform FindCanvasTransform(Transform start)
        {
            Transform t = start;
            while (t != null)
            {
                try
                {
                    var comps = t.GetComponents<Component>();
                    if (comps != null)
                        foreach (var c in comps)
                            if (c != null && c.GetType().Name == "Canvas")
                                return t;
                }
                catch { }
                t = t.parent;
            }
            return start; // fallback: stay where we are
        }

        private static void CreateOverlayPanel(Button playAllButton, float buttonHeight)
        {
            var buttonParent = playAllButton.transform.parent;

            var panelGo = new GameObject("BAPOrderPanel");
            // Create in same parent first so local-space coordinates work correctly.
            panelGo.transform.SetParent(buttonParent, false);

            float panelH = 500f;
            var rt = panelGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(380f, panelH);
            rt.anchoredPosition = new Vector2(0f, buttonHeight * 0.5f + panelH * 0.5f + 8f);

            // Lift to the Canvas (not the absolute root) as last sibling so it renders above everything.
            Transform canvasTransform = FindCanvasTransform(buttonParent);
            panelGo.transform.SetParent(canvasTransform, true);
            panelGo.transform.SetAsLastSibling();

            var bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.04f, 0.10f, 0.94f);

            // Title
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.offsetMin = new Vector2(12f, -48f);
            titleRt.offsetMax = new Vector2(-12f, -8f);
            var titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.text = "Play Order  <size=11><color=#888888>(autoplay paused)</color></size>";
            titleText.fontSize = 17;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;

            // Content
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(panelGo.transform, false);
            var contentRt = contentGo.AddComponent<RectTransform>();
            contentRt.anchorMin = Vector2.zero;
            contentRt.anchorMax = Vector2.one;
            contentRt.offsetMin = new Vector2(14f, 12f);
            contentRt.offsetMax = new Vector2(-14f, -56f);
            var contentText = contentGo.AddComponent<TextMeshProUGUI>();
            contentText.fontSize = 13f;
            contentText.alignment = TextAlignmentOptions.TopLeft;
            contentText.color = Color.white;
            s_overlayContentText = contentText;

            s_overlayPanel = panelGo;
            panelGo.SetActive(false);
        }

        private static void ToggleOverlay()
        {
            s_overlayOpen = !s_overlayOpen;
            DevLog.Info("ToggleOverlay: open=" + s_overlayOpen);

            if (s_overlayOpen)
            {
                // Stop autoplay immediately.
                try { s_autoPlayerStopMethod?.Invoke(s_lastPlayer?.AutoPlayer, null); } catch { }
                s_lastHandFingerprint = 0; // force immediate refresh on open
            }

            if (s_overlayPanel != null)
                s_overlayPanel.SetActive(s_overlayOpen);

            if (s_orderButtonLabel != null)
                s_orderButtonLabel.text = s_overlayOpen ? "Close" : "Order";

            if (s_overlayOpen)
                UpdateOverlayContent();
        }

        private static void TryLiveRefreshHand()
        {
            if (s_lastPlayer == null) return;
            try
            {
                List<CardModel> hand = null;

                // Path: player.HandPile (HandPileModel) -> .CardPile (CardPileModel) -> .[cards]
                var handPile = s_handPileProp?.GetValue(s_lastPlayer);
                if (handPile != null)
                {
                    // Step 1: find CardPile property on HandPileModel (cached after first find)
                    if (s_cardPileProp == null)
                    {
                        var type = handPile.GetType();
                        s_cardPileProp = type.GetProperty("CardPile");
                        if (s_cardPileProp == null && !s_handPileDiscoveryLogged)
                        {
                            s_handPileDiscoveryLogged = true;
                            foreach (var p in type.GetProperties())
                                BetterAutoPlayPlugin.LogSource.LogInfo("[BAP] HandPileModel prop: " + p.Name + " -> " + p.PropertyType.Name);
                        }
                    }

                    if (s_cardPileProp != null)
                    {
                        var cardPile = s_cardPileProp.GetValue(handPile);
                        if (cardPile != null)
                        {
                            // Step 2: find card-list property on CardPileModel (cached after first find)
                            if (s_handCardsOnPileProp == null)
                            {
                                var type = cardPile.GetType();
                                foreach (var name in new[] { "Cards", "CardList", "Pile", "HandCards", "Items", "AllCards", "CardModels" })
                                {
                                    var p = type.GetProperty(name);
                                    if (p != null) { s_handCardsOnPileProp = p; break; }
                                }
                                if (s_handCardsOnPileProp == null && !s_handPileDiscoveryLogged)
                                {
                                    s_handPileDiscoveryLogged = true;
                                    foreach (var p in type.GetProperties())
                                        BetterAutoPlayPlugin.LogSource.LogInfo("[BAP] CardPileModel prop: " + p.Name + " -> " + p.PropertyType.Name);
                                }
                            }

                            if (s_handCardsOnPileProp != null)
                            {
                                var raw = s_handCardsOnPileProp.GetValue(cardPile);
                                if (raw != null) hand = Il2CppListAdapter.ToManaged(raw);
                            }
                        }
                    }
                }

                // Fallback to cached list from last SortCardsByCombo call.
                if (hand == null || hand.Count == 0)
                    hand = LiveHandCache.Get();

                if (hand == null || hand.Count == 0) return;

                // Skip the sort + allocation if the hand hasn't changed since last tick.
                long fp = HandFingerprint(hand);
                if (fp == s_lastHandFingerprint) return;
                s_lastHandFingerprint = fp;

                var sorted = ComboManaSorter.SortPreservePlayed(s_lastPlayer, hand);
                SortOrderCache.LiveUpdate(sorted, s_lastPlayer);
            }
            catch { }
        }

        private static long HandFingerprint(List<CardModel> hand)
        {
            long fp = hand.Count;
            for (int i = 0; i < hand.Count; i++)
            {
                var c = hand[i];
                if (c == null) continue;
                try { fp ^= c.Pointer.ToInt64() * (i + 1); } catch { }
            }
            return fp;
        }

        private static void UpdateOverlayContent()
        {
            if (s_overlayContentText == null) return;

            TryLiveRefreshHand();
            SortOrderCache.RefreshAffordability();

            var entries = SortOrderCache.Entries;
            if (entries.Count == 0)
            {
                s_overlayContentText.text = "<color=#666666>No sort data yet.\nTrigger autoplay once to populate.</color>";
                return;
            }

            var sb = s_overlaySb;
            sb.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Played)
                {
                    sb.Append("<color=#555555>").Append(i + 1).Append(".</color> ");
                    sb.Append("<color=#44cc44>").Append(e.Name).Append("  Played</color>");
                }
                else
                {
                    string roleColor = RoleColor(e.Role);
                    string nameColor = e.CanAfford ? "#ffffff" : "#ff5555";
                    sb.Append("<color=#555555>").Append(i + 1).Append(".</color> ");
                    sb.Append("<color=").Append(nameColor).Append(">").Append(e.Name).Append("</color>");
                    sb.Append("  <color=").Append(roleColor).Append("><size=11>[").Append(e.Role).Append("]</size></color>");
                    sb.Append("  <color=#777777><size=11>").Append(e.ManaCost).Append("mp</size></color>");
                }
                sb.AppendLine();
            }
            s_overlayContentText.text = sb.ToString();
        }

        private static string RoleColor(CardRole role)
        {
            switch (role)
            {
                case CardRole.ManaGenerator: return "#44aaff";
                case CardRole.Utility:       return "#ffcc44";
                case CardRole.Crawler:       return "#bb66ff";
                case CardRole.Attack:        return "#ff4444";
                default:                     return "#777777";
            }
        }

        private static void LogResumeSkip(string reason)
        {
            float now = Time.realtimeSinceStartup;
            bool reasonChanged = !string.Equals(s_lastResumeSkipReason, reason, StringComparison.Ordinal);
            bool repeatWindowPassed = now >= s_nextSkipRepeatLogAt;
            if (!reasonChanged && !repeatWindowPassed)
                return;

            s_lastResumeSkipReason = reason;
            s_nextSkipRepeatLogAt = now + 2f;
            DevLog.Info("TryResume: skipped, " + reason);
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
        private static readonly PropertyInfo s_onPlayEffectsProp =
            AccessTools.Property(typeof(CardConfig), "OnPlayEffects");

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
                var effects = s_onPlayEffectsProp?.GetValue(config);
                if (effects == null) return false;

                PropertyInfo countProp;
                MethodInfo getItem;
                if (!ReflectionCache.TryGetListAccessors(effects.GetType(), out countProp, out getItem))
                    return false;
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
                LiveHandCache.Set(cards); // cache the live list for overlay updates
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

    internal static class AutoPlayRetryCooldown
    {
        private const float RetryDelaySeconds = 0.5f;
        private static readonly Dictionary<long, float> s_retryReadyAtByCardPtr = new Dictionary<long, float>();

        public static bool IsCoolingDown(CardModel cardModel)
        {
            long key = GetCardKey(cardModel);
            if (key == 0)
                return false;

            float readyAt;
            if (!s_retryReadyAtByCardPtr.TryGetValue(key, out readyAt))
                return false;

            if (Time.realtimeSinceStartup >= readyAt)
            {
                s_retryReadyAtByCardPtr.Remove(key);
                return false;
            }

            return true;
        }

        public static void MarkFailed(CardModel cardModel)
        {
            long key = GetCardKey(cardModel);
            if (key == 0)
                return;
            s_retryReadyAtByCardPtr[key] = Time.realtimeSinceStartup + RetryDelaySeconds;
        }

        public static void Clear(CardModel cardModel)
        {
            long key = GetCardKey(cardModel);
            if (key == 0)
                return;
            s_retryReadyAtByCardPtr.Remove(key);
        }

        private static long GetCardKey(CardModel cardModel)
        {
            try
            {
                if (cardModel == null || cardModel.Pointer == IntPtr.Zero)
                    return 0;
                return cardModel.Pointer.ToInt64();
            }
            catch
            {
                return 0;
            }
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
                if (isAutoPlay) SortOrderCache.MarkPlayed(cardModel);
                DevLog.Info("TryPlayCard Postfix: SUCCESS card=" + DescribeCard(cardModel) + " -> cooldown cleared");
                return;
            }

            bool cannotAffordAfter = false;
            try { cannotAffordAfter = __instance != null && cardModel != null && !__instance.CanAffordCard(cardModel); }
            catch { cannotAffordAfter = false; }

            if (__state || cannotAffordAfter)
            {
                AutoPlayRetryCooldown.MarkFailed(cardModel);
                DevLog.Info("TryPlayCard Postfix: FAIL card=" + DescribeCard(cardModel) + ", cannotAffordBefore=" + __state + ", cannotAffordAfter=" + cannotAffordAfter + " -> cooldown marked");
            }
            else
            {
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

    internal static class LiveHandCache
    {
        private static object s_rawList;
        public static void Set(object list) { s_rawList = list; }
        public static List<CardModel> Get()
        {
            if (s_rawList == null) return null;
            try { return Il2CppListAdapter.ToManaged(s_rawList); }
            catch { return null; }
        }
    }

    internal static class SortOrderCache
    {
        internal struct CardEntry
        {
            public string Name;
            public CardRole Role;
            public int ManaCost;
            public bool CanAfford;
            public bool Played;
        }

        public static readonly List<CardEntry> Entries = new List<CardEntry>();
        private static readonly List<CardModel> s_cardRefs = new List<CardModel>();
        private static readonly HashSet<long> s_playedPtrs = new HashSet<long>();
        private static PlayerModel s_player;

        public static void Update(List<CardModel> sorted, PlayerModel player)
        {
            s_player = player;
            s_playedPtrs.Clear();
            Rebuild(sorted, player);
        }

        public static void LiveUpdate(List<CardModel> sorted, PlayerModel player)
        {
            s_player = player;
            // Preserve played tracking across live refresh.
            Rebuild(sorted, player);
        }

        private static void Rebuild(List<CardModel> sorted, PlayerModel player)
        {
            Entries.Clear();
            s_cardRefs.Clear();
            foreach (var card in sorted)
            {
                if (card == null) continue;
                s_cardRefs.Add(card);
                var role = CardClassifier.Classify(card);
                int mana = int.MaxValue;
                try { mana = card.GetCardCostTypeManaCost(false); } catch { }
                bool canAfford = true;
                try { if (player != null) canAfford = player.CanAffordCard(card); } catch { }
                long ptr = 0;
                try { ptr = card.Pointer.ToInt64(); } catch { }
                bool played = s_playedPtrs.Contains(ptr);
                Entries.Add(new CardEntry { Name = card.Name ?? "?", Role = role, ManaCost = mana == int.MaxValue ? 0 : mana, CanAfford = canAfford, Played = played });
            }
        }

        public static void RefreshAffordability()
        {
            if (s_player == null) return;
            for (int i = 0; i < s_cardRefs.Count && i < Entries.Count; i++)
            {
                var card = s_cardRefs[i];
                if (card == null) continue;
                var e = Entries[i];
                try { e.CanAfford = s_player.CanAffordCard(card); } catch { }
                try { e.Played = s_playedPtrs.Contains(card.Pointer.ToInt64()); } catch { }
                Entries[i] = e;
            }
        }

        public static void MarkPlayed(CardModel card)
        {
            if (card == null) return;
            try
            {
                long ptr = card.Pointer.ToInt64();
                if (ptr == 0) return;
                s_playedPtrs.Add(ptr);
                for (int i = 0; i < s_cardRefs.Count; i++)
                {
                    var c = s_cardRefs[i];
                    if (c != null && c.Pointer.ToInt64() == ptr)
                    {
                        var e = Entries[i];
                        e.Played = true;
                        Entries[i] = e;
                        break;
                    }
                }
            }
            catch { }
        }
    }

    internal static class ComboManaSorter
    {
        private static readonly PropertyInfo s_gainAmountProp =
            AccessTools.Property(typeof(Nosebleed.Pancake.GameCommands.GainManaEffect), "_gainAmount");
        private static readonly PropertyInfo s_onPlayEffectsProp =
            AccessTools.Property(typeof(CardConfig), "OnPlayEffects");
        private static readonly MethodInfo s_isCardFreeToPlayMethod =
            AccessTools.Method(typeof(CardModel), "IsCardFreeToPlay", Type.EmptyTypes);
        private static readonly MethodInfo s_playerIsCardInComboMethod =
            AccessTools.Method(typeof(PlayerModel), "IsCardInCombo", new[] { typeof(ICardModel), typeof(bool) });
        private static readonly MethodInfo s_cardConfigGetManaCostMethod =
            AccessTools.Method(typeof(CardConfig), "GetManaCost", Type.EmptyTypes);

        public static List<CardModel> Sort(PlayerModel player, List<CardModel> input)
        {
            var ordered = SortCore(player, input);
            SortOrderCache.Update(ordered, player);
            return ordered;
        }

        public static List<CardModel> SortPreservePlayed(PlayerModel player, List<CardModel> input)
        {
            return SortCore(player, input); // caller handles LiveUpdate
        }

        private static List<CardModel> SortCore(PlayerModel player, List<CardModel> input)
        {
            var immediate = new List<CardModel>(input.Count);
            var deferred = new List<CardModel>(input.Count);
            RetryPolicy.PartitionByImmediatePlayability(player, input, immediate, deferred);
            DevLog.Info("Sort: input=" + input.Count + ", immediate=" + immediate.Count + ", deferred=" + deferred.Count);

            var ordered = SortSubset(player, immediate, BuildContext(player, immediate));

            if (deferred.Count > 0)
            {
                var deferredOrdered = SortSubset(player, deferred, BuildContext(player, deferred));
                ordered.AddRange(deferredOrdered);
            }
            return ordered;
        }

        private static List<CardModel> SortSubset(PlayerModel player, List<CardModel> subset, AutoPlaySortContext context)
        {
            var remaining = new List<CardModel>(subset);
            var ordered = new List<CardModel>(subset.Count);

            CardModel start = PickStartingCard(player, remaining, context);
            if (start == null)
            {
                DevLog.Info("SortSubset: no combo start found, using fallback role sort for all cards");
                AppendRoleSorted(ordered, remaining, context);
                return ordered;
            }

            DevLog.Info("SortSubset: start=" + SafeName(start));
            ordered.Add(start);
            remaining.Remove(start);

            CardModel previous = start;
            int previousMana = GetManaCost(start, context);

            while (remaining.Count > 0)
            {
                CardModel next = PickNextComboCard(remaining, previous, previousMana, context);
                if (next == null)
                {
                    DevLog.Info("SortSubset: combo chain ended, appending leftovers");
                    break;
                }

                DevLog.Info("SortSubset: next=" + SafeName(next) + ", previous=" + SafeName(previous) + ", previousMana=" + previousMana);
                ordered.Add(next);
                remaining.Remove(next);
                previous = next;
                previousMana = GetManaCost(next, context);
            }

            AppendRoleSorted(ordered, remaining, context);
            return ordered;
        }

        private static AutoPlaySortContext BuildContext(PlayerModel player, List<CardModel> subset)
        {
            var originalIndex = BuildOriginalIndex(subset);
            var roles = BuildRoles(subset);
            var manaGains = BuildManaGains(subset, roles);
            var manaCosts = BuildManaCosts(subset);
            var comboCosts = BuildComboCosts(subset);
            var evolved = BuildEvolvedFlags(subset);
            var inComboNow = BuildInComboFlags(player, subset);
            return new AutoPlaySortContext(originalIndex, roles, manaGains, manaCosts, comboCosts, evolved, inComboNow);
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
                    ? ComputeManaGain(card, ComputeRawManaCost)
                    : 0;
                gains[card] = gain;
                if (gain > 0)
                    BetterAutoPlayPlugin.LogSource?.LogDebug(
                        $"[ManaGain] '{SafeName(card)}' gives {gain} mana");
            }
            return gains;
        }

        // Sum all mana-gain effects on a card's on-play list.
        private static int ComputeManaGain(CardModel card, Func<CardModel, int> getManaCost)
        {
            try
            {
                var effects = s_onPlayEffectsProp?.GetValue(card.CardConfig);
                if (effects == null) return 0;

                PropertyInfo countProp;
                MethodInfo getItem;
                if (!ReflectionCache.TryGetListAccessors(effects.GetType(), out countProp, out getItem))
                    return 0;
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
                        total += getManaCost(card);
                }
                return total;
            }
            catch { return 0; }
        }

        // Pick the starting card: prefer whatever is currently continuing an existing combo,
        // then fall back to the best role+mana card overall.
        private static CardModel PickStartingCard(PlayerModel player, List<CardModel> cards, AutoPlaySortContext context)
        {
            CardModel bestInCombo = null;
            CardModel bestAny = null;

            foreach (var card in cards)
            {
                if (card == null) continue;
                if (IsCurrentlyInCombo(player, card, context))
                    bestInCombo = BetterCard(bestInCombo, card, context);
                bestAny = BetterCard(bestAny, card, context);
            }

            return bestInCombo ?? bestAny;
        }

        // Among cards that can combo after `previous`:
        //   Tier 1 — Same-cost ManaGenerators.
        //   Tier 2 — Strictly increasing mana, again favoring ManaGenerators inside the tier.
        //   Tier 3 — Anything with decreasing cost (final fallback).
        // This keeps valid 0->1->1->1->2 generator chains alive before stepping up,
        // without spending same-cost non-generators before the next combo step.
        private static CardModel PickNextComboCard(List<CardModel> cards, CardModel previous, int previousMana, AutoPlaySortContext context)
        {
            CardModel bestFlatManaGen = null;
            CardModel bestStrictIncrease = null;
            CardModel bestDecreasing = null;

            foreach (var card in cards)
            {
                if (card == null || !CanComboAfter(card, previous))
                    continue;

                int mana = GetManaCost(card, context);
                CardRole role = GetRole(card, context);

                if (mana == previousMana && role == CardRole.ManaGenerator)
                    bestFlatManaGen = BetterCard(bestFlatManaGen, card, context);
                else if (mana > previousMana)
                    bestStrictIncrease = BetterCard(bestStrictIncrease, card, context);
                else if (mana < previousMana)
                    bestDecreasing = BetterCard(bestDecreasing, card, context);
            }

            // When currently below zero mana-cost, prioritize climbing the combo ladder first.
            // This ensures -1 -> 0 style transitions are not skipped by role-first heuristics.
            if (previousMana < 0)
                return bestStrictIncrease ?? bestFlatManaGen ?? bestDecreasing;

            return bestFlatManaGen ?? bestStrictIncrease ?? bestDecreasing;
        }

        // Sort leftover (non-combo) cards: role first, then mana gain (desc for generators),
        // then mana cost, then combo cost, then original order.
        private static void AppendRoleSorted(List<CardModel> ordered, List<CardModel> remaining, AutoPlaySortContext context)
        {
            remaining.Sort(delegate(CardModel left, CardModel right)
            {
                CardRole leftRole = GetRole(left, context);
                CardRole rightRole = GetRole(right, context);
                int r = leftRole.CompareTo(rightRole);
                if (r != 0) return r;

                // For mana generators: higher gain plays first (descending)
                if (leftRole == CardRole.ManaGenerator)
                {
                    r = GetManaGain(right, context).CompareTo(GetManaGain(left, context));
                    if (r != 0) return r;
                }

                r = GetManaCost(left, context).CompareTo(GetManaCost(right, context));
                if (r != 0) return r;

                // Same mana: evolved cards play before non-evolved.
                bool le = IsEvolved(left, context);
                bool re2 = IsEvolved(right, context);
                if (le && !re2) return -1;
                if (!le && re2) return 1;

                r = GetComboCost(left, context).CompareTo(GetComboCost(right, context));
                if (r != 0) return r;

                return GetOriginalIndex(context, left).CompareTo(GetOriginalIndex(context, right));
            });
            ordered.AddRange(remaining);
        }

        // Role-first comparison; for ManaGenerator ties, prefer higher gain; otherwise lowest mana.
        private static CardModel BetterCard(CardModel current, CardModel candidate, AutoPlaySortContext context)
        {
            if (current == null) return candidate;

            // Prefer negative mana cards over non-negative cards to preserve
            // valid negative->zero combo openings.
            int candidateMana = GetManaCost(candidate, context);
            int currentMana = GetManaCost(current, context);
            bool candidateNegative = candidateMana < 0;
            bool currentNegative = currentMana < 0;
            if (candidateNegative != currentNegative)
                return candidateNegative ? candidate : current;

            CardRole candidateRole = GetRole(candidate, context);
            CardRole currentRole = GetRole(current, context);
            int r = candidateRole.CompareTo(currentRole);
            if (r < 0) return candidate;
            if (r > 0) return current;

            // Same role: ManaGenerators prefer higher gain (more mana = play first)
            if (candidateRole == CardRole.ManaGenerator)
            {
                r = GetManaGain(candidate, context).CompareTo(GetManaGain(current, context));
                if (r > 0) return candidate; // candidate gives more mana
                if (r < 0) return current;
            }

            return BetterLowestMana(current, candidate, context);
        }

        private static int GetManaGain(CardModel card, AutoPlaySortContext context)
        {
            int gain;
            return card != null && context.ManaGains.TryGetValue(card, out gain) ? gain : 0;
        }

        private static CardModel BetterLowestMana(CardModel current, CardModel candidate, AutoPlaySortContext context)
        {
            if (current == null) return candidate;

            int r = GetManaCost(candidate, context).CompareTo(GetManaCost(current, context));
            if (r < 0) return candidate;
            if (r > 0) return current;

            // Same mana: evolved cards play before non-evolved.
            bool ce = IsEvolved(candidate, context);
            bool cu = IsEvolved(current, context);
            if (ce && !cu) return candidate;
            if (!ce && cu) return current;

            r = GetComboCost(candidate, context).CompareTo(GetComboCost(current, context));
            if (r < 0) return candidate;
            if (r > 0) return current;

            return GetOriginalIndex(context, candidate) < GetOriginalIndex(context, current)
                ? candidate : current;
        }

        private static bool IsEvolved(CardModel card, AutoPlaySortContext context)
        {
            bool value;
            return card != null && context.Evolved.TryGetValue(card, out value) && value;
        }

        private static CardRole GetRole(CardModel card, AutoPlaySortContext context)
        {
            if (card == null) return CardRole.Unknown;
            CardRole role;
            return context.Roles.TryGetValue(card, out role) ? role : CardRole.Unknown;
        }

        private static bool IsCurrentlyInCombo(PlayerModel player, CardModel card, AutoPlaySortContext context)
        {
            bool value;
            return card != null && context.InComboNow.TryGetValue(card, out value) && value;
        }

        private static bool CanComboAfter(CardModel card, CardModel previous)
        {
            if (card == null || previous == null) return false;
            try { return card.CanCardCostTypeCombo(previous); }
            catch { return false; }
        }

        private static int GetManaCost(CardModel card, AutoPlaySortContext context)
        {
            int cost;
            return card != null && context.ManaCosts.TryGetValue(card, out cost) ? cost : int.MaxValue;
        }

        private static int ComputeRawManaCost(CardModel card)
        {
            if (card == null) return int.MaxValue;

            // Free-to-play cards should still participate in combo ladder based on base mana.
            int baseMana = GetBaseManaCostIfFree(card);
            if (baseMana != int.MaxValue)
                return baseMana;

            try { return card.GetCardCostTypeManaCost(false); }
            catch { return int.MaxValue; }
        }

        private static int GetBaseManaCostIfFree(CardModel card)
        {
            if (!IsCardFreeToPlay(card))
                return int.MaxValue;

            try
            {
                var config = card?.CardConfig;
                if (config == null || s_cardConfigGetManaCostMethod == null)
                    return int.MaxValue;

                return Convert.ToInt32(s_cardConfigGetManaCostMethod.Invoke(config, null));
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private static bool IsCardFreeToPlay(CardModel card)
        {
            if (card == null)
                return false;

            try
            {
                if (s_isCardFreeToPlayMethod != null)
                    return Convert.ToBoolean(s_isCardFreeToPlayMethod.Invoke(card, null));
            }
            catch { }

            return false;
        }

        private static int GetComboCost(CardModel card, AutoPlaySortContext context)
        {
            int combo;
            return card != null && context.ComboCosts.TryGetValue(card, out combo) ? combo : int.MaxValue;
        }

        private static int ComputeRawComboCost(CardModel card)
        {
            if (card == null) return int.MaxValue;
            try { return card.GetCardComboCost(false); }
            catch { return int.MaxValue; }
        }

        private static Dictionary<CardModel, int> BuildManaCosts(List<CardModel> input)
        {
            var manaCosts = new Dictionary<CardModel, int>(input.Count);
            foreach (var card in input)
            {
                if (card != null && !manaCosts.ContainsKey(card))
                {
                    manaCosts[card] = ComputeRawManaCost(card);
                    DevLog.Info("CardFacts: manaCost " + SafeName(card) + " = " + manaCosts[card]);
                }
            }
            return manaCosts;
        }

        private static Dictionary<CardModel, int> BuildComboCosts(List<CardModel> input)
        {
            var comboCosts = new Dictionary<CardModel, int>(input.Count);
            foreach (var card in input)
            {
                if (card != null && !comboCosts.ContainsKey(card))
                {
                    comboCosts[card] = ComputeRawComboCost(card);
                    DevLog.Info("CardFacts: comboCost " + SafeName(card) + " = " + comboCosts[card]);
                }
            }
            return comboCosts;
        }

        private static Dictionary<CardModel, bool> BuildEvolvedFlags(List<CardModel> input)
        {
            var evolved = new Dictionary<CardModel, bool>(input.Count);
            foreach (var card in input)
            {
                if (card == null || evolved.ContainsKey(card))
                    continue;
                bool value;
                try { value = card.IsEvolved; }
                catch { value = false; }
                evolved[card] = value;
                DevLog.Info("CardFacts: evolved " + SafeName(card) + " = " + value);
            }
            return evolved;
        }

        private static Dictionary<CardModel, bool> BuildInComboFlags(PlayerModel player, List<CardModel> input)
        {
            var inCombo = new Dictionary<CardModel, bool>(input.Count);
            foreach (var card in input)
            {
                if (card == null || inCombo.ContainsKey(card))
                    continue;
                inCombo[card] = ComputeIsCurrentlyInCombo(player, card);
                DevLog.Info("CardFacts: inComboNow " + SafeName(card) + " = " + inCombo[card]);
            }
            return inCombo;
        }

        private static bool ComputeIsCurrentlyInCombo(PlayerModel player, CardModel card)
        {
            if (player == null || card == null) return false;
            try
            {
                if (s_playerIsCardInComboMethod != null)
                    return Convert.ToBoolean(s_playerIsCardInComboMethod.Invoke(player, new object[] { card, true }));
            }
            catch { return false; }
            return false;
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

        private static int GetOriginalIndex(AutoPlaySortContext context, CardModel card)
        {
            int result;
            return card != null && context.OriginalIndex.TryGetValue(card, out result) ? result : int.MaxValue;
        }

        private static string SafeName(CardModel card)
        {
            try { return card?.Name ?? "?"; }
            catch { return "?"; }
        }
    }

    internal sealed class AutoPlaySortContext
    {
        public AutoPlaySortContext(
            Dictionary<CardModel, int> originalIndex,
            Dictionary<CardModel, CardRole> roles,
            Dictionary<CardModel, int> manaGains,
            Dictionary<CardModel, int> manaCosts,
            Dictionary<CardModel, int> comboCosts,
            Dictionary<CardModel, bool> evolved,
            Dictionary<CardModel, bool> inComboNow)
        {
            OriginalIndex = originalIndex;
            Roles = roles;
            ManaGains = manaGains;
            ManaCosts = manaCosts;
            ComboCosts = comboCosts;
            Evolved = evolved;
            InComboNow = inComboNow;
        }

        public Dictionary<CardModel, int> OriginalIndex { get; }
        public Dictionary<CardModel, CardRole> Roles { get; }
        public Dictionary<CardModel, int> ManaGains { get; }
        public Dictionary<CardModel, int> ManaCosts { get; }
        public Dictionary<CardModel, int> ComboCosts { get; }
        public Dictionary<CardModel, bool> Evolved { get; }
        public Dictionary<CardModel, bool> InComboNow { get; }
    }

    internal static class RetryPolicy
    {
        public static void PartitionByImmediatePlayability(PlayerModel player, List<CardModel> input, List<CardModel> immediate, List<CardModel> deferred)
        {
            foreach (var card in input)
            {
                if (card == null)
                {
                    deferred.Add(card);
                    continue;
                }

                if (CanPlayImmediately(player, card))
                {
                    immediate.Add(card);
                    DevLog.Info("Partition: immediate " + DescribeCard(card));
                }
                else
                {
                    deferred.Add(card);
                    DevLog.Info("Partition: deferred " + DescribeCard(card));
                }
            }
        }

        private static bool CanPlayImmediately(PlayerModel player, CardModel card)
        {
            if (card == null)
                return false;

            if (AutoPlayRetryCooldown.IsCoolingDown(card))
            {
                DevLog.Info("CanPlayImmediately: false due to cooldown " + DescribeCard(card));
                return false;
            }

            if (player == null)
                return true;

            try
            {
                bool canAfford = player.CanAffordCard(card);
                DevLog.Info("CanPlayImmediately: CanAffordCard(" + DescribeCard(card) + ") = " + canAfford);
                return canAfford;
            }
            catch { return true; }
        }

        private static string DescribeCard(CardModel card)
        {
            if (card == null)
                return "<null>";
            try
            {
                string name = card.Name ?? "?";
                long ptr = card.Pointer.ToInt64();
                return name + "@" + ptr;
            }
            catch
            {
                return "<card-ex>";
            }
        }
    }

    internal static class ReflectionCache
    {
        private static readonly Dictionary<Type, PropertyInfo> s_countAccessorByType = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, MethodInfo> s_getItemAccessorByType = new Dictionary<Type, MethodInfo>();

        public static bool TryGetListAccessors(Type type, out PropertyInfo countProp, out MethodInfo getItemMethod)
        {
            countProp = null;
            getItemMethod = null;
            if (type == null)
                return false;

            if (!s_countAccessorByType.TryGetValue(type, out countProp))
            {
                countProp = type.GetProperty("Count") ?? type.GetProperty("Length");
                s_countAccessorByType[type] = countProp;
            }

            if (!s_getItemAccessorByType.TryGetValue(type, out getItemMethod))
            {
                getItemMethod = type.GetMethod("get_Item");
                s_getItemAccessorByType[type] = getItemMethod;
            }

            return countProp != null && getItemMethod != null;
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
