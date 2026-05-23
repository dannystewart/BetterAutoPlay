using System;
using System.Collections.Generic;
using System.Reflection;
using Nosebleed.Pancake.Models;
using UnityEngine;

namespace BetterAutoPlay
{
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

    internal static class ReflectionCache
    {
        private static readonly Dictionary<Type, PropertyInfo> s_countAccessorByType = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, MethodInfo> s_getItemAccessorByType = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<string, PropertyInfo> s_propertyAccessorByTypeAndName = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, FieldInfo> s_fieldAccessorByTypeAndName = new Dictionary<string, FieldInfo>();

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

        public static bool TryGetMemberAccessors(Type type, string memberName, out PropertyInfo property, out FieldInfo field)
        {
            property = null;
            field = null;
            if (type == null || string.IsNullOrEmpty(memberName))
                return false;

            string key = type.FullName + "|" + memberName;

            if (!s_propertyAccessorByTypeAndName.TryGetValue(key, out property))
            {
                property = type.GetProperty(memberName);
                s_propertyAccessorByTypeAndName[key] = property;
            }

            if (property != null)
                return true;

            if (!s_fieldAccessorByTypeAndName.TryGetValue(key, out field))
            {
                field = type.GetField(memberName);
                s_fieldAccessorByTypeAndName[key] = field;
            }

            return field != null;
        }
    }

    internal static class Il2CppListAdapter
    {
        public static List<CardModel> ToManaged(object il2CppList)
        {
            var result = new List<CardModel>();
            if (il2CppList == null)
                return result;

            PropertyInfo countProperty;
            MethodInfo getItemMethod;
            if (!ReflectionCache.TryGetListAccessors(il2CppList.GetType(), out countProperty, out getItemMethod))
                return result;

            int count = Convert.ToInt32(countProperty.GetValue(il2CppList, null));
            for (int i = 0; i < count; i++)
                result.Add(getItemMethod.Invoke(il2CppList, new object[] { i }) as CardModel);

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
