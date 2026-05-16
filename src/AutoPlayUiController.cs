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
        private static readonly Color s_overlayIndexColor    = new Color(0.333f, 0.333f, 0.333f, 1f); // #555555
        private static readonly Color s_overlayPlayedColor   = new Color(0.267f, 0.800f, 0.267f, 1f); // #44cc44
        private static readonly Color s_overlayUnAffordColor = new Color(1f, 0.333f, 0.333f, 1f);     // #ff5555
        private static readonly Color s_overlayManaColor     = new Color(0.812f, 0.812f, 0.812f, 1f); // #cfcfcf

        // Order overlay
        private static TMP_FontAsset s_gameFont;
        private static Button s_orderButton;
        private static TMP_Text s_orderButtonLabel;
        private static Button s_refreshButton;
        private static GameObject s_overlayPanel;
        private static GameObject s_overlayContentRoot;
        private static TMP_Text s_overlayContentText;
        private static bool s_overlayOpen;
        private static bool s_orderButtonCreated;
        private static Button s_lastSeenPlayAllButton;
        private static TMP_Text s_cachedPlayAllLabelForButton;
        private static readonly System.Text.StringBuilder s_overlaySb = new System.Text.StringBuilder(512);
        private static readonly List<OverlayRow> s_overlayRows = new List<OverlayRow>();
        private static Sprite s_manaOrbSprite;
        private static bool s_manaOrbSpriteGenerated;
        private static float s_nextManaOrbSpriteSearchAt;
        private static Texture2D s_generatedManaOrbTexture;
        private static long s_lastHandFingerprint;

        private const float OverlayRowHeight = 22f;
        private const int OverlaySortingOrder = 32767;

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
            rt.anchoredPosition = new Vector2(0f, buttonHeight * 0.5f + panelH * 0.5f + 8f);

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
            DevLog.Info("ToggleOverlay: open=" + s_overlayOpen);

            if (s_overlayOpen)
            {
                try { s_autoPlayerStopMethod?.Invoke(s_lastPlayer?.AutoPlayer, null); } catch { }
                s_lastHandFingerprint = 0;
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

                // Index — only write once per row (index never changes after row creation)
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
            row.IndexText = CreateOverlayRowText(rowGo.transform, "Index", 0f,   28f,  12f, TextAlignmentOptions.MidlineRight);
            row.NameText  = CreateOverlayRowText(rowGo.transform, "Name",  34f,  164f, 12f, TextAlignmentOptions.MidlineLeft);
            row.TagText   = CreateOverlayRowText(rowGo.transform, "Tag",   202f, 82f,  11f, TextAlignmentOptions.MidlineLeft);
            row.ManaText  = CreateOverlayRowText(rowGo.transform, "Mana",  286f, 26f,  12f, TextAlignmentOptions.MidlineRight);
            row.ManaIcon  = CreateOverlayManaIcon(rowGo.transform, 318f);
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
}
