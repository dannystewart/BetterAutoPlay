using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Nosebleed.Pancake.Audio;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
        private void Update() { AutoPlayUiController.Tick(); }
    }


    internal static class AutoPlayUiController
    {
        private const float RainbowHueCyclesPerSecond = 0.35f;
        private const float VisualRefreshIntervalSeconds = 0.05f;
        private const string AutoPlayLabel = "Auto Play";
        private const string AutoPlayingLabel = "Auto Playing";
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
        private static TMP_Text s_refreshButtonLabel;

        // Button images + hover colors for direct color change on pointer enter/exit
        private static Image s_orderButtonImg;
        private static Color s_orderButtonNormal;
        private static Color s_orderButtonHover;
        private static Image s_refreshButtonImg;
        private static Color s_refreshButtonNormal;
        private static Color s_refreshButtonHover;

        // Pre-computed overlay colors (avoid per-tick HtmlColor/hex parsing)
        private static readonly Color s_overlayIndexColor  = new Color(0.333f, 0.333f, 0.333f, 1f); // #555555
        private static readonly Color s_overlayPlayedColor = new Color(0.267f, 0.800f, 0.267f, 1f); // #44cc44
        private static readonly Color s_overlayUnAffordColor = new Color(1f, 0.333f, 0.333f, 1f);   // #ff5555
        private static readonly Color s_overlayManaColor   = new Color(0.812f, 0.812f, 0.812f, 1f); // #cfcfcf

        // Order overlay
        private static TMP_FontAsset s_gameFont; // cached from PlayAll button label
        private static Button s_orderButton;
        private static TMP_Text s_orderButtonLabel;
        private static Button s_refreshButton;
        private static GameObject s_overlayPanel;
        private static GameObject s_overlayContentRoot;
        private static TMP_Text s_overlayContentText;
        private static bool s_overlayOpen;
        private static bool s_orderButtonCreated;
        private static Button s_lastSeenPlayAllButton;
        private static TMP_Text s_cachedPlayAllLabelForButton; // label cached per button instance
        private static readonly System.Text.StringBuilder s_overlaySb = new System.Text.StringBuilder(512);
        private static readonly List<OverlayRow> s_overlayRows = new List<OverlayRow>();
        private static Sprite s_manaOrbSprite;
        private static bool s_manaOrbSpriteGenerated;
        private static float s_nextManaOrbSpriteSearchAt;
        private static Texture2D s_generatedManaOrbTexture;
        private static long s_lastHandFingerprint; // skip sort when hand unchanged

        private const float OverlayRowHeight = 22f;
        private const int OverlaySortingOrder = 32767; // Unity Canvas max — renders above all game canvases

        private sealed class OverlayRow
        {
            public GameObject Root;
            public TMP_Text IndexText;
            public TMP_Text NameText;
            public TMP_Text TagText;
            public TMP_Text ManaText;
            public Image ManaIcon;
            public string IndexLabel; // cached so we don't reformat every tick
        }

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextVisualRefreshAt) return;
            s_nextVisualRefreshAt = now + VisualRefreshIntervalSeconds;

            if (DevLog.FullEnabled)
                DevLog.Info("Tick() -> TryResumePersistentAutoPlay()");
            TryResumePersistentAutoPlay();
            Refresh(s_lastPlayer);
            if (s_overlayOpen)
                UpdateOverlayContent();
        }

        public static void OnButtonPointerEnter(Selectable s)
        {
            if (s_orderButton != null && (object)s == (object)s_orderButton)
            { if (s_orderButtonImg != null) s_orderButtonImg.color = s_orderButtonHover; return; }
            if (s_refreshButton != null && (object)s == (object)s_refreshButton)
            { if (s_refreshButtonImg != null) s_refreshButtonImg.color = s_refreshButtonHover; }
        }

        public static void OnButtonPointerExit(Selectable s)
        {
            if (s_orderButton != null && (object)s == (object)s_orderButton)
            { if (s_orderButtonImg != null) s_orderButtonImg.color = s_orderButtonNormal; return; }
            if (s_refreshButton != null && (object)s == (object)s_refreshButton)
            { if (s_refreshButtonImg != null) s_refreshButtonImg.color = s_refreshButtonNormal; }
        }

        public static void SyncPersistentToggle(PlayerModel player)
        {
            if (player == null)
                return;

            s_persistentAutoPlayIntent = !s_persistentAutoPlayIntent;
            s_manualToggleCooldownUntil = Time.realtimeSinceStartup + 0.4f;
            DevLog.Info("SyncPersistentToggle: intent=" + s_persistentAutoPlayIntent + ", cooldownUntil=" + s_manualToggleCooldownUntil.ToString("F3"));

            // Immediately stop the running AutoPlayer when toggling off — don't wait for the next tick.
            if (!s_persistentAutoPlayIntent)
            {
                try { s_autoPlayerStopMethod?.Invoke(player.AutoPlayer, null); }
                catch { }
                DevLog.Info("SyncPersistentToggle: StopAutoPlay called immediately");
            }
        }

        public static bool IsOverlayOpen => s_overlayOpen;

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
            if (s_refreshButtonLabel != null && s_refreshButtonLabel.text != "Refresh")
                s_refreshButtonLabel.text = "Refresh";

            // Keep our cloned buttons always interactable — game components on the clone may disable them.
            if (s_orderButton != null)
                try { if (!s_orderButton.interactable) s_orderButton.interactable = true; } catch { }
            if (s_refreshButton != null)
                try { if (!s_refreshButton.interactable) s_refreshButton.interactable = true; } catch { }

            // Disable the Play All button while the order overlay is open.
            try
            {
                bool wantPlayAllInteractable = !s_overlayOpen;
                if (button.interactable != wantPlayAllInteractable)
                    button.interactable = wantPlayAllInteractable;
            }
            catch { }

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
                label.alignment = TextAlignmentOptions.Center;
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
                s_refreshButton = null;
                s_refreshButtonLabel = null;
                s_orderButtonImg = null;
                s_refreshButtonImg = null;
                s_overlayPanel = null;
                s_overlayContentRoot = null;
                s_overlayContentText = null;
                s_gameFont = null;
                s_overlayRows.Clear();
                s_overlayOpen = false;
                s_orderButtonCreated = false;
            }

            if (s_orderButtonCreated) return;
            s_orderButtonCreated = true;
            s_lastSeenPlayAllButton = playAllButton;

            try
            {
                var origRt = playAllButton.GetComponent<RectTransform>();
                float w = origRt != null ? origRt.sizeDelta.x : 128f;
                float h = origRt != null ? origRt.sizeDelta.y : 64f;

                // Clone the PlayAll button — inherits Image, ColorTint hover, SelectableAudio, etc.
                var orderGo = GameObject.Instantiate(playAllButton.gameObject);
                orderGo.name = "BAPOrderButton";
                orderGo.transform.SetParent(playAllButton.transform.parent, false);
                orderGo.transform.SetAsLastSibling();

                var rt = orderGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(w, h);
                rt.anchoredPosition = new Vector2(-(w + 6f), 0f);

                s_orderButton = orderGo.GetComponent<Button>();
                s_orderButton.onClick.RemoveAllListeners();
                s_orderButton.onClick.AddListener(new System.Action(ToggleOverlay));

                // Image is already identical to PlayAll's — cache for hover color reads if needed.
                s_orderButtonImg    = orderGo.GetComponent<Image>();
                var cb = s_orderButton.colors;
                s_orderButtonNormal = cb.normalColor * cb.colorMultiplier;
                s_orderButtonHover  = cb.highlightedColor * cb.colorMultiplier;

                // Update the cloned label text.
                s_orderButtonLabel = orderGo.GetComponentInChildren<TMP_Text>();
                if (s_orderButtonLabel != null)
                    s_orderButtonLabel.text = "Order";

                Color labelColor = s_orderButtonLabel != null ? s_orderButtonLabel.color : Color.white;
                CreateOverlayPanel(playAllButton, h, labelColor);
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource.LogWarning("EnsureOrderButton failed: " + ex.Message);
            }
        }

        // Walks up to the nearest Canvas ancestor so local-space positions stay correct.
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
            return start;
        }

        private static void CreateOverlayPanel(Button playAllButton, float buttonHeight, Color buttonLabelColor)
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

            // Lift to the Canvas ancestor so local-space positions stay correct.
            Transform topCanvas = FindCanvasTransform(buttonParent);
            panelGo.transform.SetParent(topCanvas, true);
            panelGo.transform.SetAsLastSibling();

            // Use max sorting order AND copy the highest sorting layer from any active canvas
            // so we render above health/player UI regardless of which layer they use.
            var panelCanvas = panelGo.AddComponent<Canvas>();
            panelCanvas.overrideSorting = true;
            int highestLayerId = 0;
            try
            {
                var allCanvases = Resources.FindObjectsOfTypeAll<Canvas>();
                int highestOrder = -1;
                for (int i = 0; i < allCanvases.Length; i++)
                {
                    var c = allCanvases[i];
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    if (c.sortingOrder > highestOrder)
                    {
                        highestOrder = c.sortingOrder;
                        highestLayerId = c.sortingLayerID;
                    }
                }
            }
            catch { }
            panelCanvas.sortingLayerID = highestLayerId;
            panelCanvas.sortingOrder = OverlaySortingOrder;
            panelGo.AddComponent<GraphicRaycaster>();

            // Try to copy the background panel style from the game's own UI (walk up from the button).
            var bg = panelGo.AddComponent<Image>();
            bool bgStyled = false;
            try
            {
                Transform walker = buttonParent;
                for (int depth = 0; depth < 6 && walker != null; depth++, walker = walker.parent)
                {
                    var walkerImg = walker.GetComponent<Image>();
                    if (walkerImg != null && walkerImg.sprite != null
                        && walkerImg.color.a > 0.3f
                        && !walkerImg.GetType().Name.Contains("Button"))
                    {
                        bg.sprite   = walkerImg.sprite;
                        bg.material = walkerImg.material;
                        bg.type     = walkerImg.type;
                        bg.color    = new Color(walkerImg.color.r * 0.55f, walkerImg.color.g * 0.55f, walkerImg.color.b * 0.55f, 0.96f);
                        bgStyled = true;
                        break;
                    }
                }
            }
            catch { }
            if (!bgStyled)
                bg.color = new Color(0.08f, 0.06f, 0.14f, 0.96f);

            // Grab font from PlayAll button label to match game style throughout.
            TMP_FontAsset gameFont = null;
            try { gameFont = playAllButton.GetComponentInChildren<TMP_Text>()?.font; } catch { }
            if (gameFont != null) s_gameFont = gameFont;

            // Title bar
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.offsetMin = new Vector2(14f, -44f);
            titleRt.offsetMax = new Vector2(-14f, -10f);
            var titleText = titleGo.AddComponent<TextMeshProUGUI>();
            if (gameFont != null) titleText.font = gameFont;
            titleText.text = "PLAY ORDER";
            titleText.fontSize = 15;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = new Color(0.9f, 0.82f, 0.55f, 1f); // warm gold — common header colour in RPG UIs
            titleText.characterSpacing = 3f;

            // Refresh button — clone PlayAll so hover/sound/style are inherited automatically
            var refreshGo = GameObject.Instantiate(playAllButton.gameObject);
            refreshGo.name = "BAPRefreshButton";
            refreshGo.transform.SetParent(panelGo.transform, false);
            var refreshRt = refreshGo.GetComponent<RectTransform>();
            refreshRt.anchorMin = new Vector2(1f, 1f);
            refreshRt.anchorMax = new Vector2(1f, 1f);
            refreshRt.pivot     = new Vector2(1f, 1f);
            refreshRt.sizeDelta = new Vector2(84f, 34f);
            refreshRt.anchoredPosition = new Vector2(-8f, -10f);

            s_refreshButton = refreshGo.GetComponent<Button>();
            s_refreshButton.onClick.RemoveAllListeners();
            s_refreshButton.onClick.AddListener(new System.Action(OnRefreshClicked));

            s_refreshButtonImg = refreshGo.GetComponent<Image>();
            var cb2 = s_refreshButton.colors;
            s_refreshButtonNormal = cb2.normalColor * cb2.colorMultiplier;
            s_refreshButtonHover  = cb2.highlightedColor * cb2.colorMultiplier;

            s_refreshButtonLabel = refreshGo.GetComponentInChildren<TMP_Text>();
            if (s_refreshButtonLabel != null)
            {
                s_refreshButtonLabel.text             = "Refresh";
                s_refreshButtonLabel.enableAutoSizing = false;
                s_refreshButtonLabel.fontSize         = 9f;
                s_refreshButtonLabel.alignment        = TextAlignmentOptions.Center;
            }

            // Divider line under title
            var divGo = new GameObject("Divider");
            divGo.transform.SetParent(panelGo.transform, false);
            var divRt = divGo.AddComponent<RectTransform>();
            divRt.anchorMin = new Vector2(0f, 1f);
            divRt.anchorMax = new Vector2(1f, 1f);
            divRt.pivot = new Vector2(0.5f, 1f);
            divRt.offsetMin = new Vector2(12f, -47f);
            divRt.offsetMax = new Vector2(-12f, -45f);
            var divImg = divGo.AddComponent<Image>();
            divImg.color = new Color(0.9f, 0.82f, 0.55f, 0.35f);

            // Sub-header: column labels — each at the same x offset as the actual data columns.
            // Row layout: Index x=0 w=28 | Name x=34 w=164 | Tag x=202 w=82 | Mana x=286 w=26 | Icon x=318
            // The content area is inset 14px from panel edges, so we mirror that here.
            var headerRowGo = new GameObject("ColHeaders");
            headerRowGo.transform.SetParent(panelGo.transform, false);
            var headerRowRt = headerRowGo.AddComponent<RectTransform>();
            headerRowRt.anchorMin = new Vector2(0f, 1f);
            headerRowRt.anchorMax = new Vector2(1f, 1f);
            headerRowRt.pivot = new Vector2(0f, 1f);
            headerRowRt.offsetMin = new Vector2(14f, -66f);
            headerRowRt.offsetMax = new Vector2(-14f, -49f);
            CreateHeaderCell(headerRowGo.transform, "#",    0f,   28f, TextAlignmentOptions.MidlineRight, gameFont);
            CreateHeaderCell(headerRowGo.transform, "Name", 34f, 164f, TextAlignmentOptions.MidlineLeft,  gameFont);
            CreateHeaderCell(headerRowGo.transform, "Role", 202f, 82f, TextAlignmentOptions.MidlineLeft,  gameFont);
            CreateHeaderCell(headerRowGo.transform, "Cost", 286f, 26f, TextAlignmentOptions.MidlineRight, gameFont);

            // Content area (rows start below header)
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(panelGo.transform, false);
            var contentRt = contentGo.AddComponent<RectTransform>();
            contentRt.anchorMin = Vector2.zero;
            contentRt.anchorMax = Vector2.one;
            contentRt.offsetMin = new Vector2(14f, 12f);
            contentRt.offsetMax = new Vector2(-14f, -68f);
            s_overlayContentRoot = contentGo;

            var emptyGo = new GameObject("EmptyText");
            emptyGo.transform.SetParent(contentGo.transform, false);
            var emptyRt = emptyGo.AddComponent<RectTransform>();
            emptyRt.anchorMin = Vector2.zero;
            emptyRt.anchorMax = Vector2.one;
            emptyRt.offsetMin = Vector2.zero;
            emptyRt.offsetMax = Vector2.zero;
            var contentText = emptyGo.AddComponent<TextMeshProUGUI>();
            if (gameFont != null) contentText.font = gameFont;
            contentText.fontSize = 13f;
            contentText.alignment = TextAlignmentOptions.TopLeft;
            contentText.color = new Color(0.7f, 0.7f, 0.7f, 1f);
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

        private static void OnRefreshClicked()
        {
            DevLog.Info("OnRefreshClicked: forcing hand fingerprint reset");
            s_lastHandFingerprint = 0;
            UpdateOverlayContent();
        }


        private static void TryLiveRefreshHand()
        {
            if (s_lastPlayer == null) return;
            try
            {
                List<CardModel> hand = null;

                // Direct typed access using public CardPileModel API: Count + TryPeekIndex.
                try
                {
                    HandPileModel handPile = s_lastPlayer.HandPile;
                    if (handPile != null)
                    {
                        CardPileModel cardPile = handPile.CardPile;
                        if (cardPile != null)
                        {
                            int count = cardPile.Count;
                            if (count > 0)
                            {
                                // Compute fingerprint from CardPile before allocating a List.
                                long fpEarly = count;
                                for (int i = 0; i < count; i++)
                                {
                                    CardModel card;
                                    if (cardPile.TryPeekIndex(i, out card) && card != null)
                                        try { fpEarly ^= card.Pointer.ToInt64() * (i + 1); } catch { }
                                }
                                if (fpEarly == s_lastHandFingerprint) return;

                                hand = new List<CardModel>(count);
                                for (int i = 0; i < count; i++)
                                {
                                    CardModel card;
                                    if (cardPile.TryPeekIndex(i, out card) && card != null)
                                        hand.Add(card);
                                }
                            }
                        }
                    }
                }
                catch { }

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
            if (s_overlayContentText == null || s_overlayContentRoot == null) return;

            TryLiveRefreshHand();
            SortOrderCache.RefreshAffordability();

            var entries = SortOrderCache.Entries;
            if (entries.Count == 0)
            {
                HideOverlayRows();
                s_overlayContentText.gameObject.SetActive(true);
                s_overlayContentText.text = "<color=#666666>No sort data yet.\nTrigger autoplay once to populate.</color>";
                return;
            }

            s_overlayContentText.gameObject.SetActive(false);
            EnsureOverlayRows(entries.Count);

            var manaSprite = GetManaOrbSprite();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var row = s_overlayRows[i];
                row.Root.SetActive(true);

                // Index — only update when row index label changes (it doesn't once rows exist)
                string idxStr = row.IndexLabel;
                if (idxStr == null)
                {
                    idxStr = (i + 1).ToString() + ".";
                    row.IndexLabel = idxStr;
                    row.IndexText.text = idxStr;
                    row.IndexText.color = s_overlayIndexColor;
                }

                // Name + color
                if (row.NameText.text != e.Name)
                    row.NameText.text = e.Name;
                Color nameColor = e.Played ? s_overlayPlayedColor : (e.CanAfford ? Color.white : s_overlayUnAffordColor);
                if (row.NameText.color != nameColor)
                    row.NameText.color = nameColor;

                // Tag
                string roleLabel = string.IsNullOrEmpty(e.RoleLabel) ? e.Role.ToString() : e.RoleLabel;
                string tagStr = "[" + roleLabel + "]";
                if (row.TagText.text != tagStr)
                    row.TagText.text = tagStr;
                Color tagColor = HtmlColor(string.IsNullOrEmpty(e.RoleColor) ? RoleColor(e.Role) : e.RoleColor, Color.gray);
                if (row.TagText.color != tagColor)
                    row.TagText.color = tagColor;

                // Mana
                string manaStr = e.ManaCost.ToString();
                if (row.ManaText.text != manaStr)
                    row.ManaText.text = manaStr;
                if (row.ManaText.color != s_overlayManaColor)
                    row.ManaText.color = s_overlayManaColor;

                row.ManaIcon.enabled = true;
                row.ManaIcon.sprite = manaSprite;
            }

            for (int i = entries.Count; i < s_overlayRows.Count; i++)
                s_overlayRows[i].Root.SetActive(false);
        }

        private static void HideOverlayRows()
        {
            for (int i = 0; i < s_overlayRows.Count; i++)
                if (s_overlayRows[i].Root != null)
                    s_overlayRows[i].Root.SetActive(false);
        }

        private static void EnsureOverlayRows(int count)
        {
            if (s_overlayContentRoot == null)
                return;

            while (s_overlayRows.Count < count)
                s_overlayRows.Add(CreateOverlayRow(s_overlayRows.Count));
        }

        private static OverlayRow CreateOverlayRow(int index)
        {
            var rowGo = new GameObject("Row_" + (index + 1).ToString());
            rowGo.transform.SetParent(s_overlayContentRoot.transform, false);

            var rt = rowGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -((index + 1) * OverlayRowHeight));
            rt.offsetMax = new Vector2(0f, -(index * OverlayRowHeight));

            var row = new OverlayRow();
            row.Root = rowGo;
            row.IndexText = CreateOverlayRowText(rowGo.transform, "Index", 0f, 28f, 12f, TextAlignmentOptions.MidlineRight);
            row.NameText = CreateOverlayRowText(rowGo.transform, "Name", 34f, 164f, 12f, TextAlignmentOptions.MidlineLeft);
            row.TagText = CreateOverlayRowText(rowGo.transform, "Tag", 202f, 82f, 11f, TextAlignmentOptions.MidlineLeft);
            row.ManaText = CreateOverlayRowText(rowGo.transform, "Mana", 286f, 26f, 12f, TextAlignmentOptions.MidlineRight);
            row.ManaIcon = CreateOverlayManaIcon(rowGo.transform, 318f);
            return row;
        }

        private static TMP_Text CreateOverlayRowText(Transform parent, string name, float x, float width, float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(x, 0f);
            rt.offsetMax = new Vector2(x + width, 0f);

            var text = go.AddComponent<TextMeshProUGUI>();
            if (s_gameFont != null) text.font = s_gameFont;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.margin = Vector4.zero;
            text.raycastTarget = false;
            return text;
        }

        private static void CreateHeaderCell(Transform parent, string label, float x, float width, TextAlignmentOptions alignment, TMP_FontAsset font)
        {
            var go = new GameObject("H_" + label);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(x, 0f);
            rt.offsetMax = new Vector2(x + width, 0f);
            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.text = label;
            text.fontSize = 10f;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            text.raycastTarget = false;
        }

        private static Image CreateOverlayManaIcon(Transform parent, float x)
        {
            var go = new GameObject("ManaOrb");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(15f, 15f);

            var image = go.AddComponent<Image>();
            image.sprite = GetManaOrbSprite();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        private static Color HtmlColor(string html, Color fallback)
        {
            Color parsed;
            return ColorUtility.TryParseHtmlString(html, out parsed) ? parsed : fallback;
        }

        private static Sprite GetManaOrbSprite()
        {
            if (s_manaOrbSprite != null && !s_manaOrbSpriteGenerated)
                return s_manaOrbSprite;

            if (s_manaOrbSprite == null || Time.realtimeSinceStartup >= s_nextManaOrbSpriteSearchAt)
            {
                s_nextManaOrbSpriteSearchAt = Time.realtimeSinceStartup + 2f;
                var gameSprite = TryFindGameManaOrbSprite();
                if (gameSprite != null)
                {
                    s_manaOrbSprite = gameSprite;
                    s_manaOrbSpriteGenerated = false;
                    return s_manaOrbSprite;
                }
            }

            if (s_manaOrbSprite == null)
            {
                s_manaOrbSprite = CreateGeneratedManaOrbSprite();
                s_manaOrbSpriteGenerated = true;
            }

            return s_manaOrbSprite;
        }

        private static Sprite TryFindGameManaOrbSprite()
        {
            try
            {
                var images = Resources.FindObjectsOfTypeAll<Image>();
                if (images == null)
                    return null;

                Sprite best = null;
                int bestScore = 0;
                for (int i = 0; i < images.Length; i++)
                {
                    var image = images[i];
                    if (image == null || image.sprite == null)
                        continue;

                    var sprite = image.sprite;
                    int score = ScoreManaSpriteCandidate(image, sprite);
                    if (score > bestScore)
                    {
                        best = sprite;
                        bestScore = score;
                    }
                }

                return bestScore >= 70 ? best : null;
            }
            catch
            {
                return null;
            }
        }

        private static int ScoreManaSpriteCandidate(Image image, Sprite sprite)
        {
            string text = SafeLower(image.name) + " " + SafeLower(sprite.name);
            try
            {
                var parent = image.transform.parent;
                if (parent != null)
                    text += " " + SafeLower(parent.name);
            }
            catch { }

            int score = 0;
            if (text.Contains("mana")) score += 60;
            if (text.Contains("cost")) score += 35;
            if (text.Contains("orb")) score += 35;
            if (text.Contains("blood")) score += 25;
            if (text.Contains("cardmanacost")) score += 45;
            if (text.Contains("button")) score -= 35;
            if (text.Contains("background")) score -= 20;
            if (text.Contains("cardimage")) score -= 25;

            try
            {
                var rect = sprite.rect;
                float width = rect.width;
                float height = rect.height;
                if (width > 0f && height > 0f)
                {
                    float aspect = width / height;
                    if (aspect > 0.75f && aspect < 1.33f)
                        score += 15;
                    if (width <= 128f && height <= 128f)
                        score += 10;
                }
            }
            catch { }

            return score;
        }

        private static string SafeLower(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.ToLowerInvariant();
        }

        private static Sprite CreateGeneratedManaOrbSprite()
        {
            const int size = 32;
            s_generatedManaOrbTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            s_generatedManaOrbTexture.name = "BAP_GeneratedManaOrb";
            s_generatedManaOrbTexture.wrapMode = TextureWrapMode.Clamp;
            s_generatedManaOrbTexture.filterMode = FilterMode.Bilinear;

            var pixels = new Color[size * size];
            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = size * 0.43f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var pos = new Vector2(x, y);
                    float distance = Vector2.Distance(pos, center);
                    float edge = Mathf.Clamp01(1f - (distance - radius + 2f) / 3f);
                    if (distance > radius + 2f)
                    {
                        pixels[y * size + x] = Color.clear;
                        continue;
                    }

                    float t = Mathf.Clamp01(distance / radius);
                    Color baseColor = Color.Lerp(new Color(0.72f, 0.96f, 1f, 1f), new Color(0.06f, 0.32f, 0.98f, 1f), t);
                    float highlight = Mathf.Clamp01(1f - Vector2.Distance(pos, new Vector2(11f, 22f)) / 8f);
                    Color color = Color.Lerp(baseColor, Color.white, highlight * 0.55f);
                    color.a = edge;
                    pixels[y * size + x] = color;
                }
            }

            s_generatedManaOrbTexture.SetPixels(pixels);
            s_generatedManaOrbTexture.Apply(false, true);
            return Sprite.Create(
                s_generatedManaOrbTexture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
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

    internal struct CardDisplayTag
    {
        public readonly string Label;
        public readonly string Color;

        public CardDisplayTag(string label, string color)
        {
            Label = label;
            Color = color;
        }
    }

    internal static class CardClassifier
    {
        // IL2CPP interop exposes native fields as properties, not C# fields — use AccessTools.Property.
        private static readonly PropertyInfo s_cardGroupProp =
            AccessTools.Property(typeof(CardConfig), "cardGroup");
        private static readonly PropertyInfo s_isWeaponProp =
            AccessTools.Property(typeof(CardGroup), "IsWeapon");
        private static readonly PropertyInfo s_onPlayEffectsProp =
            AccessTools.Property(typeof(CardConfig), "OnPlayEffects");
        private static readonly PropertyInfo s_cardTypesProp =
            AccessTools.Property(typeof(CardModel), "CardTypes");
        private static readonly PropertyInfo s_cardTypeProp =
            AccessTools.Property(typeof(CardConfig), "cardType");

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

        public static CardDisplayTag Describe(CardModel card, CardRole role)
        {
            try
            {
                var config = card?.CardConfig;
                CardDisplayTag configTypeTag;
                bool hasConfigType = TryGetConfigCardTypeTag(config, out configTypeTag);

                CardDisplayTag tag;
                if (config != null && TryGetEffectTag(config, role, hasConfigType ? configTypeTag.Color : null, out tag))
                    return tag;

                if (hasConfigType)
                    return configTypeTag;

                if (TryGetRuntimeCardTypeTag(card, out tag))
                    return tag;
            }
            catch { }

            return new CardDisplayTag(role.ToString(), FallbackRoleColor(role));
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

        private static bool TryGetConfigCardTypeTag(CardConfig config, out CardDisplayTag tag)
        {
            tag = default;
            if (config == null)
                return false;

            try
            {
                var cardType = s_cardTypeProp?.GetValue(config);
                return TryBuildSingleCardTypeTag(cardType, out tag);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetRuntimeCardTypeTag(CardModel card, out CardDisplayTag tag)
        {
            tag = default;
            if (card == null)
                return false;

            try
            {
                var cardTypes = s_cardTypesProp?.GetValue(card);
                if (TryBuildCardTypesTag(cardTypes, out tag))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool TryBuildCardTypesTag(object cardTypes, out CardDisplayTag tag)
        {
            tag = default;
            if (cardTypes == null)
                return false;

            try
            {
                PropertyInfo countProp;
                MethodInfo getItem;
                if (!ReflectionCache.TryGetListAccessors(cardTypes.GetType(), out countProp, out getItem))
                    return false;

                int count = Convert.ToInt32(countProp.GetValue(cardTypes));
                if (count <= 0)
                    return false;

                var label = new System.Text.StringBuilder(32);
                string color = null;
                int labels = 0;

                for (int i = 0; i < count; i++)
                {
                    var cardType = getItem.Invoke(cardTypes, new object[] { i });
                    CardDisplayTag single;
                    if (!TryBuildSingleCardTypeTag(cardType, out single))
                        continue;

                    if (labels > 0)
                        label.Append("/");
                    label.Append(single.Label);
                    if (string.IsNullOrEmpty(color))
                        color = single.Color;

                    labels++;
                    if (labels >= 2)
                        break;
                }

                if (labels == 0)
                    return false;

                if (count > labels)
                    label.Append("+");

                tag = new CardDisplayTag(label.ToString(), string.IsNullOrEmpty(color) ? "#777777" : color);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildSingleCardTypeTag(object cardType, out CardDisplayTag tag)
        {
            tag = default;
            if (cardType == null)
                return false;

            string label = SelectBestCardTypeLabel(cardType);
            if (string.IsNullOrEmpty(label))
                return false;

            string color = ReadColorMember(cardType, "typeColor");
            if (string.IsNullOrEmpty(color))
                color = "#777777";

            tag = new CardDisplayTag(label, color);
            return true;
        }

        private static bool TryGetEffectTag(CardConfig config, CardRole role, string preferredColor, out CardDisplayTag tag)
        {
            tag = default;
            try
            {
                if (config.GetType().Name == "FccConfig")
                {
                    tag = new CardDisplayTag("Character", string.IsNullOrEmpty(preferredColor) ? "#bb66ff" : preferredColor);
                    return true;
                }

                var effects = s_onPlayEffectsProp?.GetValue(config);
                if (effects == null)
                    return false;

                PropertyInfo countProp;
                MethodInfo getItem;
                if (!ReflectionCache.TryGetListAccessors(effects.GetType(), out countProp, out getItem))
                    return false;

                CardDisplayTag fallback = default;
                int count = Convert.ToInt32(countProp.GetValue(effects));
                for (int i = 0; i < count; i++)
                {
                    var effect = getItem.Invoke(effects, new object[] { i });
                    if (effect == null)
                        continue;

                    string name = effect.GetType().Name;
                    if (name == "GainManaEffect" || name == "GainManaEqualToCostEffect")
                    {
                        tag = new CardDisplayTag("Mana", string.IsNullOrEmpty(preferredColor) ? "#44aaff" : preferredColor);
                        return true;
                    }
                    if (name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        tag = new CardDisplayTag("Draw", string.IsNullOrEmpty(preferredColor) ? "#44dd88" : preferredColor);
                        return true;
                    }
                    if (name.IndexOf("Cost", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        tag = new CardDisplayTag("Cost", string.IsNullOrEmpty(preferredColor) ? "#66ccff" : preferredColor);
                        return true;
                    }
                    if (name.IndexOf("CreateCard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("CloneCard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        tag = new CardDisplayTag("Copy", string.IsNullOrEmpty(preferredColor) ? "#ffaa66" : preferredColor);
                        return true;
                    }

                    if (IsDamageEffectName(name))
                        fallback = new CardDisplayTag(role == CardRole.Attack ? "Attack" : "Damage", string.IsNullOrEmpty(preferredColor) ? "#ff4444" : preferredColor);
                    else if (name.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0)
                        fallback = new CardDisplayTag("Armor", string.IsNullOrEmpty(preferredColor) ? "#aaccff" : preferredColor);
                    else if (name.IndexOf("Wings", StringComparison.OrdinalIgnoreCase) >= 0)
                        fallback = new CardDisplayTag("Wings", string.IsNullOrEmpty(preferredColor) ? "#88ddff" : preferredColor);
                    else if (IsBuffEffectName(name))
                        fallback = new CardDisplayTag("Buff", string.IsNullOrEmpty(preferredColor) ? "#ffcc44" : preferredColor);
                }

                if (!string.IsNullOrEmpty(fallback.Label))
                {
                    tag = fallback;
                    return true;
                }

                if (role == CardRole.Attack)
                {
                    tag = new CardDisplayTag("Attack", string.IsNullOrEmpty(preferredColor) ? "#ff4444" : preferredColor);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsDamageEffectName(string name)
        {
            return name.IndexOf("Damage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Kill", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name == "BoneEffect" ||
                   name == "DemiEffect" ||
                   name == "PhieraggiEffect" ||
                   name == "SongOfManaEffect" ||
                   name == "ThunderLoopEffect" ||
                   name == "ValkyrieTurnerEffect" ||
                   name == "VandalierEffect" ||
                   name == "VentoSacroEffect";
        }

        private static bool IsBuffEffectName(string name)
        {
            return name.IndexOf("Might", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Growth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Luck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Magnet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Recovery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Area", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Duration", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Amount", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SelectBestCardTypeLabel(object cardType)
        {
            string label;
            if (TryUseCardTypeLabel(ReadStringMember(cardType, "Name"), out label))
                return label;
            if (TryUseCardTypeLabel(ReadStringMember(cardType, "typeId"), out label))
                return label;
            if (TryUseCardTypeLabel(ReadStringMember(cardType, "AssetId"), out label))
                return label;
            if (TryUseCardTypeLabel(ReadStringMember(cardType, "name"), out label))
                return label;

            return null;
        }

        private static bool TryUseCardTypeLabel(string raw, out string label)
        {
            label = NormalizeCardTypeLabel(raw);
            return !string.IsNullOrEmpty(label) && !IsColorOnlyLabel(label);
        }

        private static string NormalizeCardTypeLabel(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            string value = raw.Trim();
            int slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
            if (slash >= 0 && slash + 1 < value.Length)
                value = value.Substring(slash + 1);

            value = value.Replace("_", " ").Replace("-", " ");
            value = value.Replace("CardType", " ").Replace("cardtype", " ");
            value = value.Replace("Card Type", " ").Replace("card type", " ");
            value = value.Replace("Type", " ").Replace("type", " ");
            value = CollapseSpaces(value);
            if (string.IsNullOrEmpty(value))
                return null;

            string normalized = value.ToLowerInvariant();
            if (normalized == "defence" || normalized == "defense")
                return "Armor";
            if (normalized == "attack")
                return "Attack";
            if (normalized == "armor")
                return "Armor";
            if (normalized == "mana")
                return "Mana";
            if (normalized == "special")
                return "Special";
            if (normalized == "character")
                return "Character";
            if (normalized == "debuff")
                return "Debuff";
            if (normalized == "void")
                return "Void";

            return TitleCaseWords(value);
        }

        private static string CollapseSpaces(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var sb = new System.Text.StringBuilder(value.Length);
            bool previousSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isSpace = char.IsWhiteSpace(c);
                if (isSpace)
                {
                    if (!previousSpace)
                        sb.Append(' ');
                    previousSpace = true;
                }
                else
                {
                    sb.Append(c);
                    previousSpace = false;
                }
            }

            return sb.ToString().Trim();
        }

        private static string TitleCaseWords(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var sb = new System.Text.StringBuilder(value.Length);
            bool newWord = true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                    newWord = true;
                    continue;
                }

                sb.Append(newWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                newWord = false;
            }

            return sb.ToString();
        }

        private static bool IsColorOnlyLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return false;

            string normalized = label.Trim().ToLowerInvariant();
            return normalized == "red" ||
                   normalized == "blue" ||
                   normalized == "green" ||
                   normalized == "yellow" ||
                   normalized == "purple" ||
                   normalized == "orange" ||
                   normalized == "pink" ||
                   normalized == "black" ||
                   normalized == "white" ||
                   normalized == "gray" ||
                   normalized == "grey" ||
                   normalized == "cyan" ||
                   normalized == "magenta" ||
                   normalized == "violet" ||
                   normalized == "brown" ||
                   normalized == "gold" ||
                   normalized == "silver";
        }

        private static string ReadStringMember(object target, string memberName)
        {
            object value;
            if (!TryReadMember(target, memberName, out value) || value == null)
                return null;
            return value.ToString();
        }

        private static string ReadColorMember(object target, string memberName)
        {
            object value;
            if (!TryReadMember(target, memberName, out value))
                return null;
            if (!(value is Color))
                return null;
            return ColorToHex((Color)value);
        }

        private static bool TryReadMember(object target, string memberName, out object value)
        {
            value = null;
            if (target == null)
                return false;

            try
            {
                Type type = target.GetType();
                var prop = type.GetProperty(memberName);
                if (prop != null)
                {
                    value = prop.GetValue(target);
                    return true;
                }

                var field = type.GetField(memberName);
                if (field != null)
                {
                    value = field.GetValue(target);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static string FallbackRoleColor(CardRole role)
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

        private static string ColorToHex(Color color)
        {
            int r = ColorByte(color.r);
            int g = ColorByte(color.g);
            int b = ColorByte(color.b);
            return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        private static int ColorByte(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;
            return (int)Math.Round(value * 255f);
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
        private static bool Prefix()
        {
            // Block the button entirely while the order overlay is open.
            if (AutoPlayUiController.IsOverlayOpen)
                return false;
            return true;
        }

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
            public string RoleLabel;
            public string RoleColor;
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
                var display = CardClassifier.Describe(card, role);
                int mana = GetCurrentManaCost(card);
                bool canAfford = true;
                try { if (player != null) canAfford = player.CanAffordCard(card); } catch { }
                long ptr = 0;
                try { ptr = card.Pointer.ToInt64(); } catch { }
                bool played = s_playedPtrs.Contains(ptr);
                Entries.Add(new CardEntry
                {
                    Name = card.Name ?? "?",
                    Role = role,
                    RoleLabel = display.Label,
                    RoleColor = display.Color,
                    ManaCost = mana,
                    CanAfford = canAfford,
                    Played = played
                });
            }
        }

        public static void RefreshAffordability()
        {
            for (int i = 0; i < s_cardRefs.Count && i < Entries.Count; i++)
            {
                var card = s_cardRefs[i];
                if (card == null) continue;
                var e = Entries[i];
                if (s_player != null)
                {
                    try { e.CanAfford = s_player.CanAffordCard(card); } catch { }
                }
                e.ManaCost = GetCurrentManaCost(card);
                var display = CardClassifier.Describe(card, e.Role);
                e.RoleLabel = display.Label;
                e.RoleColor = display.Color;
                try { e.Played = s_playedPtrs.Contains(card.Pointer.ToInt64()); } catch { }
                Entries[i] = e;
            }
        }

        private static int GetCurrentManaCost(CardModel card)
        {
            if (card == null) return 0;
            try { return card.GetCardCostTypeManaCost(false); }
            catch { return 0; }
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
        private const int MaxComboSearchDepth = 8;
        private const int ComboSearchBeamWidth = 50;

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
            var sortable = new List<CardModel>(input.Count);
            var cooldown = new List<CardModel>(input.Count);
            foreach (var card in input)
            {
                if (card == null || AutoPlayRetryCooldown.IsCoolingDown(card))
                    cooldown.Add(card);
                else
                    sortable.Add(card);
            }
            DevLog.Info("Sort: input=" + input.Count + ", sortable=" + sortable.Count + ", cooldown=" + cooldown.Count);

            var ordered = SortSubset(player, sortable, BuildContext(player, sortable));

            if (cooldown.Count > 0)
            {
                var cooldownOrdered = SortSubset(player, cooldown, BuildContext(player, cooldown));
                ordered.AddRange(cooldownOrdered);
            }
            return ordered;
        }

        private static List<CardModel> SortSubset(PlayerModel player, List<CardModel> subset, AutoPlaySortContext context)
        {
            var ordered = BeamSearchOrder(player, subset, context);
            DevLog.Info("SortSubset: beam search ordered=" + ordered.Count);
            return ordered;
        }

        private static AutoPlaySortContext BuildContext(PlayerModel player, List<CardModel> subset)
        {
            var originalIndex = BuildOriginalIndex(subset);
            var roles = BuildRoles(subset);
            var manaGains = BuildManaGains(subset, roles);
            var drawScores = BuildDrawScores(subset);
            var manaCosts = BuildManaCosts(subset);
            var comboCosts = BuildComboCosts(subset);
            var evolved = BuildEvolvedFlags(subset);
            var inComboNow = BuildInComboFlags(player, subset);
            return new AutoPlaySortContext(originalIndex, roles, manaGains, drawScores, manaCosts, comboCosts, evolved, inComboNow);
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

        private static Dictionary<CardModel, int> BuildDrawScores(List<CardModel> input)
        {
            var draws = new Dictionary<CardModel, int>(input.Count);
            foreach (var card in input)
            {
                if (card == null || draws.ContainsKey(card))
                    continue;

                int draw = ComputeDrawScore(card);
                draws[card] = draw;
                if (draw > 0)
                    DevLog.Info("CardFacts: drawScore " + SafeName(card) + " = " + draw);
            }
            return draws;
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

        private static int ComputeDrawScore(CardModel card)
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

                int score = 0;
                int count = Convert.ToInt32(countProp.GetValue(effects));
                for (int i = 0; i < count; i++)
                {
                    var effect = getItem.Invoke(effects, new object[] { i });
                    if (effect == null) continue;

                    string name = effect.GetType().Name;
                    if (name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("CardDraw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("AddCard", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 1;
                    }
                }
                return score;
            }
            catch { return 0; }
        }

        private static List<CardModel> BeamSearchOrder(PlayerModel player, List<CardModel> cards, AutoPlaySortContext context)
        {
            var startRemaining = new List<CardModel>(cards);
            var beam = new List<BeamState>();
            beam.Add(new BeamState(new List<CardModel>(), startRemaining, EvaluateBeamScore(player, new List<CardModel>(), startRemaining, context)));

            int targetCount = startRemaining.Count;
            for (int depth = 0; depth < targetCount; depth++)
            {
                var expanded = new List<BeamState>();
                foreach (var state in beam)
                {
                    var expansionCandidates = GetBeamExpansionCandidates(state.Played, state.Remaining, context);
                    foreach (var card in expansionCandidates)
                    {
                        if (card == null)
                            continue;

                        var nextPlayed = new List<CardModel>(state.Played);
                        nextPlayed.Add(card);

                        var nextRemaining = new List<CardModel>(state.Remaining);
                        nextRemaining.Remove(card);

                        expanded.Add(new BeamState(
                            nextPlayed,
                            nextRemaining,
                            EvaluateBeamScore(player, nextPlayed, nextRemaining, context)));
                    }
                }

                if (expanded.Count == 0)
                    break;

                expanded.Sort(delegate(BeamState left, BeamState right)
                {
                    return BeamScore.Compare(right.Score, left.Score);
                });

                int keep = Math.Min(expanded.Count, ComboSearchBeamWidth);
                beam.Clear();
                for (int i = 0; i < keep; i++)
                    beam.Add(expanded[i]);
            }

            if (beam.Count == 0)
                return new List<CardModel>(cards);

            beam.Sort(delegate(BeamState left, BeamState right)
            {
                return BeamScore.Compare(right.Score, left.Score);
            });

            var best = beam[0];
            var ordered = new List<CardModel>(best.Played);
            if (best.Remaining.Count > 0)
            {
                best.Remaining.Sort(delegate(CardModel left, CardModel right)
                {
                    return CompareBeamExpansionCandidate(left, right, best.Played.Count > 0 ? best.Played[best.Played.Count - 1] : null, best.Remaining, player, context);
                });
                ordered.AddRange(best.Remaining);
            }

            return NormalizeManaLadderOrder(ordered, context);
        }

        private static List<CardModel> NormalizeManaLadderOrder(List<CardModel> source, AutoPlaySortContext context)
        {
            var remaining = new List<CardModel>(source);
            var ordered = new List<CardModel>(source.Count);

            while (remaining.Count > 0)
            {
                int lowestMana = GetLowestMana(remaining, context);
                if (lowestMana == int.MaxValue)
                {
                    ordered.AddRange(remaining);
                    break;
                }

                CardModel current = PickBestCardAtMana(remaining, lowestMana, null, context);
                if (current == null)
                    break;

                ordered.Add(current);
                remaining.Remove(current);

                while (remaining.Count > 0)
                {
                    int nextMana = GetNextHigherMana(remaining, SafeMana(GetManaCost(current, context)), context);
                    if (nextMana == int.MaxValue)
                        break;

                    CardModel next = PickBestCardAtMana(remaining, nextMana, current, context);
                    if (next == null)
                        break;

                    ordered.Add(next);
                    remaining.Remove(next);
                    current = next;
                }
            }

            return ordered;
        }

        private static int GetLowestMana(List<CardModel> cards, AutoPlaySortContext context)
        {
            int result = int.MaxValue;
            foreach (var card in cards)
            {
                if (card == null)
                    continue;
                int mana = SafeMana(GetManaCost(card, context));
                if (mana < result)
                    result = mana;
            }
            return result;
        }

        private static int GetNextHigherMana(List<CardModel> cards, int currentMana, AutoPlaySortContext context)
        {
            int result = int.MaxValue;
            foreach (var card in cards)
            {
                if (card == null)
                    continue;
                int mana = SafeMana(GetManaCost(card, context));
                if (mana > currentMana && mana < result)
                    result = mana;
            }
            return result;
        }

        private static CardModel PickBestCardAtMana(List<CardModel> cards, int mana, CardModel previous, AutoPlaySortContext context)
        {
            CardModel best = null;
            foreach (var card in cards)
            {
                if (card == null || SafeMana(GetManaCost(card, context)) != mana)
                    continue;

                if (best == null || CompareManaStepCandidate(card, best, previous, cards, context) < 0)
                    best = card;
            }
            return best;
        }

        private static int CompareManaStepCandidate(CardModel left, CardModel right, CardModel previous, List<CardModel> available, AutoPlaySortContext context)
        {
            if (left == right) return 0;
            if (left == null) return 1;
            if (right == null) return -1;

            if (previous != null)
            {
                bool leftCombo = CanComboAfter(left, previous);
                bool rightCombo = CanComboAfter(right, previous);
                if (leftCombo != rightCombo)
                    return leftCombo ? -1 : 1;
            }

            int r = CountHigherManaFollowers(right, available, context).CompareTo(CountHigherManaFollowers(left, available, context));
            if (r != 0) return r;

            return CompareCardPreference(left, right, context);
        }

        private static int CountHigherManaFollowers(CardModel card, List<CardModel> cards, AutoPlaySortContext context)
        {
            if (card == null || cards == null)
                return 0;

            int mana = SafeMana(GetManaCost(card, context));
            int count = 0;
            foreach (var other in cards)
            {
                if (other == null || ReferenceEquals(other, card))
                    continue;
                if (SafeMana(GetManaCost(other, context)) > mana)
                    count++;
            }
            return count;
        }

        private static List<CardModel> GetBeamExpansionCandidates(List<CardModel> played, List<CardModel> remaining, AutoPlaySortContext context)
        {
            var candidates = new List<CardModel>();
            if (remaining == null || remaining.Count == 0)
                return candidates;

            CardModel previous = played != null && played.Count > 0 ? played[played.Count - 1] : null;
            if (previous == null)
            {
                candidates.AddRange(remaining);
                return candidates;
            }

            int previousMana = SafeMana(GetManaCost(previous, context));
            bool hasHigherCombo = false;
            foreach (var card in remaining)
            {
                if (card == null)
                    continue;
                if (SafeMana(GetManaCost(card, context)) > previousMana && CanComboAfter(card, previous))
                {
                    hasHigherCombo = true;
                    break;
                }
            }

            if (!hasHigherCombo)
            {
                candidates.AddRange(remaining);
                return candidates;
            }

            foreach (var card in remaining)
            {
                if (card == null)
                    continue;

                bool isHigherCombo = SafeMana(GetManaCost(card, context)) > previousMana && CanComboAfter(card, previous);
                if (isHigherCombo || IsCriticalUtilityCard(card, context))
                    candidates.Add(card);
            }

            return candidates;
        }

        private static BeamScore EvaluateBeamScore(PlayerModel player, List<CardModel> played, List<CardModel> remaining, AutoPlaySortContext context)
        {
            var score = new BeamScore();
            score.PlayedCount = played.Count;
            score.FirstOriginalIndex = played.Count > 0 ? GetOriginalIndex(context, played[0]) : int.MaxValue;
            score.StartMana = played.Count > 0 ? SafeMana(GetManaCost(played[0], context)) : int.MaxValue;

            CardModel previous = null;
            bool brokeCombo = false;
            for (int i = 0; i < played.Count; i++)
            {
                var card = played[i];
                if (card == null)
                    continue;

                int mana = SafeMana(GetManaCost(card, context));
                int positionWeight = played.Count - i;
                int utility = GetCardUtilityScore(card, context);
                var availableAtStep = BuildAvailableAtStep(played, remaining, i);
                score.CardUtility += utility * (positionWeight + 1);
                score.RoleScore += GetRoleWeight(GetRole(card, context)) * positionWeight;
                score.ManaOrderScore += Math.Max(0, 30 - mana) * positionWeight * 90;
                if (ShouldPreferUtilityForSameManaNoClimb(previous, card, availableAtStep, context))
                    score.SameManaPenalty += 2200;

                if (i == 0)
                {
                    if (IsCurrentlyInCombo(player, card, context))
                        score.ComboScore += 3000;
                    score.LowStartScore += Math.Max(0, 50 - mana) * 120;
                }
                else if (CanComboAfter(card, previous))
                {
                    int previousMana = SafeMana(GetManaCost(previous, context));
                    int delta = mana - previousMana;
                    if (delta > 0)
                    {
                        score.ComboScore += 7000 + (delta == 1 ? 1400 : 300) + Math.Max(0, mana) * 40;
                        score.ManaFlowScore += 200 + Math.Max(0, mana);
                    }
                    else if (delta == 0)
                    {
                        score.ComboScore += 450;
                        score.SameManaPenalty += 900;
                        if (HasHigherComboFollower(previous, availableAtStep, context))
                            score.SameManaPenalty += 9000;
                        score.ManaFlowScore += 80;
                    }
                    else
                    {
                        score.ComboScore += 300 - Math.Min(250, Math.Abs(delta) * 50);
                        score.ManaFlowScore -= Math.Min(300, Math.Abs(delta) * 60);
                    }
                }
                else
                {
                    brokeCombo = true;
                    score.ComboBreakPenalty += Math.Max(0, 2200 - utility);
                    score.ManaFlowScore -= 250;
                }

                previous = card;
            }

            CardModel last = played.Count > 0 ? played[played.Count - 1] : null;
            score.FuturePotential = GetFuturePotentialScore(last, remaining, context);
            if (!brokeCombo && played.Count > 1)
                score.ComboScore += 600;

            return score;
        }

        private static List<CardModel> BuildAvailableAtStep(List<CardModel> played, List<CardModel> remaining, int playedIndex)
        {
            var available = new List<CardModel>();
            for (int i = playedIndex; i < played.Count; i++)
            {
                if (played[i] != null)
                    available.Add(played[i]);
            }
            if (remaining != null)
            {
                foreach (var card in remaining)
                {
                    if (card != null)
                        available.Add(card);
                }
            }
            return available;
        }

        private static bool ShouldPreferUtilityForSameManaNoClimb(CardModel previous, CardModel selected, List<CardModel> available, AutoPlaySortContext context)
        {
            if (selected == null || GetRole(selected, context) != CardRole.Attack)
                return false;

            int selectedMana = SafeMana(GetManaCost(selected, context));
            if (HasHigherComboFollower(selected, available, context))
                return false;

            foreach (var card in available)
            {
                if (card == null || ReferenceEquals(card, selected))
                    continue;
                if (GetRole(card, context) != CardRole.Utility)
                    continue;
                if (SafeMana(GetManaCost(card, context)) != selectedMana)
                    continue;
                if (previous != null && !CanComboAfter(card, previous))
                    continue;
                if (previous == null && !IsComparableStartCandidate(card, selected, context))
                    continue;
                if (!HasHigherComboFollower(card, available, context))
                    return true;
            }

            return false;
        }

        private static bool IsComparableStartCandidate(CardModel left, CardModel right, AutoPlaySortContext context)
        {
            return left != null
                && right != null
                && SafeMana(GetManaCost(left, context)) == SafeMana(GetManaCost(right, context));
        }

        private static int GetCardUtilityScore(CardModel card, AutoPlaySortContext context)
        {
            if (card == null)
                return 0;

            int mana = SafeMana(GetManaCost(card, context));
            CardRole role = GetRole(card, context);
            int score = 0;

            score += GetManaGain(card, context) * 650;
            score += GetDrawScore(card, context) * 700;

            switch (role)
            {
                case CardRole.ManaGenerator:
                    score += 900;
                    break;
                case CardRole.Utility:
                    score += 120;
                    break;
                case CardRole.Crawler:
                    score += 120;
                    break;
                case CardRole.Attack:
                    score += 120 + Math.Max(0, mana) * 60;
                    break;
                default:
                    score += 80;
                    break;
            }

            if (IsEvolved(card, context))
                score += 180;

            score += Math.Max(0, 12 - mana) * 15;
            return score;
        }

        private static bool IsCriticalUtilityCard(CardModel card, AutoPlaySortContext context)
        {
            if (card == null)
                return false;

            return GetManaGain(card, context) > 0 || GetDrawScore(card, context) > 0;
        }

        private static int GetFuturePotentialScore(CardModel last, List<CardModel> remaining, AutoPlaySortContext context)
        {
            if (remaining == null || remaining.Count == 0)
                return 0;

            int score = 0;
            if (last != null)
            {
                foreach (var card in remaining)
                {
                    if (card == null)
                        continue;

                    if (CanComboAfter(card, last))
                    {
                        int lastMana = SafeMana(GetManaCost(last, context));
                        int mana = SafeMana(GetManaCost(card, context));
                        score += 600;
                        if (mana > lastMana)
                            score += 250 + (mana - lastMana == 1 ? 250 : 0);
                        else if (mana == lastMana)
                            score += 100;
                    }
                }
            }

            int lowestMana = int.MaxValue;
            foreach (var card in remaining)
            {
                if (card == null)
                    continue;
                lowestMana = Math.Min(lowestMana, SafeMana(GetManaCost(card, context)));
                score += Math.Min(1200, GetCardUtilityScore(card, context) / 8);
            }

            if (lowestMana != int.MaxValue)
                score += Math.Max(0, 50 - lowestMana) * 5;

            return score;
        }

        private static int CompareBeamExpansionCandidate(CardModel left, CardModel right, CardModel previous, List<CardModel> remaining, PlayerModel player, AutoPlaySortContext context)
        {
            if (left == right) return 0;
            if (left == null) return 1;
            if (right == null) return -1;

            int r;
            if (previous != null)
            {
                bool leftCombo = CanComboAfter(left, previous);
                bool rightCombo = CanComboAfter(right, previous);
                if (leftCombo != rightCombo)
                    return leftCombo ? -1 : 1;

                int leftMana = SafeMana(GetManaCost(left, context));
                int rightMana = SafeMana(GetManaCost(right, context));
                int previousMana = SafeMana(GetManaCost(previous, context));
                bool leftIncreases = leftMana > previousMana;
                bool rightIncreases = rightMana > previousMana;
                if (leftIncreases != rightIncreases)
                    return leftIncreases ? -1 : 1;
                if (leftIncreases && rightIncreases)
                {
                    int rIncrease = leftMana.CompareTo(rightMana);
                    if (rIncrease != 0) return rIncrease;
                }

                if (leftCombo && rightCombo && leftMana == rightMana)
                {
                    bool leftUtility = GetRole(left, context) == CardRole.Utility;
                    bool rightUtility = GetRole(right, context) == CardRole.Utility;
                    bool leftAttack = GetRole(left, context) == CardRole.Attack;
                    bool rightAttack = GetRole(right, context) == CardRole.Attack;
                    bool leftCanClimb = HasHigherComboFollower(left, remaining, context);
                    bool rightCanClimb = HasHigherComboFollower(right, remaining, context);
                    if (!leftCanClimb && !rightCanClimb)
                    {
                        if (leftUtility && rightAttack) return -1;
                        if (leftAttack && rightUtility) return 1;
                    }
                }
            }
            else
            {
                bool leftInCombo = IsCurrentlyInCombo(player, left, context);
                bool rightInCombo = IsCurrentlyInCombo(player, right, context);
                if (leftInCombo != rightInCombo)
                    return leftInCombo ? -1 : 1;
            }

            r = GetCardUtilityScore(right, context).CompareTo(GetCardUtilityScore(left, context));
            if (r != 0) return r;

            r = GetFuturePotentialScore(right, remaining, context).CompareTo(GetFuturePotentialScore(left, remaining, context));
            if (r != 0) return r;

            return CompareCardPreference(left, right, context);
        }

        private static bool HasHigherComboFollower(CardModel card, List<CardModel> cards, AutoPlaySortContext context)
        {
            if (card == null || cards == null)
                return false;

            int mana = SafeMana(GetManaCost(card, context));
            foreach (var other in cards)
            {
                if (other == null || ReferenceEquals(other, card))
                    continue;
                if (SafeMana(GetManaCost(other, context)) > mana && CanComboAfter(other, card))
                    return true;
            }

            return false;
        }

        private sealed class BeamState
        {
            public BeamState(List<CardModel> played, List<CardModel> remaining, BeamScore score)
            {
                Played = played;
                Remaining = remaining;
                Score = score;
            }

            public List<CardModel> Played { get; }
            public List<CardModel> Remaining { get; }
            public BeamScore Score { get; }
        }

        private sealed class BeamScore
        {
            public int PlayedCount;
            public int ComboScore;
            public int CardUtility;
            public int FuturePotential;
            public int ComboBreakPenalty;
            public int SameManaPenalty;
            public int ManaFlowScore;
            public int ManaOrderScore;
            public int LowStartScore;
            public int RoleScore;
            public int StartMana;
            public int FirstOriginalIndex;

            public static int Compare(BeamScore left, BeamScore right)
            {
                long leftTotal = left.Total;
                long rightTotal = right.Total;
                int r = leftTotal.CompareTo(rightTotal);
                if (r != 0) return r;
                r = left.PlayedCount.CompareTo(right.PlayedCount);
                if (r != 0) return r;
                r = right.StartMana.CompareTo(left.StartMana);
                if (r != 0) return r;
                return right.FirstOriginalIndex.CompareTo(left.FirstOriginalIndex);
            }

            public long Total
            {
                get
                {
                    return ComboScore
                        + CardUtility
                        + FuturePotential
                        + ManaFlowScore
                        + ManaOrderScore
                        + LowStartScore
                        - ComboBreakPenalty
                        - SameManaPenalty;
                }
            }
        }

        private static List<CardModel> BuildManaFirstPath(PlayerModel player, List<CardModel> cards, AutoPlaySortContext context)
        {
            CardModel start = PickLowestManaStart(player, cards, context);
            if (start == null)
                return null;

            return BuildAscendingComboPreview(start, cards, context);
        }

        private static CardModel PickLowestManaStart(PlayerModel player, List<CardModel> cards, AutoPlaySortContext context)
        {
            int lowestMana = int.MaxValue;
            foreach (var card in cards)
            {
                if (card == null)
                    continue;

                int mana = SafeMana(GetManaCost(card, context));
                if (mana < lowestMana)
                    lowestMana = mana;
            }

            CardModel best = null;
            foreach (var card in cards)
            {
                if (card == null || SafeMana(GetManaCost(card, context)) != lowestMana)
                    continue;

                best = BetterLowestManaStart(best, card, cards, player, context);
            }

            return best;
        }

        private static CardModel BetterLowestManaStart(CardModel current, CardModel candidate, List<CardModel> cards, PlayerModel player, AutoPlaySortContext context)
        {
            if (current == null)
                return candidate;

            var currentPath = BuildAscendingComboPreview(current, cards, context);
            var candidatePath = BuildAscendingComboPreview(candidate, cards, context);

            int r = candidatePath.Count.CompareTo(currentPath.Count);
            if (r > 0) return candidate;
            if (r < 0) return current;

            r = GetPathPeakMana(candidatePath, context).CompareTo(GetPathPeakMana(currentPath, context));
            if (r > 0) return candidate;
            if (r < 0) return current;

            bool currentInCombo = IsCurrentlyInCombo(player, current, context);
            bool candidateInCombo = IsCurrentlyInCombo(player, candidate, context);
            if (currentInCombo != candidateInCombo)
                return candidateInCombo ? candidate : current;

            return CompareCardPreference(candidate, current, context) < 0 ? candidate : current;
        }

        private static List<CardModel> BuildAscendingComboPreview(CardModel start, List<CardModel> cards, AutoPlaySortContext context)
        {
            var path = new List<CardModel>();
            if (start == null)
                return path;

            var remaining = new List<CardModel>(cards);
            remaining.Remove(start);
            path.Add(start);

            CardModel previous = start;
            while (remaining.Count > 0 && path.Count < MaxComboSearchDepth)
            {
                CardModel next = PickAscendingComboNext(previous, remaining, context);
                if (next == null)
                    break;

                path.Add(next);
                remaining.Remove(next);
                previous = next;
            }

            return path;
        }

        private static CardModel PickAscendingComboNext(CardModel previous, List<CardModel> remaining, AutoPlaySortContext context)
        {
            if (previous == null)
                return null;

            int previousMana = SafeMana(GetManaCost(previous, context));
            CardModel bestIncrease = null;
            int bestIncreaseMana = int.MaxValue;
            CardModel bestSame = null;

            foreach (var card in remaining)
            {
                if (card == null || !CanComboAfter(card, previous))
                    continue;

                int mana = SafeMana(GetManaCost(card, context));
                if (mana > previousMana)
                {
                    if (mana < bestIncreaseMana)
                    {
                        bestIncrease = card;
                        bestIncreaseMana = mana;
                    }
                    else if (mana == bestIncreaseMana)
                    {
                        bestIncrease = BetterSameManaComboChoice(bestIncrease, card, remaining, context);
                    }
                }
                else if (mana == previousMana)
                {
                    bestSame = BetterSameManaComboChoice(bestSame, card, remaining, context);
                }
            }

            return bestIncrease ?? bestSame;
        }

        private static CardModel BetterSameManaComboChoice(CardModel current, CardModel candidate, List<CardModel> cards, AutoPlaySortContext context)
        {
            if (current == null)
                return candidate;

            int r = CountAscendingComboFollowers(candidate, cards, context).CompareTo(CountAscendingComboFollowers(current, cards, context));
            if (r > 0) return candidate;
            if (r < 0) return current;

            return CompareCardPreference(candidate, current, context) < 0 ? candidate : current;
        }

        private static int CompareCardPreference(CardModel left, CardModel right, AutoPlaySortContext context)
        {
            int r = GetManaCost(left, context).CompareTo(GetManaCost(right, context));
            if (r != 0) return r;

            r = GetManaGain(right, context).CompareTo(GetManaGain(left, context));
            if (r != 0) return r;

            r = GetDrawScore(right, context).CompareTo(GetDrawScore(left, context));
            if (r != 0) return r;

            bool le = IsEvolved(left, context);
            bool re2 = IsEvolved(right, context);
            if (le && !re2) return -1;
            if (!le && re2) return 1;

            CardRole leftRole = GetRole(left, context);
            CardRole rightRole = GetRole(right, context);
            r = leftRole.CompareTo(rightRole);
            if (r != 0) return r;

            r = GetComboCost(left, context).CompareTo(GetComboCost(right, context));
            if (r != 0) return r;

            return GetOriginalIndex(context, left).CompareTo(GetOriginalIndex(context, right));
        }

        private static int CountAscendingComboFollowers(CardModel card, List<CardModel> cards, AutoPlaySortContext context)
        {
            if (card == null || cards == null) return 0;

            int count = 0;
            int mana = SafeMana(GetManaCost(card, context));
            foreach (var other in cards)
            {
                if (other == null || ReferenceEquals(other, card))
                    continue;
                if (SafeMana(GetManaCost(other, context)) >= mana && CanComboAfter(other, card))
                    count++;
            }
            return count;
        }

        private static int GetPathPeakMana(List<CardModel> path, AutoPlaySortContext context)
        {
            if (path == null || path.Count == 0)
                return int.MinValue;

            int peak = int.MinValue;
            foreach (var card in path)
            {
                int mana = SafeMana(GetManaCost(card, context));
                if (mana > peak)
                    peak = mana;
            }

            return peak;
        }

        private static int SafeMana(int mana)
        {
            return mana == int.MaxValue ? 99 : mana;
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

        private static int GetDrawScore(CardModel card, AutoPlaySortContext context)
        {
            int draw;
            return card != null && context.DrawScores.TryGetValue(card, out draw) ? draw : 0;
        }

        private static int GetRoleWeight(CardRole role)
        {
            switch (role)
            {
                case CardRole.ManaGenerator: return 45;
                case CardRole.Utility:       return 34;
                case CardRole.Crawler:       return 26;
                case CardRole.Attack:        return 22;
                default:                     return 0;
            }
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
            Dictionary<CardModel, int> drawScores,
            Dictionary<CardModel, int> manaCosts,
            Dictionary<CardModel, int> comboCosts,
            Dictionary<CardModel, bool> evolved,
            Dictionary<CardModel, bool> inComboNow)
        {
            OriginalIndex = originalIndex;
            Roles = roles;
            ManaGains = manaGains;
            DrawScores = drawScores;
            ManaCosts = manaCosts;
            ComboCosts = comboCosts;
            Evolved = evolved;
            InComboNow = inComboNow;
        }

        public Dictionary<CardModel, int> OriginalIndex { get; }
        public Dictionary<CardModel, CardRole> Roles { get; }
        public Dictionary<CardModel, int> ManaGains { get; }
        public Dictionary<CardModel, int> DrawScores { get; }
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
