using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nosebleed.Pancake.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BetterAutoPlay
{
    internal static class AutoPlayUiController
    {
        private const float RainbowHueCyclesPerSecond = 0.35f;
        private const float VisualRefreshIntervalSeconds = 0.05f;
        private const float OverlayRebuildDelaySeconds = 2.0f;
        private const float CardAddedOverlayRebuildDelaySeconds = 1.0f;
        private const float AutoPlayContinuationTimeoutSeconds = 0.6f;
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
        private static float s_nextOverlayPrewarmAt;
        private static float s_scheduledOverlayRebuildAt;
        private static bool s_scheduledOverlayRebuildClearCache;
        private static float s_autoPlayContinuationTimeoutAt;
        private static PlayerModel s_autoPlayContinuationPlayer;
        private static bool s_pendingSortOrderRebuild;
        private static TMP_Text s_cachedPlayAllLabel;
        private static string s_defaultPlayAllLabelText;
        private static Color s_defaultPlayAllLabelColor;
        private static bool s_labelOverridden;
        private static bool s_wasShowingAutoPlaying;

        private static readonly MethodInfo s_autoPlayerStopMethod = AccessTools.Method(typeof(AutoPlayer), "StopAutoPlay");

        // Button images + hover colors for direct color change on pointer enter/exit
        private static Image s_orderButtonImg;
        private static Color s_orderButtonNormal;
        private static Color s_orderButtonHover;

        // Pre-computed overlay colors (avoid per-tick HtmlColor/hex parsing)
        private static readonly Color s_overlayIndexColor    = new Color(0.333f, 0.333f, 0.333f, 1f); // #555555
        private static readonly Color s_overlayPlayedColor   = new Color(0.267f, 0.800f, 0.267f, 1f); // #44cc44
        private static readonly Color s_overlayUnAffordColor = new Color(1f, 0.333f, 0.333f, 1f);     // #ff5555
        private static readonly Color s_overlayManaColor     = new Color(0.812f, 0.812f, 0.812f, 1f); // #cfcfcf

        // Order overlay
        private static TMP_FontAsset s_gameFont;
        private static Button s_orderButton;
        private static TMP_Text s_orderButtonLabel;
        private static GameObject s_overlayPanel;
        private static GameObject s_overlayContentRoot;
        private static TMP_Text s_overlayContentText;
        private static bool s_overlayOpen;
        private static bool s_restoreOverlayWhenAvailable;
        private static bool s_playAllButtonAvailable;
        private static bool s_orderButtonCreated;
        private static Button s_lastSeenPlayAllButton;
        private static TMP_Text s_cachedPlayAllLabelForButton;
        private static readonly List<OverlayRow> s_overlayRows = new List<OverlayRow>();
        private static Sprite s_manaOrbSprite;
        private static bool s_manaOrbSpriteGenerated;
        private static float s_nextManaOrbSpriteSearchAt;
        private static Texture2D s_generatedManaOrbTexture;
        private static Material s_dissolveTextMaterialTemplate;
        private static bool s_attemptedDissolveTextMaterialCapture;
        private static long s_lastHandFingerprint;
        private static bool s_loggedOverlayTextMaterialProbe;
        private static bool s_loggedGlobalTmpDissolveProbe;

        private const float OverlayRowHeight = 22f;
        private const float OverlayRowExitDissolveDurationSeconds = 0.28f;
        private const float OverlayRowEnterDissolveDurationSeconds = 0.40f;
        private const int OverlaySortingOrder = 32767;
        private const string DissolveAmountPropertyName = "_DissolveAmount";

        private sealed class OverlayRow
        {
            public GameObject Root;
            public TMP_Text IndexText;
            public TMP_Text NameText;
            public TMP_Text TagText;
            public TMP_Text ManaText;
            public Image ManaIcon;
            public string IndexLabel; // cached so we don't reformat every tick
            public Material IndexBaseMaterial;
            public Material NameBaseMaterial;
            public Material TagBaseMaterial;
            public Material ManaBaseMaterial;
            public Material IndexDissolveMaterial;
            public Material NameDissolveMaterial;
            public Material TagDissolveMaterial;
            public Material ManaDissolveMaterial;
            public bool Dissolving;
            public bool DissolveEntering;
            public float DissolveStartedAt;
        }

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextVisualRefreshAt) return;
            s_nextVisualRefreshAt = now + VisualRefreshIntervalSeconds;

            if (DevLog.FullEnabled)
                DevLog.Info("Tick() -> TryResumePersistentAutoPlay()");
            TryResumePersistentAutoPlay();
            TryPrewarmOverlayAssets(now);
            Refresh(s_lastPlayer);
            UpdateOverlayRowDissolves(now);
        }

        public static void OnButtonPointerEnter(Selectable s)
        {
            if (s_orderButton != null && (object)s == (object)s_orderButton)
            { if (s_orderButtonImg != null) s_orderButtonImg.color = s_orderButtonHover; return; }
        }

        public static void OnButtonPointerExit(Selectable s)
        {
            if (s_orderButton != null && (object)s == (object)s_orderButton)
            { if (s_orderButtonImg != null) s_orderButtonImg.color = s_orderButtonNormal; return; }
        }

        public static void SyncPersistentToggle(PlayerModel player)
        {
            if (player == null)
                return;

            if (s_persistentAutoPlayIntent)
            {
                ClearAutoPlayIntent(player, "button toggle off");
                return;
            }

            s_persistentAutoPlayIntent = true;
            s_manualToggleCooldownUntil = Time.realtimeSinceStartup + 0.4f;
            DevLog.Info("SyncPersistentToggle: intent=" + s_persistentAutoPlayIntent + ", cooldownUntil=" + s_manualToggleCooldownUntil.ToString("F3"));

            TryContinueAutoPlay(player, "button toggle");
        }

        public static void ContinueAutoPlayFromButton(PlayerModel player)
        {
            if (player == null || !s_persistentAutoPlayIntent)
                return;

            TryContinueAutoPlay(player, "button postfix");
        }

        public static void OnAutoPlayCardSucceeded(PlayerModel player)
        {
            if (player == null || !s_persistentAutoPlayIntent)
                return;

            s_autoPlayContinuationTimeoutAt = 0f;
            s_autoPlayContinuationPlayer = null;
            CancelDelayedSortOrderRebuild();

            bool canKeepPlaying = false;
            try { canKeepPlaying = player.CanPlayerKeepPlaying(); }
            catch { }
            if (canKeepPlaying)
            {
                ArmAutoPlayContinuationTimeout(player, "card success");
                TryContinueAutoPlay(player, "card success");
                return;
            }

            ClearAutoPlayIntent(player, "auto-play hand complete");
            ScheduleDelayedSortOrderRebuild(player, "auto-play wave complete: scheduled delayed order cache rebuild", true);
        }

        public static void OnCardAddedToHand(PlayerModel player)
        {
            if (player == null)
                return;

            ScheduleDelayedSortOrderRebuild(player, "card added to hand: scheduled delayed order cache rebuild", true, CardAddedOverlayRebuildDelaySeconds);
        }

        public static void OnManualCardPlayed(PlayerModel player)
        {
            if (player == null)
                return;

            ScheduleDelayedSortOrderRebuild(player, "manual card played: scheduled delayed order cache rebuild", true, CardAddedOverlayRebuildDelaySeconds);
        }

        public static void Refresh(PlayerModel player)
        {
            if (player == null)
                return;
            s_lastPlayer = player;
            TryProcessAutoPlayContinuationTimeout();
            TryProcessScheduledSortOrderRebuild();
            if (s_overlayOpen)
                TryProcessPendingSortOrderRebuild(player);

            Button button = null;
            try { button = player.PlayAllButton; } catch { }

            if (button == null)
            {
                s_playAllButtonAvailable = false;
                CloseOrderOverlay("PlayAll button missing");
                DevLog.Info("Refresh: PlayAll button is null, skipping visual update");
                return;
            }

            bool buttonJustAppeared = !s_playAllButtonAvailable;
            s_playAllButtonAvailable = true;
            EnsureOrderButton(button);
            if (buttonJustAppeared)
                RestoreOrderOverlayIfRequested("button appeared");

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

            // Keep our cloned buttons always interactable — game components on the clone may disable them.
            if (s_orderButton != null)
                try { if (!s_orderButton.interactable) s_orderButton.interactable = true; } catch { }


            if (s_cachedPlayAllLabel != label)
            {
                s_cachedPlayAllLabel = label;
                s_defaultPlayAllLabelText = AutoPlayLabel;
                s_defaultPlayAllLabelColor = label.color;
                s_labelOverridden = false;
            }

            bool canActNow = false;
            try { canActNow = button != null && button.interactable; }
            catch { }

            bool shouldShowAutoPlaying = canActNow && s_persistentAutoPlayIntent;
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

        private static void TryContinueAutoPlay(PlayerModel player, string reason)
        {
            try
            {
                if (player.AutoPlayer == null || player.AutoPlayer.IsPlaying)
                    return;

                bool canKeepPlaying = true;
                try { canKeepPlaying = player.CanPlayerKeepPlaying(); }
                catch { }
                if (!canKeepPlaying)
                    return;

                DevLog.Info("TryContinueAutoPlay: calling AutoPlayer.Play(), reason=" + reason);
                ArmAutoPlayContinuationTimeout(player, reason);
                player.AutoPlayer.Play();
            }
            catch (Exception ex)
            {
                BetterAutoPlayPlugin.LogSource?.LogWarning("[BAP] Auto-play continuation failed: " + ex.Message);
            }
        }

        private static void ArmAutoPlayContinuationTimeout(PlayerModel player, string reason)
        {
            s_autoPlayContinuationPlayer = player;
            s_autoPlayContinuationTimeoutAt = Time.realtimeSinceStartup + AutoPlayContinuationTimeoutSeconds;
            DevLog.Info("ArmAutoPlayContinuationTimeout: reason=" + reason + ", timeoutAt=" + s_autoPlayContinuationTimeoutAt.ToString("F3"));
        }

        private static void EnsureOrderButton(Button playAllButton)
        {
            if (playAllButton == null) return;

            if (s_orderButtonCreated && s_lastSeenPlayAllButton != playAllButton)
            {
                if (s_orderButton != null)
                    try { GameObject.Destroy(s_orderButton.gameObject); } catch { }
                if (s_overlayPanel != null)
                    try { GameObject.Destroy(s_overlayPanel); } catch { }
                s_orderButton = null;
                s_orderButtonLabel = null;
                s_orderButtonImg = null;
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

                s_orderButtonImg    = orderGo.GetComponent<Image>();
                var cb = s_orderButton.colors;
                s_orderButtonNormal = cb.normalColor * cb.colorMultiplier;
                s_orderButtonHover  = cb.highlightedColor * cb.colorMultiplier;

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
            panelGo.transform.SetParent(buttonParent, false);

            float panelH = 500f;
            var rt = panelGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(380f, panelH);
            rt.anchoredPosition = new Vector2(6f, buttonHeight * 0.5f + panelH * 0.5f + 8f);

            Transform topCanvas = FindCanvasTransform(buttonParent);
            panelGo.transform.SetParent(topCanvas, true);
            panelGo.transform.SetAsLastSibling();

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
            titleText.color = new Color(0.9f, 0.82f, 0.55f, 1f);
            titleText.characterSpacing = 3f;

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

            // Column headers
            // Row layout: Index x=0 w=28 | Name x=34 w=164 | Tag x=202 w=82 | Mana x=286 w=26 | Icon x=318
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
            s_restoreOverlayWhenAvailable = s_overlayOpen;
            DevLog.Info("ToggleOverlay: open=" + s_overlayOpen);

            if (s_overlayPanel != null)
                s_overlayPanel.SetActive(s_overlayOpen);

            if (s_orderButtonLabel != null)
                s_orderButtonLabel.text = s_overlayOpen ? "Close" : "Order";

            if (s_overlayOpen)
            {
                if (s_dissolveTextMaterialTemplate == null && !s_attemptedDissolveTextMaterialCapture)
                    TryCaptureDissolveTextMaterialTemplate();
                EnsureSortOrderReadyForOverlay();
                UpdateOverlayContent();
            }
        }

        private static bool RebuildSortOrderFromCurrentHand()
        {
            if (s_lastPlayer == null) return false;
            try
            {
                List<CardModel> hand = null;

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

                // Fallback to the last autoplay-sorted hand only when the live hand
                // could not be read at all for this explicit rebuild request.
                if (hand == null)
                    hand = LiveHandCache.Get();

                if (hand == null || hand.Count == 0)
                {
                    DevLog.Info("RebuildSortOrderFromCurrentHand: no cards available, leaving cache unchanged");
                    return false;
                }

                long fp = HandFingerprint(hand);
                s_lastHandFingerprint = fp;

                var sorted = ComboManaSorter.SortPreservePlayed(s_lastPlayer, hand);
                SortOrderCache.LiveUpdate(sorted, s_lastPlayer);
                return true;
            }
            catch { }
            return false;
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

        private static void InvalidateOverlayRefreshState()
        {
            s_lastHandFingerprint = 0;
        }

        private static void ScheduleDelayedSortOrderRebuild(PlayerModel player, string reason, bool clearCacheFirst)
        {
            ScheduleDelayedSortOrderRebuild(player, reason, clearCacheFirst, OverlayRebuildDelaySeconds);
        }

        private static void ScheduleDelayedSortOrderRebuild(PlayerModel player, string reason, bool clearCacheFirst, float delaySeconds)
        {
            if (player != null)
                s_lastPlayer = player;

            s_scheduledOverlayRebuildAt = Time.realtimeSinceStartup + delaySeconds;
            s_scheduledOverlayRebuildClearCache = clearCacheFirst;
            DevLog.Info(reason + ", delay=" + delaySeconds.ToString("F1") + "s");
        }

        private static void CancelDelayedSortOrderRebuild()
        {
            s_scheduledOverlayRebuildAt = 0f;
            s_scheduledOverlayRebuildClearCache = false;
        }

        private static void TryProcessScheduledSortOrderRebuild()
        {
            if (s_scheduledOverlayRebuildAt <= 0f)
                return;

            if (Time.realtimeSinceStartup < s_scheduledOverlayRebuildAt)
                return;

            if (!IsReadyForOverlayRebuild(s_lastPlayer))
            {
                s_scheduledOverlayRebuildAt = Time.realtimeSinceStartup + 0.25f;
                return;
            }

            bool clearCacheFirst = s_scheduledOverlayRebuildClearCache;
            s_scheduledOverlayRebuildAt = 0f;
            s_scheduledOverlayRebuildClearCache = false;

            InvalidateOverlayRefreshState();
            if (clearCacheFirst)
                SortOrderCache.Reset();
            s_pendingSortOrderRebuild = true;
            DevLog.Info("TryProcessScheduledSortOrderRebuild: delay elapsed, rebuilding order cache");
        }

        private static bool IsReadyForOverlayRebuild(PlayerModel player)
        {
            if (player == null)
                return false;

            try
            {
                if (player.AutoPlayer != null && player.AutoPlayer.IsPlaying)
                    return false;
            }
            catch { }

            try
            {
                var button = player.PlayAllButton;
                if (button != null && !button.interactable)
                    return false;
            }
            catch { }

            return true;
        }

        private static void RequestSortOrderRebuild(PlayerModel player, string reason, bool clearCacheFirst)
        {
            if (player != null)
                s_lastPlayer = player;
            InvalidateOverlayRefreshState();
            if (clearCacheFirst)
                SortOrderCache.Reset();
            s_pendingSortOrderRebuild = true;
            DevLog.Info(reason);
            if (s_overlayOpen)
            {
                TryProcessPendingSortOrderRebuild(player);
                UpdateOverlayContent();
            }
        }

        private static void TryProcessPendingSortOrderRebuild(PlayerModel player)
        {
            if (!s_overlayOpen || !s_pendingSortOrderRebuild)
                return;

            if (player != null)
                s_lastPlayer = player;

            if (!RebuildSortOrderFromCurrentHand())
                return;

            s_pendingSortOrderRebuild = false;
            DevLog.Info("TryProcessPendingSortOrderRebuild: rebuild completed");
            if (s_overlayOpen)
                UpdateOverlayContent();
        }

        private static void EnsureSortOrderReadyForOverlay()
        {
            if (SortOrderCache.Entries.Count == 0)
                s_pendingSortOrderRebuild = true;

            TryProcessPendingSortOrderRebuild(s_lastPlayer);
        }

        public static bool IsOrderOverlayOpen()
        {
            return s_overlayOpen;
        }

        public static void OnTurnStarted(PlayerModel player)
        {
            ComboManaSorter.OnTurnStarted(player ?? s_lastPlayer);
            RestoreOrderOverlayIfRequested("turn started");
            RequestSortOrderRebuild(player, "OnTurnStarted: queued order cache rebuild", true);
        }

        public static void OnTurnEnded(PlayerModel player)
        {
            ComboManaSorter.OnTurnEnded();
            ClearAutoPlayIntent(player ?? s_lastPlayer, "turn ended");
            RequestSortOrderRebuild(player, "OnTurnEnded: queued order cache rebuild", true);
        }

        public static void OnEncounterDefeated(PlayerModel player)
        {
            ComboManaSorter.OnTurnEnded();
            ClearAutoPlayIntent(player ?? s_lastPlayer, "encounter defeated");
            CloseOrderOverlay("encounter defeated");
            RequestSortOrderRebuild(player, "OnEncounterDefeated: queued order cache rebuild", true);
        }

        private static void CloseOrderOverlay(string reason)
        {
            if (!s_overlayOpen)
                return;

            s_overlayOpen = false;
            if (s_overlayPanel != null)
            {
                try { s_overlayPanel.SetActive(false); }
                catch { }
            }
            if (s_orderButtonLabel != null)
            {
                try { s_orderButtonLabel.text = "Order"; }
                catch { }
            }
            DevLog.Info("CloseOrderOverlay: reason=" + reason);
        }

        private static void RestoreOrderOverlayIfRequested(string reason)
        {
            if (!s_restoreOverlayWhenAvailable || s_overlayOpen || s_overlayPanel == null)
                return;

            s_overlayOpen = true;
            try { s_overlayPanel.SetActive(true); }
            catch { }
            if (s_orderButtonLabel != null)
            {
                try { s_orderButtonLabel.text = "Close"; }
                catch { }
            }

            try
            {
                if (s_dissolveTextMaterialTemplate == null && !s_attemptedDissolveTextMaterialCapture)
                    TryCaptureDissolveTextMaterialTemplate();
                EnsureSortOrderReadyForOverlay();
                UpdateOverlayContent();
            }
            catch { }

            DevLog.Info("RestoreOrderOverlayIfRequested: restored open state, reason=" + reason);
        }

        private static void ClearAutoPlayIntent(PlayerModel player, string reason)
        {
            s_persistentAutoPlayIntent = false;
            s_nextResumeAttemptAt = 0f;
            s_lastResumeSkipReason = null;
            s_autoPlayContinuationTimeoutAt = 0f;
            s_autoPlayContinuationPlayer = null;

            try
            {
                if (player != null)
                    s_autoPlayerStopMethod?.Invoke(player.AutoPlayer, null);
            }
            catch { }

            RestoreAutoPlayLabel();
            DevLog.Info("ClearAutoPlayIntent: reason=" + reason);
        }

        private static void TryProcessAutoPlayContinuationTimeout()
        {
            if (!s_persistentAutoPlayIntent || s_autoPlayContinuationTimeoutAt <= 0f)
                return;

            if (Time.realtimeSinceStartup < s_autoPlayContinuationTimeoutAt)
                return;

            var player = s_autoPlayContinuationPlayer ?? s_lastPlayer;
            ClearAutoPlayIntent(player, "auto-play continuation timed out");
            ScheduleDelayedSortOrderRebuild(player, "auto-play lull: scheduled delayed order cache rebuild", true);
        }

        private static void RestoreAutoPlayLabel()
        {
            var label = s_cachedPlayAllLabel;
            if (label == null)
                label = s_cachedPlayAllLabelForButton;
            if (label == null)
                return;

            try
            {
                label.text = s_defaultPlayAllLabelText ?? AutoPlayLabel;
                label.color = s_defaultPlayAllLabelColor;
            }
            catch { }

            s_labelOverridden = false;
            s_wasShowingAutoPlaying = false;
        }

        public static void OnChooseCardModalClosed()
        {
            RequestSortOrderRebuild(null, "OnChooseCardModalClosed: queued soft order cache rebuild", false);
        }

        public static void OnOrderStateChanged(CardModel card)
        {
            if (!s_overlayOpen || card == null)
                return;

            int index;
            SortOrderCache.CardEntry entry;
            if (!SortOrderCache.TryRefreshEntry(card, out index, out entry))
                return;

            UpdateOverlayRow(index, entry);
        }

        private static void UpdateOverlayContent()
        {
            if (s_overlayContentText == null || s_overlayContentRoot == null) return;

            SortOrderCache.RefreshAffordability();

            var entries = SortOrderCache.Entries;
            if (entries.Count == 0)
            {
                HideOverlayRows(false);
                s_overlayContentText.gameObject.SetActive(true);
                s_overlayContentText.text = "<color=#666666>No sort data yet.\nTrigger autoplay once to populate.</color>";
                return;
            }

            s_overlayContentText.gameObject.SetActive(false);
            TryCaptureDissolveTextMaterialTemplate();
            EnsureOverlayRows(entries.Count);
            MaybeRunOverlayTextMaterialProbe();

            var manaSprite = GetManaOrbSprite();
            for (int i = 0; i < entries.Count; i++)
            {
                UpdateOverlayRow(i, entries[i]);
            }

            for (int i = entries.Count; i < s_overlayRows.Count; i++)
                StartRowDissolve(s_overlayRows[i]);
        }

        private static void HideOverlayRows(bool immediate = true)
        {
            for (int i = 0; i < s_overlayRows.Count; i++)
            {
                var row = s_overlayRows[i];
                if (row.Root == null)
                    continue;

                if (immediate)
                {
                    CancelRowDissolve(row);
                    row.Root.SetActive(false);
                }
                else
                {
                    StartRowDissolve(row);
                }
            }
        }

        private static void UpdateOverlayRow(int index, SortOrderCache.CardEntry entry)
        {
            if (index < 0 || index >= s_overlayRows.Count)
                return;

            var row = s_overlayRows[index];
            if (row == null || row.Root == null)
                return;

            bool shouldAnimateIn = false;
            try { shouldAnimateIn = !row.Root.activeSelf || (row.Dissolving && !row.DissolveEntering); } catch { }
            CancelRowDissolve(row);
            row.Root.SetActive(true);

            string idxStr = row.IndexLabel;
            if (idxStr == null)
            {
                idxStr = (index + 1).ToString() + ".";
                row.IndexLabel = idxStr;
                row.IndexText.text = idxStr;
                row.IndexText.color = s_overlayIndexColor;
            }

            if (row.NameText.text != entry.Name)
                row.NameText.text = entry.Name;
            Color nameColor = entry.Played ? s_overlayPlayedColor : (entry.CanAfford ? Color.white : s_overlayUnAffordColor);
            if (row.NameText.color != nameColor)
                row.NameText.color = nameColor;

            string roleLabel = string.IsNullOrEmpty(entry.RoleLabel) ? entry.Role.ToString() : entry.RoleLabel;
            string tagStr = "[" + roleLabel + "]";
            if (row.TagText.text != tagStr)
                row.TagText.text = tagStr;
            Color tagColor = HtmlColor(string.IsNullOrEmpty(entry.RoleColor) ? CardClassifier.RoleColor(entry.Role) : entry.RoleColor, Color.gray);
            if (row.TagText.color != tagColor)
                row.TagText.color = tagColor;

            string manaStr = entry.ManaCost.ToString();
            if (row.ManaText.text != manaStr)
                row.ManaText.text = manaStr;
            if (row.ManaText.color != s_overlayManaColor)
                row.ManaText.color = s_overlayManaColor;

            row.ManaIcon.enabled = true;
            row.ManaIcon.sprite = GetManaOrbSprite();

            if (shouldAnimateIn)
                StartRowAppear(row);
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
            row.IndexText = CreateOverlayRowText(rowGo.transform, "Index", 0f,   28f,  12f, TextAlignmentOptions.MidlineRight);
            row.NameText  = CreateOverlayRowText(rowGo.transform, "Name",  34f,  164f, 12f, TextAlignmentOptions.MidlineLeft);
            row.TagText   = CreateOverlayRowText(rowGo.transform, "Tag",   202f, 82f,  11f, TextAlignmentOptions.MidlineLeft);
            row.ManaText  = CreateOverlayRowText(rowGo.transform, "Mana",  286f, 26f,  12f, TextAlignmentOptions.MidlineRight);
            row.ManaIcon  = CreateOverlayManaIcon(rowGo.transform, 318f);
            CaptureBaseMaterial(ref row.IndexBaseMaterial, row.IndexText);
            CaptureBaseMaterial(ref row.NameBaseMaterial, row.NameText);
            CaptureBaseMaterial(ref row.TagBaseMaterial, row.TagText);
            CaptureBaseMaterial(ref row.ManaBaseMaterial, row.ManaText);
            rowGo.SetActive(false);
            return row;
        }

        private static void TryCaptureDissolveTextMaterialTemplate()
        {
            if (s_dissolveTextMaterialTemplate != null || s_attemptedDissolveTextMaterialCapture)
                return;

            s_attemptedDissolveTextMaterialCapture = true;

            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
                if (texts == null)
                    return;

                for (int i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    if (text == null)
                        continue;

                    var fontMaterial = SafeGetFontMaterial(text);
                    var sharedMaterial = SafeGetSharedMaterial(text);
                    Material template = SelectBestDissolveMaterial(fontMaterial) ?? SelectBestDissolveMaterial(sharedMaterial);
                    if (template == null)
                        continue;

                    s_dissolveTextMaterialTemplate = new Material(template);
                    s_dissolveTextMaterialTemplate.name = template.name + " [BAP Template]";
                    DevLog.Info("[TMPProbe] Captured dissolve TMP material template from " + GetTransformPath(text.transform)
                        + " material='" + template.name + "' shader='" + SafeGetShaderName(template) + "'");
                    return;
                }
            }
            catch (Exception ex)
            {
                DevLog.Info("[TMPProbe] Dissolve TMP material capture failed: " + ex.Message);
            }
        }

        private static void MaybeRunOverlayTextMaterialProbe()
        {
            if (!DevLog.FullEnabled)
                return;

            if (!s_loggedOverlayTextMaterialProbe)
            {
                s_loggedOverlayTextMaterialProbe = true;
                DevLog.Info("[TMPProbe] Overlay row material probe starting");
                for (int i = 0; i < s_overlayRows.Count; i++)
                {
                    var row = s_overlayRows[i];
                    ProbeTmpTextMaterial("Overlay.Row" + (i + 1) + ".Index", row.IndexText);
                    ProbeTmpTextMaterial("Overlay.Row" + (i + 1) + ".Name", row.NameText);
                    ProbeTmpTextMaterial("Overlay.Row" + (i + 1) + ".Tag", row.TagText);
                    ProbeTmpTextMaterial("Overlay.Row" + (i + 1) + ".Mana", row.ManaText);
                }
            }

            if (!s_loggedGlobalTmpDissolveProbe)
            {
                s_loggedGlobalTmpDissolveProbe = true;
                ProbeGlobalTmpTextsForDissolve();
            }
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

        private static void CaptureBaseMaterial(ref Material destination, TMP_Text text)
        {
            if (destination != null || text == null)
                return;

            try
            {
                var current = SafeGetSharedMaterial(text);
                if (current == null)
                    current = SafeGetFontMaterial(text);
                if (current == null)
                    return;

                destination = new Material(current);
                destination.name = current.name + " [BAP Base " + text.name + "]";
            }
            catch { }
        }

        private static void UpdateOverlayRowDissolves(float now)
        {
            for (int i = 0; i < s_overlayRows.Count; i++)
            {
                var row = s_overlayRows[i];
                if (!row.Dissolving)
                    continue;

                float duration = row.DissolveEntering ? OverlayRowEnterDissolveDurationSeconds : OverlayRowExitDissolveDurationSeconds;
                float t = Mathf.Clamp01((now - row.DissolveStartedAt) / duration);
                float dissolveAmount = row.DissolveEntering ? (1f - t) : t;
                SetRowDissolveAmount(row, dissolveAmount);

                try
                {
                    row.Root.transform.localScale = row.DissolveEntering
                        ? Vector3.Lerp(new Vector3(0.94f, 0.94f, 1f), Vector3.one, t)
                        : Vector3.Lerp(Vector3.one, new Vector3(0.94f, 0.94f, 1f), t);
                }
                catch { }

                if (t < 1f)
                    continue;

                bool wasEntering = row.DissolveEntering;
                row.Dissolving = false;
                row.DissolveEntering = false;
                RestoreBaseMaterials(row);
                try { row.Root.transform.localScale = Vector3.one; } catch { }
                if (!wasEntering)
                    try { row.Root.SetActive(false); } catch { }
            }
        }

        private static void StartRowAppear(OverlayRow row)
        {
            if (row == null || row.Root == null)
                return;

            if (s_dissolveTextMaterialTemplate == null)
                return;

            ApplyDissolveMaterial(row.IndexText, ref row.IndexDissolveMaterial);
            ApplyDissolveMaterial(row.NameText, ref row.NameDissolveMaterial);
            ApplyDissolveMaterial(row.TagText, ref row.TagDissolveMaterial);
            ApplyDissolveMaterial(row.ManaText, ref row.ManaDissolveMaterial);
            SetRowDissolveAmount(row, 1f);
            row.Dissolving = true;
            row.DissolveEntering = true;
            row.DissolveStartedAt = Time.realtimeSinceStartup;
            try { row.Root.transform.localScale = new Vector3(0.94f, 0.94f, 1f); } catch { }
        }

        private static void StartRowDissolve(OverlayRow row)
        {
            if (row == null || row.Root == null)
                return;

            bool isActive = false;
            try { isActive = row.Root.activeSelf; } catch { }
            if (!isActive || row.Dissolving)
                return;

            if (s_dissolveTextMaterialTemplate == null)
            {
                row.Root.SetActive(false);
                return;
            }

            ApplyDissolveMaterial(row.IndexText, ref row.IndexDissolveMaterial);
            ApplyDissolveMaterial(row.NameText, ref row.NameDissolveMaterial);
            ApplyDissolveMaterial(row.TagText, ref row.TagDissolveMaterial);
            ApplyDissolveMaterial(row.ManaText, ref row.ManaDissolveMaterial);
            SetRowDissolveAmount(row, 0f);
            row.Dissolving = true;
            row.DissolveEntering = false;
            row.DissolveStartedAt = Time.realtimeSinceStartup;
        }

        private static void CancelRowDissolve(OverlayRow row)
        {
            if (row == null)
                return;

            if (!row.Dissolving)
            {
                RestoreBaseMaterials(row);
                return;
            }

            row.Dissolving = false;
            row.DissolveEntering = false;
            SetRowDissolveAmount(row, 0f);
            RestoreBaseMaterials(row);
            try { if (row.Root != null) row.Root.transform.localScale = Vector3.one; } catch { }
        }

        private static void RestoreBaseMaterials(OverlayRow row)
        {
            RestoreBaseMaterial(row.IndexText, row.IndexBaseMaterial);
            RestoreBaseMaterial(row.NameText, row.NameBaseMaterial);
            RestoreBaseMaterial(row.TagText, row.TagBaseMaterial);
            RestoreBaseMaterial(row.ManaText, row.ManaBaseMaterial);
        }

        private static void RestoreBaseMaterial(TMP_Text text, Material material)
        {
            if (text == null || material == null)
                return;

            try { text.fontSharedMaterial = material; }
            catch { }
        }

        private static void ApplyDissolveMaterial(TMP_Text text, ref Material destination)
        {
            if (text == null || s_dissolveTextMaterialTemplate == null)
                return;

            try
            {
                if (destination == null)
                {
                    destination = new Material(s_dissolveTextMaterialTemplate);
                    destination.name = s_dissolveTextMaterialTemplate.name + " [" + text.name + " Dissolve]";
                }

                text.fontSharedMaterial = destination;
            }
            catch { }
        }

        private static void SetRowDissolveAmount(OverlayRow row, float value)
        {
            SetMaterialDissolveAmount(row.IndexDissolveMaterial, value);
            SetMaterialDissolveAmount(row.NameDissolveMaterial, value);
            SetMaterialDissolveAmount(row.TagDissolveMaterial, value);
            SetMaterialDissolveAmount(row.ManaDissolveMaterial, value);
        }

        private static void SetMaterialDissolveAmount(Material material, float value)
        {
            if (material == null)
                return;

            try
            {
                if (material.HasProperty(DissolveAmountPropertyName))
                    material.SetFloat(DissolveAmountPropertyName, value);
            }
            catch { }
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
            if (s_manaOrbSprite == null)
            {
                s_manaOrbSprite = CreateGeneratedManaOrbSprite();
                s_manaOrbSpriteGenerated = true;
            }
            return s_manaOrbSprite;
        }

        private static void TryPrewarmOverlayAssets(float now)
        {
            if (now < s_nextOverlayPrewarmAt)
                return;

            s_nextOverlayPrewarmAt = now + 1f;

            if (s_dissolveTextMaterialTemplate == null && !s_attemptedDissolveTextMaterialCapture)
            {
                TryCaptureDissolveTextMaterialTemplate();
                return;
            }

            if (s_manaOrbSprite == null)
            {
                s_manaOrbSprite = CreateGeneratedManaOrbSprite();
                s_manaOrbSpriteGenerated = true;
                return;
            }

            if (!s_manaOrbSpriteGenerated)
                return;

            if (now < s_nextManaOrbSpriteSearchAt)
                return;

            s_nextManaOrbSpriteSearchAt = now + 2f;
            var gameSprite = TryFindGameManaOrbSprite();
            if (gameSprite == null)
                return;

            s_manaOrbSprite = gameSprite;
            s_manaOrbSpriteGenerated = false;
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
            if (text.Contains("mana"))         score += 60;
            if (text.Contains("cost"))         score += 35;
            if (text.Contains("orb"))          score += 35;
            if (text.Contains("blood"))        score += 25;
            if (text.Contains("cardmanacost")) score += 45;
            if (text.Contains("button"))       score -= 35;
            if (text.Contains("background"))   score -= 20;
            if (text.Contains("cardimage"))    score -= 25;

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

        private static void ProbeGlobalTmpTextsForDissolve()
        {
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
                if (texts == null)
                {
                    DevLog.Info("[TMPProbe] Global TMP dissolve scan found no TMP_Text objects");
                    return;
                }

                int logged = 0;
                for (int i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    if (text == null)
                        continue;

                    var fontMaterial = SafeGetFontMaterial(text);
                    var sharedMaterial = SafeGetSharedMaterial(text);
                    var renderingMaterial = SafeGetMaterialForRendering(text);

                    if (!MaterialLooksDissolveCapable(fontMaterial) &&
                        !MaterialLooksDissolveCapable(sharedMaterial) &&
                        !MaterialLooksDissolveCapable(renderingMaterial))
                        continue;

                    DevLog.Info("[TMPProbe] Dissolve-capable TMP text found at " + GetTransformPath(text.transform));
                    ProbeTmpTextMaterial("GlobalTMP[" + logged + "]", text);
                    logged++;
                    if (logged >= 20)
                        break;
                }

                DevLog.Info("[TMPProbe] Global TMP dissolve scan complete. Matches=" + logged + ", Total=" + texts.Length);
            }
            catch (Exception ex)
            {
                DevLog.Info("[TMPProbe] Global TMP dissolve scan failed: " + ex.Message);
            }
        }

        private static Material SelectBestDissolveMaterial(Material material)
        {
            if (!MaterialLooksDissolveCapable(material))
                return null;

            string shaderName = SafeLower(SafeGetShaderName(material));
            if (shaderName.Contains("distance field dissolve"))
                return material;

            if (shaderName.Contains("dissolve"))
                return material;

            return null;
        }

        private static void ProbeTmpTextMaterial(string label, TMP_Text text)
        {
            if (text == null)
            {
                DevLog.Info("[TMPProbe] " + label + " -> text is null");
                return;
            }

            try
            {
                DevLog.Info("[TMPProbe] " + label + " path=" + GetTransformPath(text.transform) + ", text='" + SafeSnippet(text.text) + "'");
            }
            catch { }

            ProbeMaterial(label + ".fontMaterial", SafeGetFontMaterial(text));
            ProbeMaterial(label + ".sharedMaterial", SafeGetSharedMaterial(text));
            ProbeMaterial(label + ".materialForRendering", SafeGetMaterialForRendering(text));
        }

        private static void ProbeMaterial(string label, Material material)
        {
            if (material == null)
            {
                DevLog.Info("[TMPProbe] " + label + " -> null");
                return;
            }

            string shaderName = "?";
            try { shaderName = material.shader != null ? material.shader.name : "<null-shader>"; } catch { }

            string keywords = "";
            try
            {
                var shaderKeywords = material.shaderKeywords;
                if (shaderKeywords != null && shaderKeywords.Length > 0)
                    keywords = string.Join(",", shaderKeywords);
            }
            catch { }

            DevLog.Info("[TMPProbe] " + label + " material='" + material.name + "', shader='" + shaderName + "', dissolveLike=" + MaterialLooksDissolveCapable(material) + ", keywords='" + keywords + "'");

            string[] candidateProps =
            {
                "_DissolveAmount",
                "_Dissolve",
                "_DissolveValue",
                "_DissolveFade",
                "_DissolveAlpha",
                "_DissolveProgress",
                "_Cutoff"
            };

            for (int i = 0; i < candidateProps.Length; i++)
            {
                string prop = candidateProps[i];
                bool hasProp = false;
                try { hasProp = material.HasProperty(prop); } catch { }
                if (!hasProp)
                    continue;

                float value = 0f;
                try { value = material.GetFloat(prop); } catch { }
                DevLog.Info("[TMPProbe] " + label + " property " + prop + "=" + value.ToString("F4"));
            }

            try
            {
                var shader = material.shader;
                if (shader == null)
                    return;

                var getCount = typeof(Shader).GetMethod("GetPropertyCount", Type.EmptyTypes);
                var getName = typeof(Shader).GetMethod("GetPropertyName", new[] { typeof(int) });
                if (getCount == null || getName == null)
                    return;

                int count = Convert.ToInt32(getCount.Invoke(shader, null));
                for (int i = 0; i < count; i++)
                {
                    string propName = getName.Invoke(shader, new object[] { i }) as string;
                    if (string.IsNullOrEmpty(propName))
                        continue;

                    string normalized = SafeLower(propName);
                    if (normalized.Contains("dissolve") || normalized.Contains("cutoff") || normalized.Contains("fade"))
                        DevLog.Info("[TMPProbe] " + label + " shader property candidate=" + propName);
                }
            }
            catch { }
        }

        private static bool MaterialLooksDissolveCapable(Material material)
        {
            if (material == null)
                return false;

            try
            {
                if (material.shader != null)
                {
                    string shaderName = SafeLower(material.shader.name);
                    if (shaderName.Contains("dissolve") || shaderName.Contains("fade"))
                        return true;
                }
            }
            catch { }

            string[] candidateProps =
            {
                "_DissolveAmount",
                "_Dissolve",
                "_DissolveValue",
                "_DissolveFade",
                "_DissolveAlpha",
                "_DissolveProgress",
                "_Cutoff"
            };

            for (int i = 0; i < candidateProps.Length; i++)
            {
                try
                {
                    if (material.HasProperty(candidateProps[i]))
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static string SafeGetShaderName(Material material)
        {
            if (material == null)
                return "";

            try { return material.shader != null ? (material.shader.name ?? "") : ""; }
            catch { return ""; }
        }

        private static Material SafeGetFontMaterial(TMP_Text text)
        {
            try { return text.fontMaterial; }
            catch { return null; }
        }

        private static Material SafeGetSharedMaterial(TMP_Text text)
        {
            try { return text.fontSharedMaterial; }
            catch { return null; }
        }

        private static Material SafeGetMaterialForRendering(TMP_Text text)
        {
            try { return text.materialForRendering; }
            catch { return null; }
        }

        private static string SafeSnippet(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 48 ? value : value.Substring(0, 48);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            try
            {
                var current = transform.parent;
                while (current != null)
                {
                    path = current.name + "/" + path;
                    current = current.parent;
                }
            }
            catch { }

            return path;
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
}
