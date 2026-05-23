using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.Models;

namespace BetterAutoPlay
{
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
            Dictionary<CardModel, bool> inComboNow,
            HashSet<ComboPairKey> comboPairs)
        {
            OriginalIndex = originalIndex;
            Roles = roles;
            ManaGains = manaGains;
            DrawScores = drawScores;
            ManaCosts = manaCosts;
            ComboCosts = comboCosts;
            Evolved = evolved;
            InComboNow = inComboNow;
            ComboPairs = comboPairs;
        }

        public Dictionary<CardModel, int> OriginalIndex { get; }
        public Dictionary<CardModel, CardRole> Roles { get; }
        public Dictionary<CardModel, int> ManaGains { get; }
        public Dictionary<CardModel, int> DrawScores { get; }
        public Dictionary<CardModel, int> ManaCosts { get; }
        public Dictionary<CardModel, int> ComboCosts { get; }
        public Dictionary<CardModel, bool> Evolved { get; }
        public Dictionary<CardModel, bool> InComboNow { get; }
        public HashSet<ComboPairKey> ComboPairs { get; }
    }

    internal struct ComboPairKey : IEquatable<ComboPairKey>
    {
        public readonly long PreviousPtr;
        public readonly long CardPtr;

        public ComboPairKey(long previousPtr, long cardPtr)
        {
            PreviousPtr = previousPtr;
            CardPtr = cardPtr;
        }

        public bool Equals(ComboPairKey other)
        {
            return PreviousPtr == other.PreviousPtr && CardPtr == other.CardPtr;
        }

        public override bool Equals(object obj)
        {
            return obj is ComboPairKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (PreviousPtr.GetHashCode() * 397) ^ CardPtr.GetHashCode();
            }
        }
    }

    internal static class ComboManaSorter
    {
        private struct TurnCardFacts
        {
            public CardRole Role;
            public int ManaGain;
            public int DrawScore;
            public int ManaCost;
            public int ComboCost;
            public bool Evolved;
            public bool InComboNow;
        }

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
        private static readonly Dictionary<long, TurnCardFacts> s_turnFactsByPtr = new Dictionary<long, TurnCardFacts>();
        private static readonly HashSet<ComboPairKey> s_turnComboPairs = new HashSet<ComboPairKey>();
        private static readonly string[] s_turnPileMemberNames = new[]
        {
            "HandPile",
            "DrawPile",
            "DeckPile",
            "DiscardPile",
            "ExhaustPile"
        };
        private static PlayerModel s_turnPreparedPlayer;
        private static bool s_turnPrepared;

        internal static PropertyInfo GainAmountProperty => s_gainAmountProp;

        public static void OnTurnStarted(PlayerModel player)
        {
            ResetTurnFacts();
            PrepareTurnFacts(player);
        }

        public static void OnTurnEnded()
        {
            ResetTurnFacts();
        }

        public static void OnCardAddedToHand(PlayerModel player, CardModel card)
        {
            if (!s_turnPrepared || player == null || card == null)
                return;

            s_turnPreparedPlayer = player;
            AddCardToTurnFacts(player, card);
        }

        public static List<CardModel> Sort(PlayerModel player, List<CardModel> input)
        {
            return SortCore(player, input);
        }

        public static List<CardModel> SortPreservePlayed(PlayerModel player, List<CardModel> input)
        {
            return SortCore(player, input);
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
            if (s_turnPrepared && subset != null && subset.Count > 0)
                return BuildContextFromTurnFacts(subset);

            var originalIndex = BuildOriginalIndex(subset);
            var roles = BuildRoles(subset);
            var manaGains = BuildManaGains(subset, roles);
            var drawScores = BuildDrawScores(subset);
            var manaCosts = BuildManaCosts(subset);
            var comboCosts = BuildComboCosts(subset);
            var evolved = BuildEvolvedFlags(subset);
            var inComboNow = BuildInComboFlags(player, subset);
            return new AutoPlaySortContext(originalIndex, roles, manaGains, drawScores, manaCosts, comboCosts, evolved, inComboNow, null);
        }

        private static AutoPlaySortContext BuildContextFromTurnFacts(List<CardModel> subset)
        {
            var originalIndex = BuildOriginalIndex(subset);
            var roles = new Dictionary<CardModel, CardRole>(subset.Count);
            var manaGains = new Dictionary<CardModel, int>(subset.Count);
            var drawScores = new Dictionary<CardModel, int>(subset.Count);
            var manaCosts = new Dictionary<CardModel, int>(subset.Count);
            var comboCosts = new Dictionary<CardModel, int>(subset.Count);
            var evolved = new Dictionary<CardModel, bool>(subset.Count);
            var inComboNow = new Dictionary<CardModel, bool>(subset.Count);

            foreach (var card in subset)
            {
                if (card == null || roles.ContainsKey(card))
                    continue;

                TurnCardFacts facts;
                if (!TryGetTurnFacts(card, out facts))
                    facts = BuildFallbackTurnFacts(s_turnPreparedPlayer, card);

                roles[card] = facts.Role;
                manaGains[card] = facts.ManaGain;
                drawScores[card] = facts.DrawScore;
                manaCosts[card] = facts.ManaCost;
                comboCosts[card] = facts.ComboCost;
                evolved[card] = facts.Evolved;
                inComboNow[card] = facts.InComboNow;
            }

            return new AutoPlaySortContext(originalIndex, roles, manaGains, drawScores, manaCosts, comboCosts, evolved, inComboNow, s_turnComboPairs);
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

        private static int ComputeManaGain(CardModel card, Func<CardModel, int> getManaCost)
        {
            try
            {
                var config = card?.CardConfig;
                if (config == null)
                    return 0;

                var analysis = CardClassifier.GetCachedConfigAnalysis(config);
                int total = analysis.StaticManaGain;
                if (analysis.ManaEqualToCostCount > 0)
                    total += analysis.ManaEqualToCostCount * getManaCost(card);
                return total;
            }
            catch { return 0; }
        }

        private static int ComputeDrawScore(CardModel card)
        {
            try
            {
                var config = card?.CardConfig;
                if (config == null)
                    return 0;

                return CardClassifier.GetCachedConfigAnalysis(config).DrawScore;
            }
            catch { return 0; }
        }

        private static List<CardModel> BeamSearchOrder(PlayerModel player, List<CardModel> cards, AutoPlaySortContext context)
        {
            var startRemaining = new List<CardModel>(cards);
            var beam = new List<BeamState>();
            beam.Add(new BeamState(new List<CardModel>(), startRemaining, EvaluateBeamScore(player, new List<CardModel>(), startRemaining, context)));

            int targetCount = startRemaining.Count;
            int searchDepth = Math.Min(targetCount, MaxComboSearchDepth);
            for (int depth = 0; depth < searchDepth; depth++)
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
                bool leftCombo = CanComboAfter(left, previous, context);
                bool rightCombo = CanComboAfter(right, previous, context);
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
                if (SafeMana(GetManaCost(card, context)) > previousMana && CanComboAfter(card, previous, context))
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

                bool isHigherCombo = SafeMana(GetManaCost(card, context)) > previousMana && CanComboAfter(card, previous, context);
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
                score.ManaOrderScore += Math.Max(0, 30 - mana) * positionWeight * 90;
                if (ShouldPreferUtilityForSameManaNoClimb(previous, card, availableAtStep, context))
                    score.SameManaPenalty += 2200;

                if (i == 0)
                {
                    if (IsCurrentlyInCombo(player, card, context))
                        score.ComboScore += 3000;
                    score.LowStartScore += Math.Max(0, 50 - mana) * 120;
                }
                else if (CanComboAfter(card, previous, context))
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
                if (previous != null && !CanComboAfter(card, previous, context))
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
                case CardRole.ManaGenerator: score += 900; break;
                case CardRole.Utility:       score += 120; break;
                case CardRole.Crawler:       score += 120; break;
                case CardRole.Attack:        score += 120 + Math.Max(0, mana) * 60; break;
                default:                     score += 80; break;
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

                    if (CanComboAfter(card, last, context))
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
                bool leftCombo = CanComboAfter(left, previous, context);
                bool rightCombo = CanComboAfter(right, previous, context);
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
                if (SafeMana(GetManaCost(other, context)) > mana && CanComboAfter(other, card, context))
                    return true;
            }

            return false;
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

        private static bool CanComboAfter(CardModel card, CardModel previous, AutoPlaySortContext context)
        {
            if (card == null || previous == null) return false;
            if (context != null && context.ComboPairs != null)
            {
                var key = MakeComboPairKey(previous, card);
                if (key.PreviousPtr != 0 && key.CardPtr != 0 && context.ComboPairs.Contains(key))
                    return true;
            }
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

            // Free-to-play cards still participate in combo ladder based on base mana.
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

        private static void ResetTurnFacts()
        {
            s_turnPrepared = false;
            s_turnPreparedPlayer = null;
            s_turnFactsByPtr.Clear();
            s_turnComboPairs.Clear();
        }

        private static void PrepareTurnFacts(PlayerModel player)
        {
            if (player == null)
                return;

            var cards = CollectTurnCards(player);
            if (cards.Count == 0)
                return;

            s_turnPreparedPlayer = player;
            for (int i = 0; i < cards.Count; i++)
            {
                AddCardToTurnFacts(player, cards[i]);
            }

            s_turnPrepared = true;
            DevLog.Info("PrepareTurnFacts: cached " + s_turnFactsByPtr.Count + " cards, comboPairs=" + s_turnComboPairs.Count);
        }

        private static void AddCardToTurnFacts(PlayerModel player, CardModel card)
        {
            long ptr = GetCardPtr(card);
            if (ptr == 0)
                return;

            if (!s_turnFactsByPtr.ContainsKey(ptr))
                s_turnFactsByPtr[ptr] = BuildFallbackTurnFacts(player, card);

            var existingPtrs = new List<long>(s_turnFactsByPtr.Keys);
            for (int i = 0; i < existingPtrs.Count; i++)
            {
                long otherPtr = existingPtrs[i];
                if (otherPtr == 0 || otherPtr == ptr)
                    continue;

                CardModel otherCard = FindCardByPtr(player, otherPtr);
                if (otherCard == null)
                    continue;

                try
                {
                    if (card.CanCardCostTypeCombo(otherCard))
                        s_turnComboPairs.Add(MakeComboPairKey(otherPtr, ptr));
                }
                catch { }

                try
                {
                    if (otherCard.CanCardCostTypeCombo(card))
                        s_turnComboPairs.Add(MakeComboPairKey(ptr, otherPtr));
                }
                catch { }
            }
        }

        private static List<CardModel> CollectTurnCards(PlayerModel player)
        {
            var cards = new List<CardModel>();
            var seenPtrs = new HashSet<long>();
            if (player == null)
                return cards;

            for (int i = 0; i < s_turnPileMemberNames.Length; i++)
                AppendCardsFromMember(player, s_turnPileMemberNames[i], cards, seenPtrs);

            if (cards.Count == 0)
            {
                var fallback = LiveHandCache.Get();
                if (fallback != null)
                {
                    for (int i = 0; i < fallback.Count; i++)
                        AddUniqueCard(cards, seenPtrs, fallback[i]);
                }
            }

            return cards;
        }

        private static void AppendCardsFromMember(object target, string memberName, List<CardModel> cards, HashSet<long> seenPtrs)
        {
            object pile;
            if (!TryReadMemberValue(target, memberName, out pile) || pile == null)
                return;

            object cardPile;
            if (TryReadMemberValue(pile, "CardPile", out cardPile) && cardPile != null)
                AppendCardsFromPileObject(cardPile, cards, seenPtrs);
            else
                AppendCardsFromPileObject(pile, cards, seenPtrs);
        }

        private static void AppendCardsFromPileObject(object pile, List<CardModel> cards, HashSet<long> seenPtrs)
        {
            if (pile == null)
                return;

            try
            {
                Type type = pile.GetType();
                var countProp = type.GetProperty("Count");
                var tryPeekIndexMethod = type.GetMethod("TryPeekIndex");
                if (countProp != null && tryPeekIndexMethod != null)
                {
                    int count = Convert.ToInt32(countProp.GetValue(pile, null));
                    for (int i = 0; i < count; i++)
                    {
                        object[] args = new object[] { i, null };
                        bool ok = Convert.ToBoolean(tryPeekIndexMethod.Invoke(pile, args));
                        if (ok)
                            AddUniqueCard(cards, seenPtrs, args[1] as CardModel);
                    }
                    return;
                }
            }
            catch { }

            try
            {
                var managed = Il2CppListAdapter.ToManaged(pile);
                for (int i = 0; i < managed.Count; i++)
                    AddUniqueCard(cards, seenPtrs, managed[i]);
            }
            catch { }
        }

        private static void AddUniqueCard(List<CardModel> cards, HashSet<long> seenPtrs, CardModel card)
        {
            long ptr = GetCardPtr(card);
            if (ptr == 0 || !seenPtrs.Add(ptr))
                return;

            cards.Add(card);
        }

        private static bool TryReadMemberValue(object target, string memberName, out object value)
        {
            value = null;
            if (target == null || string.IsNullOrEmpty(memberName))
                return false;

            try
            {
                PropertyInfo property;
                FieldInfo field;
                if (!ReflectionCache.TryGetMemberAccessors(target.GetType(), memberName, out property, out field))
                    return false;

                if (property != null)
                {
                    value = property.GetValue(target, null);
                    return true;
                }

                if (field != null)
                {
                    value = field.GetValue(target);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetTurnFacts(CardModel card, out TurnCardFacts facts)
        {
            facts = default;
            long ptr = GetCardPtr(card);
            return ptr != 0 && s_turnFactsByPtr.TryGetValue(ptr, out facts);
        }

        private static TurnCardFacts BuildFallbackTurnFacts(PlayerModel player, CardModel card)
        {
            var facts = new TurnCardFacts();
            if (card == null)
                return facts;

            facts.Role = CardClassifier.Classify(card);
            facts.ManaGain = ComputeManaGain(card, ComputeRawManaCost);
            facts.DrawScore = ComputeDrawScore(card);
            facts.ManaCost = ComputeRawManaCost(card);
            facts.ComboCost = ComputeRawComboCost(card);
            try { facts.Evolved = card.IsEvolved; } catch { facts.Evolved = false; }
            facts.InComboNow = ComputeIsCurrentlyInCombo(player, card);
            return facts;
        }

        private static long GetCardPtr(CardModel card)
        {
            if (card == null)
                return 0;

            try { return card.Pointer.ToInt64(); }
            catch { return 0; }
        }

        private static CardModel FindCardByPtr(PlayerModel player, long ptr)
        {
            if (ptr == 0)
                return null;

            var cards = CollectTurnCards(player);
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (GetCardPtr(card) == ptr)
                    return card;
            }

            return null;
        }

        private static ComboPairKey MakeComboPairKey(CardModel previous, CardModel card)
        {
            return MakeComboPairKey(GetCardPtr(previous), GetCardPtr(card));
        }

        private static ComboPairKey MakeComboPairKey(long previousPtr, long cardPtr)
        {
            return new ComboPairKey(previousPtr, cardPtr);
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

        private static int SafeMana(int mana)
        {
            return mana == int.MaxValue ? 99 : mana;
        }

        private static string SafeName(CardModel card)
        {
            try { return card?.Name ?? "?"; }
            catch { return "?"; }
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
    }
}
