using System;
using System.Collections.Generic;
using Nosebleed.Pancake.Models;

namespace BetterAutoPlay
{
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
}
