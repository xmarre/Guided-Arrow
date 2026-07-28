using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Diagnostic-only lifecycle trace for the remaining concentrated-volley failure.
    /// It does not suppress, defer, replace or mutate any Guided Arrow operation.
    /// Each marker is flushed synchronously so the final completed callback survives a hard crash.
    /// </summary>
    internal static class ConcentratedVolleyTracePatch
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath = Path.Combine(
            Path.GetDirectoryName(typeof(ConcentratedVolleyTracePatch).Assembly.Location) ?? string.Empty,
            "GuidedArrow-volley-trace.log");

        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static FieldInfo _cameraIndexField;
        private static FieldInfo _trackedField;
        private static FieldInfo _pendingContextsField;
        private static FieldInfo _earlyReactionsField;
        private static FieldInfo _pendingRemovalsField;
        private static FieldInfo _pendingVictimsField;
        private static FieldInfo _confirmedKillsField;
        private static FieldInfo _splitClosedField;

        private static PropertyInfo _settingsInstanceProperty;
        private static readonly Dictionary<string, FieldInfo> SettingsFields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        private static long _sequence;

        internal static void Install(Harmony harmony, Type behaviorType, Type settingsType)
        {
            if (harmony == null || behaviorType == null) return;

            _stateField = AccessTools.Field(behaviorType, "_state");
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _cameraIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _trackedField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _pendingContextsField = AccessTools.Field(behaviorType, "_pendingCollisionContexts");
            _earlyReactionsField = AccessTools.Field(behaviorType, "_earlyCollisionReactions");
            _pendingRemovalsField = AccessTools.Field(behaviorType, "_pendingNativeMissileRemovals");
            _pendingVictimsField = AccessTools.Field(behaviorType, "_pendingHitVictims");
            _confirmedKillsField = AccessTools.Field(behaviorType, "_confirmedCinematicKillCount");
            _splitClosedField = AccessTools.Field(behaviorType, "_splitSiblingAcquisitionClosed");

            ConfigureSettings(settingsType);
            ResetLog();
            MarkRaw("TRACE_INSTALL core=" + behaviorType.Assembly.GetName().Version + " progression=" + typeof(ConcentratedVolleyTracePatch).Assembly.GetName().Version);

            MethodInfo prefix = AccessTools.Method(typeof(ConcentratedVolleyTracePatch), nameof(TracePrefix));
            MethodInfo postfix = AccessTools.Method(typeof(ConcentratedVolleyTracePatch), nameof(TracePostfix));
            MethodInfo finalizer = AccessTools.Method(typeof(ConcentratedVolleyTracePatch), nameof(TraceFinalizer));
            if (prefix == null || postfix == null || finalizer == null) return;

            string[] names =
            {
                "OnAgentShootMissile",
                "StartGuidedShot",
                "CloseSplitSiblingAcquisition",
                "OnMissileCollisionReaction",
                "ResolveCollisionReaction",
                "OnMissileHit",
                "QueuePendingCollisionContext",
                "QueueEarlyCollisionReaction",
                "TryConsumeEarlyCollisionReaction",
                "RemovePendingCollisionContext",
                "QueueNativeMissileRemoval",
                "FlushPendingNativeMissileRemovals",
                "RemoveTrackedMissile",
                "OnMissileRemoved",
                "TrackHitVictim",
                "HandleConfirmedKill",
                "TryPromoteCameraOwnerWithinSwarm",
                "SuspendProjectileCameraForCollisionReaction",
                "HandleGuidedSwarmTerminal",
                "BeginReturn",
                "BeginCinematic",
                "BeginCinematicKill",
                "TickCinematic",
                "OnPreDisplayMissionTick",
                "ResetAll"
            };

            foreach (string name in names)
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == name && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(prefix) { priority = int.MaxValue },
                            postfix: new HarmonyMethod(postfix) { priority = int.MinValue },
                            finalizer: new HarmonyMethod(finalizer) { priority = int.MinValue });
                    }
                    catch (Exception exception)
                    {
                        MarkRaw("PATCH_FAILED " + Describe(method) + " " + exception.GetType().Name);
                    }
                }
            }
        }

        private static void ConfigureSettings(Type settingsType)
        {
            SettingsFields.Clear();
            _settingsInstanceProperty = null;
            if (settingsType == null) return;

            try
            {
                Type closed = typeof(MCM.Abstractions.Base.Global.GlobalSettings<>).MakeGenericType(settingsType);
                _settingsInstanceProperty = closed.GetProperty(
                    "Instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

                foreach (FieldInfo field in settingsType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string name = field.Name;
                    if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                        name = name.Substring(1, name.Length - 17);
                    if (!SettingsFields.ContainsKey(name)) SettingsFields.Add(name, field);
                }
            }
            catch { }
        }

        private static void TracePrefix(object __instance, MethodBase __originalMethod, object[] __args, out long __state)
        {
            __state = Interlocked.Increment(ref _sequence);
            MarkRaw("ENTER #" + __state + " " + Describe(__originalMethod) + " " + DescribeState(__instance) + " " + DescribeArguments(__args));
        }

        private static void TracePostfix(object __instance, MethodBase __originalMethod, long __state)
        {
            MarkRaw("EXIT #" + __state + " " + Describe(__originalMethod) + " " + DescribeState(__instance));
        }

        private static Exception TraceFinalizer(Exception __exception, object __instance, MethodBase __originalMethod, long __state)
        {
            if (__exception != null)
            {
                MarkRaw("EXCEPTION #" + __state + " " + Describe(__originalMethod) + " " +
                        __exception.GetType().FullName + " " + DescribeState(__instance));
            }
            return __exception;
        }

        private static string DescribeState(object instance)
        {
            if (instance == null) return "instance=null";

            return "state=" + ReadInt(_stateField, instance, -1) +
                   " gen=" + ReadInt(_generationField, instance, -1) +
                   " camera=" + ReadInt(_cameraIndexField, instance, -1) +
                   " tracked=" + ReadCount(_trackedField, instance) +
                   " contexts=" + ReadCount(_pendingContextsField, instance) +
                   " early=" + ReadCount(_earlyReactionsField, instance) +
                   " removals=" + ReadCount(_pendingRemovalsField, instance) +
                   " victims=" + ReadCount(_pendingVictimsField, instance) +
                   " kills=" + ReadInt(_confirmedKillsField, instance, -1) +
                   " splitClosed=" + ReadBool(_splitClosedField, instance) +
                   " settings=" + DescribeSettings();
        }

        private static string DescribeSettings()
        {
            object settings;
            try { settings = _settingsInstanceProperty?.GetValue(null, null); }
            catch { settings = null; }
            if (settings == null) return "unavailable";

            return "enabled:" + ReadSetting(settings, "Enabled") +
                   ",split:" + ReadSetting(settings, "EnableStandaloneSplitProjectiles") +
                   ",splitCount:" + ReadSetting(settings, "StandaloneSplitProjectileCount") +
                   ",penetration:" + ReadSetting(settings, "EnablePenetrationOverride") +
                   ",penetrations:" + ReadSetting(settings, "MaximumAgentPenetrations") +
                   ",infinite:" + ReadSetting(settings, "InfiniteAgentPenetrations") +
                   ",autoScope:" + ReadSetting(settings, "AutoguidanceScope");
        }

        private static string ReadSetting(object settings, string name)
        {
            if (settings == null || !SettingsFields.TryGetValue(name, out FieldInfo field)) return "?";
            try
            {
                object value = field.GetValue(settings);
                return value == null ? "null" : value.ToString();
            }
            catch { return "!"; }
        }

        private static string DescribeArguments(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0) return "args=none";

            try
            {
                string[] result = new string[arguments.Length];
                for (int i = 0; i < arguments.Length; i++)
                {
                    object value = arguments[i];
                    if (value == null)
                    {
                        result[i] = i + ":null";
                    }
                    else if (value is AttackCollisionData collision)
                    {
                        result[i] = i + ":collision(index=" + collision.AffectorWeaponSlotOrMissileIndex +
                                    ",missile=" + collision.IsMissile + ")";
                    }
                    else if (value.GetType().Name == "MissileCollisionReaction")
                    {
                        result[i] = i + ":reaction=" + value;
                    }
                    else if (value is Agent agent)
                    {
                        result[i] = i + ":Agent#" + RuntimeHelpers.GetHashCode(agent);
                    }
                    else if (value is int integer)
                    {
                        result[i] = i + ":int=" + integer;
                    }
                    else if (value is bool boolean)
                    {
                        result[i] = i + ":bool=" + boolean;
                    }
                    else
                    {
                        result[i] = i + ":" + value.GetType().Name + DescribeTrackedIndex(value);
                    }
                }
                return string.Join(" ", result);
            }
            catch
            {
                return "args=unavailable";
            }
        }

        private static string DescribeTrackedIndex(object value)
        {
            if (value == null || value.GetType().Name != "TrackedMissile") return string.Empty;
            try
            {
                FieldInfo index = AccessTools.Field(value.GetType(), "Index");
                return index == null ? string.Empty : "(index=" + index.GetValue(value) + ")";
            }
            catch { return string.Empty; }
        }

        private static int ReadCount(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return -1;
            try
            {
                object value = field.GetValue(instance);
                if (value is ICollection collection) return collection.Count;
                PropertyInfo count = value?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object result = count?.GetValue(value, null);
                return result is int integer ? integer : -1;
            }
            catch { return -1; }
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }

        private static string ReadBool(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return "?";
            try { return ((bool)field.GetValue(instance)).ToString(); }
            catch { return "!"; }
        }

        private static string Describe(MethodBase method)
        {
            if (method == null) return "method=null";
            return (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
        }

        private static void MarkRaw(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return;
            try
            {
                lock (Sync)
                {
                    using (FileStream stream = new FileStream(
                        LogPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        8192,
                        FileOptions.WriteThrough))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.Write(DateTime.UtcNow.ToString("O"));
                        writer.Write(" | thread=");
                        writer.Write(Thread.CurrentThread.ManagedThreadId);
                        writer.Write(" | ");
                        writer.WriteLine(marker);
                        writer.Flush();
                        stream.Flush(true);
                    }
                }
            }
            catch { }
        }

        private static void ResetLog()
        {
            try
            {
                lock (Sync)
                {
                    File.WriteAllText(
                        LogPath,
                        "Guided Arrow concentrated-volley trace - UTC - diagnostic only" + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
