using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.Models;
using UnityEngine;

namespace BetterAutoPlay
{
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

                if (config.GetType().Name == "FccConfig")
                    return CardRole.Crawler;

                if (HasManaEffect(config))
                    return CardRole.ManaGenerator;

                if (IsWeaponCard(config))
                    return CardRole.Attack;

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

            return new CardDisplayTag(role.ToString(), RoleColor(role));
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
            catch { }

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
            if (normalized == "defence" || normalized == "defense") return "Armor";
            if (normalized == "attack")   return "Attack";
            if (normalized == "armor")    return "Armor";
            if (normalized == "mana")     return "Mana";
            if (normalized == "special")  return "Special";
            if (normalized == "character") return "Character";
            if (normalized == "debuff")   return "Debuff";
            if (normalized == "void")     return "Void";

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
            return normalized == "red"     || normalized == "blue"    || normalized == "green"  ||
                   normalized == "yellow"  || normalized == "purple"  || normalized == "orange" ||
                   normalized == "pink"    || normalized == "black"   || normalized == "white"  ||
                   normalized == "gray"    || normalized == "grey"    || normalized == "cyan"   ||
                   normalized == "magenta" || normalized == "violet"  || normalized == "brown"  ||
                   normalized == "gold"    || normalized == "silver";
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

        internal static string RoleColor(CardRole role)
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
}
