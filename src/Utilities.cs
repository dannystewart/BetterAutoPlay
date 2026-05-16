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
