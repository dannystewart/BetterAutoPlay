using System;
using System.Collections.Generic;
using System.Reflection;
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
            public bool CumulativeManaExceedsCurrent;
            public bool Played;
        }

        public static readonly List<CardEntry> Entries = new List<CardEntry>();
        private static readonly List<CardModel> s_cardRefs = new List<CardModel>();
        private static readonly HashSet<long> s_playedPtrs = new HashSet<long>();
        private static PlayerModel s_player;
        private static readonly string[] s_currentManaValueMemberNames = { "CurrentMana", "currentMana", "_currentMana", "CachedMana", "_cachedMana" };
        private static readonly string[] s_manaContainerMemberNames = { "Mana", "FccMana", "PlayerMana", "ManaModel", "ManaStat", "Stats" };
        private const int MaxManaProbeCost = 99;
        private static string s_lastCumulativeManaDebug;

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
                RefreshCumulativeManaWarnings();
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

            RefreshCumulativeManaWarnings();
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

            RefreshCumulativeManaWarnings();
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
                RefreshCumulativeManaWarnings();
                index = i;
                entry = e;
                return true;
            }

            return false;
        }

        private static void RefreshCumulativeManaWarnings()
        {
            int currentMana = GetCurrentPlayerMana(s_player);
            int cumulativeCost = 0;
            int warnedCount = 0;

            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e.Played)
                {
                    e.CumulativeManaExceedsCurrent = false;
                    Entries[i] = e;
                    continue;
                }

                cumulativeCost += Math.Max(0, e.ManaCost);
                e.CumulativeManaExceedsCurrent = currentMana >= 0 && cumulativeCost > currentMana;
                if (e.CumulativeManaExceedsCurrent)
                    warnedCount++;
                Entries[i] = e;
            }

            if (DevLog.FullEnabled)
            {
                string debug = "currentMana=" + currentMana + ", cumulativeCost=" + cumulativeCost + ", warned=" + warnedCount + ", entries=" + Entries.Count;
                if (debug != s_lastCumulativeManaDebug)
                {
                    s_lastCumulativeManaDebug = debug;
                    DevLog.Info("[OrderMana] " + debug);
                }
            }
        }

        private static int GetCurrentPlayerMana(PlayerModel player)
        {
            if (player == null) return -1;
            try
            {
                int mana;
                if (TryProbeCurrentManaFromObject(player, 0, out mana))
                    return mana;

                if (TryReadCurrentManaFromObject(player, 0, out mana))
                    return mana;
            }
            catch { }

            return -1;
        }

        private static bool TryProbeCurrentManaFromObject(object source, int depth, out int value)
        {
            value = -1;
            if (source == null || depth > 3)
                return false;

            try
            {
                Type type = source.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var method = type.GetMethod("CanAffordManaCost", flags, null, new[] { typeof(int) }, null);
                if (method != null && TryInferManaFromAffordabilityMethod(method, source, out value))
                    return true;

                for (int i = 0; i < s_manaContainerMemberNames.Length; i++)
                {
                    object nested;
                    if (TryGetMemberValue(type, source, s_manaContainerMemberNames[i], flags, out nested)
                        && !ReferenceEquals(nested, source)
                        && TryProbeCurrentManaFromObject(nested, depth + 1, out value))
                    {
                        return true;
                    }
                }

                if (depth >= 2)
                    return false;

                var members = type.GetMembers(flags);
                for (int i = 0; i < members.Length; i++)
                {
                    var member = members[i];
                    if (!LooksLikeManaContainerName(member.Name))
                        continue;

                    object nested;
                    if (TryGetMemberValue(member, source, out nested)
                        && !ReferenceEquals(nested, source)
                        && TryProbeCurrentManaFromObject(nested, depth + 1, out value))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryInferManaFromAffordabilityMethod(MethodInfo method, object target, out int value)
        {
            value = -1;
            if (method == null || target == null || method.ReturnType != typeof(bool))
                return false;

            try
            {
                bool canAffordZero = Convert.ToBoolean(method.Invoke(target, new object[] { 0 }));
                if (!canAffordZero)
                    return false;

                int highest = -1;
                for (int cost = 0; cost <= MaxManaProbeCost; cost++)
                {
                    bool canAfford = Convert.ToBoolean(method.Invoke(target, new object[] { cost }));
                    if (!canAfford)
                        break;
                    highest = cost;
                }

                if (highest < 0 || highest >= MaxManaProbeCost)
                    return false;

                value = highest;
                return true;
            }
            catch { }

            return false;
        }

        private static bool TryReadCurrentManaFromObject(object source, int depth, out int value)
        {
            value = -1;
            if (source == null || depth > 3)
                return false;

            try
            {
                Type type = source.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                for (int i = 0; i < s_currentManaValueMemberNames.Length; i++)
                {
                    object raw;
                    if (TryGetMemberValue(type, source, s_currentManaValueMemberNames[i], flags, out raw) && TryConvertToInt(raw, out value))
                        return true;
                }

                for (int i = 0; i < s_manaContainerMemberNames.Length; i++)
                {
                    object nested;
                    if (TryGetMemberValue(type, source, s_manaContainerMemberNames[i], flags, out nested)
                        && !ReferenceEquals(nested, source)
                        && TryReadCurrentManaFromObject(nested, depth + 1, out value))
                    {
                        return true;
                    }
                }

                if (depth >= 2)
                    return false;

                var members = type.GetMembers(flags);
                for (int i = 0; i < members.Length; i++)
                {
                    var member = members[i];
                    if (!LooksLikeManaContainerName(member.Name))
                        continue;

                    object nested;
                    if (TryGetMemberValue(member, source, out nested)
                        && !ReferenceEquals(nested, source)
                        && TryReadCurrentManaFromObject(nested, depth + 1, out value))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetMemberValue(Type type, object target, string name, BindingFlags flags, out object value)
        {
            value = null;
            if (type == null || target == null || string.IsNullOrEmpty(name))
                return false;

            var property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                return TryGetMemberValue(property, target, out value);

            var field = type.GetField(name, flags);
            return TryGetMemberValue(field, target, out value);
        }

        private static bool TryGetMemberValue(MemberInfo member, object target, out object value)
        {
            value = null;
            if (member == null || target == null)
                return false;

            try
            {
                var property = member as PropertyInfo;
                if (property != null)
                {
                    if (property.GetIndexParameters().Length != 0)
                        return false;
                    value = property.GetValue(target, null);
                    return value != null;
                }

                var field = member as FieldInfo;
                if (field != null)
                {
                    value = field.GetValue(target);
                    return value != null;
                }
            }
            catch { }

            return false;
        }

        private static bool TryConvertToInt(object raw, out int value)
        {
            value = -1;
            if (raw == null)
                return false;

            Type type = raw.GetType();
            if (type.IsEnum || raw is bool || raw is char)
                return false;

            TypeCode code = Type.GetTypeCode(type);
            if (code != TypeCode.Byte
                && code != TypeCode.SByte
                && code != TypeCode.Int16
                && code != TypeCode.UInt16
                && code != TypeCode.Int32
                && code != TypeCode.UInt32
                && code != TypeCode.Int64
                && code != TypeCode.UInt64)
            {
                return false;
            }

            try
            {
                value = Convert.ToInt32(raw);
                return value >= 0;
            }
            catch { }

            return false;
        }

        private static bool LooksLikeManaContainerName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf("Mana", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return name.IndexOf("Cost", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Modifier", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Starting", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Reward", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Breakdown", StringComparison.OrdinalIgnoreCase) < 0;
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
