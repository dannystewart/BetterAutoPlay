using System;
using System.Collections.Generic;
using Nosebleed.Pancake.Models;

namespace BetterAutoPlay
{
    internal static class LiveHandCache
    {
        private static object s_rawList;
        public static void Set(object list) { s_rawList = list; }
        public static void Clear() { s_rawList = null; }
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

        public static void Reset()
        {
            Entries.Clear();
            s_cardRefs.Clear();
            s_playedPtrs.Clear();
            s_player = null;
            LiveHandCache.Clear();
        }

        public static void Update(List<CardModel> sorted, PlayerModel player)
        {
            s_player = player;
            s_playedPtrs.Clear();
            Rebuild(sorted, player);
        }

        public static void LiveUpdate(List<CardModel> sorted, PlayerModel player)
        {
            s_player = player;

            var sortedPtrs = new HashSet<long>();
            if (sorted != null)
            {
                for (int i = 0; i < sorted.Count; i++)
                {
                    long ptr = GetCardPtr(sorted[i]);
                    if (ptr != 0)
                        sortedPtrs.Add(ptr);
                }
            }

            // Collect played entries whose cards are no longer in the incoming hand,
            // so they survive the rebuild and stay visible in the overlay.
            List<CardEntry> orphanEntries = null;
            List<CardModel> orphanRefs    = null;
            HashSet<long> orphanPtrs = null;
            if (s_playedPtrs.Count > 0)
            {
                for (int i = 0; i < s_cardRefs.Count && i < Entries.Count; i++)
                {
                    if (!Entries[i].Played) continue;
                    var cardRef = s_cardRefs[i];
                    long ptr = GetCardPtr(cardRef);
                    if (ptr == 0) continue;

                    if (!sortedPtrs.Contains(ptr))
                    {
                        if (orphanPtrs != null && orphanPtrs.Contains(ptr))
                            continue;

                        if (orphanEntries == null)
                        {
                            orphanEntries = new List<CardEntry>();
                            orphanRefs = new List<CardModel>();
                            orphanPtrs = new HashSet<long>();
                        }

                        orphanEntries.Add(Entries[i]);
                        orphanRefs.Add(cardRef);
                        orphanPtrs.Add(ptr);
                    }
                }
            }

            Rebuild(sorted, player);

            // Re-insert played cards at the top in their original order.
            if (orphanEntries != null)
            {
                Entries.InsertRange(0, orphanEntries);
                s_cardRefs.InsertRange(0, orphanRefs);
            }
        }

        private static void Rebuild(List<CardModel> sorted, PlayerModel player)
        {
            Entries.Clear();
            s_cardRefs.Clear();
            var seenPtrs = new HashSet<long>();
            foreach (var card in sorted)
            {
                if (card == null) continue;
                long ptr = GetCardPtr(card);
                if (ptr != 0 && !seenPtrs.Add(ptr))
                    continue;

                s_cardRefs.Add(card);
                var role = CardClassifier.Classify(card);
                var display = CardClassifier.Describe(card, role);
                int mana = GetCurrentManaCost(card);
                bool canAfford = true;
                try { if (player != null) canAfford = player.CanAffordCard(card); } catch { }
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
                try { e.Played = s_playedPtrs.Contains(card.Pointer.ToInt64()); } catch { }
                Entries[i] = e;
            }
        }

        public static bool TryRefreshEntry(CardModel card, out int index, out CardEntry entry)
        {
            index = -1;
            entry = default;
            if (card == null)
                return false;

            long ptr = GetCardPtr(card);
            if (ptr == 0)
                return false;

            for (int i = 0; i < s_cardRefs.Count && i < Entries.Count; i++)
            {
                if (GetCardPtr(s_cardRefs[i]) != ptr)
                    continue;

                var e = Entries[i];
                if (s_player != null)
                {
                    try { e.CanAfford = s_player.CanAffordCard(s_cardRefs[i]); } catch { }
                }
                e.ManaCost = GetCurrentManaCost(s_cardRefs[i]);
                e.Played = s_playedPtrs.Contains(ptr);
                Entries[i] = e;
                index = i;
                entry = e;
                return true;
            }

            return false;
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
                long ptr = GetCardPtr(card);
                if (ptr == 0) return;
                s_playedPtrs.Add(ptr);
                for (int i = 0; i < s_cardRefs.Count; i++)
                {
                    if (GetCardPtr(s_cardRefs[i]) == ptr)
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

        private static long GetCardPtr(CardModel card)
        {
            if (card == null) return 0;
            try { return card.Pointer.ToInt64(); }
            catch { return 0; }
        }
    }
}
