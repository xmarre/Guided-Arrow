using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps native/TOR ability missiles on their own collision and penetration path.
    /// Guided Arrow's synthetic continuation system remains available for the added
    /// standalone followers and for ordinary single-projectile shots.
    /// </summary>
    internal static class NativeVolleyPenetrationIsolationPatch
    {
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _nativeSplitBatchDetectedField;
        private static FieldInfo _trackedSyntheticField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo spawnMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            if (spawnMethod == null) return;

            Type trackedType = spawnMethod.GetParameters()[0].ParameterType;
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _nativeSplitBatchDetectedField = AccessTools.Field(behaviorType, "_nativeSplitBatchDetected");
            _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
            if (_trackedMissilesField == null || _trackedSyntheticField == null) return;

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(NativeVolleyPenetrationIsolationPatch), nameof(Prefix)))
                {
                    priority = Priority.First
                };
                harmony.Patch(spawnMethod, prefix: prefix);
            }
            catch
            {
                // A changed core layout leaves the verified runtime untouched.
            }
        }

        private static bool Prefix(object __instance, object[] __args, ref bool __result)
        {
            if (__instance == null || __args == null || __args.Length == 0 || __args[0] == null)
                return true;

            object source = __args[0];
            if (IsSynthetic(source)) return true;
            if (!IsNativeVolleyActive(__instance)) return true;

            // The source is one of the original native ability projectiles. Do not create
            // a Guided Arrow custom continuation for it: TOR/native code retains ownership
            // of its magic, explosion and penetration behaviour.
            __result = false;
            return false;
        }

        private static bool IsNativeVolleyActive(object instance)
        {
            if (_nativeSplitBatchDetectedField != null)
            {
                try
                {
                    if ((bool)_nativeSplitBatchDetectedField.GetValue(instance)) return true;
                }
                catch { }
            }

            IList tracked;
            try { tracked = _trackedMissilesField.GetValue(instance) as IList; }
            catch { return false; }
            if (tracked == null) return false;

            int nativeCount = 0;
            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item == null || IsSynthetic(item)) continue;
                nativeCount++;
                if (nativeCount >= 2) return true;
            }
            return false;
        }

        private static bool IsSynthetic(object tracked)
        {
            try { return tracked != null && (bool)_trackedSyntheticField.GetValue(tracked); }
            catch { return false; }
        }
    }
}
