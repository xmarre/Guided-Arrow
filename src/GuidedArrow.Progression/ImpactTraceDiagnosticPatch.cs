using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Diagnostic-only tracing for the binary core's impact path.
    ///
    /// Every marker is synchronously flushed to disk so the final completed operation survives a
    /// native access violation or hard process termination. This patch does not suppress, defer or
    /// otherwise change Guided Arrow behaviour.
    /// </summary>
    internal static class ImpactTraceDiagnosticPatch
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath = Path.Combine(
            Path.GetDirectoryName(typeof(ImpactTraceDiagnosticPatch).Assembly.Location) ?? string.Empty,
            "GuidedArrow-impact-trace.log");

        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static FieldInfo _cameraMissileIndexField;
        private static FieldInfo _trackedMissilesField;
        private static MethodInfo _fatalDamageGetter;

        [ThreadStatic]
        private static int _impactDepth;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _stateField = AccessTools.Field(behaviorType, "_state");
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _fatalDamageGetter = AccessTools.PropertyGetter(typeof(AttackCollisionData), "IsFatalDamage");

            ResetLog();
            Mark("TRACE_INSTALL core=" + behaviorType.Assembly.GetName().Version);

            MethodInfo impactPrefix = AccessTools.Method(typeof(ImpactTraceDiagnosticPatch), nameof(ImpactPrefix));
            MethodInfo impactPostfix = AccessTools.Method(typeof(ImpactTraceDiagnosticPatch), nameof(ImpactPostfix));
            MethodInfo impactFinalizer = AccessTools.Method(typeof(ImpactTraceDiagnosticPatch), nameof(ImpactFinalizer));
            MethodInfo callPrefix = AccessTools.Method(typeof(ImpactTraceDiagnosticPatch), nameof(CallPrefix));
            MethodInfo callPostfix = AccessTools.Method(typeof(ImpactTraceDiagnosticPatch), nameof(CallPostfix));
            MethodInfo callFinalizer = AccessTools.Method(typeof(ImpactTraceDiagnosticPatch), nameof(CallFinalizer));

            MethodInfo impactMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "OnMissileHit" &&
                    !method.IsAbstract &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(AttackCollisionData)));

            if (impactMethod != null && impactPrefix != null && impactPostfix != null && impactFinalizer != null)
            {
                TryPatch(
                    harmony,
                    impactMethod,
                    new HarmonyMethod(impactPrefix) { priority = int.MaxValue },
                    new HarmonyMethod(impactPostfix) { priority = int.MinValue },
                    new HarmonyMethod(impactFinalizer) { priority = int.MinValue });
            }

            string[] tracedBehaviorMethods =
            {
                "CloseSplitSiblingAcquisition",
                "FindTrackedMissile",
                "AbandonNativePresentationHandlesAfterImpact",
                "QueuePendingCollisionContext",
                "TryPromoteCameraOwnerWithinSwarm",
                "SuspendProjectileCameraForCollisionReaction",
                "TrackHitVictim",
                "HandleConfirmedKill",
                "TryConsumeEarlyCollisionReaction",
                "RemovePendingCollisionContext",
                "RemoveTrackedMissile",
                "HandleGuidedSwarmTerminal",
                "UpdateCrosshairVisibility",
                "OnMissileCollisionReaction",
                "ResolveCollisionReaction",
                "OnMissileRemoved",
                "OnAgentHit"
            };

            if (callPrefix != null && callPostfix != null && callFinalizer != null)
            {
                foreach (string name in tracedBehaviorMethods)
                {
                    foreach (MethodInfo method in behaviorType
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(candidate => candidate.Name == name && !candidate.IsAbstract))
                    {
                        TryPatch(
                            harmony,
                            method,
                            new HarmonyMethod(callPrefix) { priority = int.MaxValue },
                            new HarmonyMethod(callPostfix) { priority = int.MinValue },
                            new HarmonyMethod(callFinalizer) { priority = int.MinValue });
                    }
                }
            }

            PatchGetter(harmony, typeof(Agent), nameof(Agent.Health), callPrefix, callPostfix, callFinalizer);

            Type concreteMissileType = typeof(MBMissile).Assembly.GetType("Missile", false);
            if (concreteMissileType != null)
                PatchGetter(harmony, concreteMissileType, "Entity", callPrefix, callPostfix, callFinalizer);
        }

        private static void PatchGetter(
            Harmony harmony,
            Type type,
            string propertyName,
            MethodInfo prefix,
            MethodInfo postfix,
            MethodInfo finalizer)
        {
            MethodInfo getter = type == null ? null : AccessTools.PropertyGetter(type, propertyName);
            if (getter == null || prefix == null || postfix == null || finalizer == null) return;

            TryPatch(
                harmony,
                getter,
                new HarmonyMethod(prefix) { priority = int.MaxValue },
                new HarmonyMethod(postfix) { priority = int.MinValue },
                new HarmonyMethod(finalizer) { priority = int.MinValue });
        }

        private static void TryPatch(
            Harmony harmony,
            MethodBase original,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            HarmonyMethod finalizer)
        {
            try
            {
                harmony.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            catch (Exception exception)
            {
                Mark("PATCH_FAILED " + Describe(original) + " " + exception.GetType().Name);
            }
        }

        private static void ImpactPrefix(object __instance, object[] __args)
        {
            _impactDepth++;
            Mark("IMPACT_ENTER " + DescribeState(__instance) + " " + DescribeArguments(__args));
        }

        private static void ImpactPostfix(object __instance)
        {
            Mark("IMPACT_EXIT " + DescribeState(__instance));
        }

        private static Exception ImpactFinalizer(Exception __exception, object __instance)
        {
            if (__exception != null)
                Mark("IMPACT_EXCEPTION " + __exception.GetType().FullName + " " + DescribeState(__instance));

            if (_impactDepth > 0) _impactDepth--;
            return __exception;
        }

        private static void CallPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (_impactDepth <= 0 && !IsOuterCallback(__originalMethod)) return;
            Mark("ENTER " + Describe(__originalMethod) + " " + DescribeArguments(__args));
        }

        private static void CallPostfix(MethodBase __originalMethod)
        {
            if (_impactDepth <= 0 && !IsOuterCallback(__originalMethod)) return;
            Mark("EXIT " + Describe(__originalMethod));
        }

        private static Exception CallFinalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null && (_impactDepth > 0 || IsOuterCallback(__originalMethod)))
                Mark("EXCEPTION " + Describe(__originalMethod) + " " + __exception.GetType().FullName);
            return __exception;
        }

        private static bool IsOuterCallback(MethodBase method)
        {
            if (method == null) return false;
            return method.Name == "OnMissileCollisionReaction" ||
                   method.Name == "OnMissileRemoved" ||
                   method.Name == "OnAgentHit";
        }

        private static string DescribeState(object instance)
        {
            if (instance == null) return "instance=null";

            int state = ReadInt(_stateField, instance, -1);
            int generation = ReadInt(_generationField, instance, -1);
            int cameraIndex = ReadInt(_cameraMissileIndexField, instance, -1);
            int trackedCount = -1;

            try
            {
                if (_trackedMissilesField?.GetValue(instance) is ICollection collection)
                    trackedCount = collection.Count;
            }
            catch { }

            return "state=" + state +
                   " generation=" + generation +
                   " camera=" + cameraIndex +
                   " tracked=" + trackedCount;
        }

        private static string DescribeArguments(object[] arguments)
        {
            if (arguments == null) return "args=null";

            try
            {
                string[] values = new string[arguments.Length];
                for (int i = 0; i < arguments.Length; i++)
                {
                    object value = arguments[i];
                    if (value == null)
                    {
                        values[i] = i + ":null";
                        continue;
                    }

                    if (value is AttackCollisionData collision)
                    {
                        values[i] = i + ":collision(index=" +
                                    collision.AffectorWeaponSlotOrMissileIndex +
                                    ",missile=" + collision.IsMissile +
                                    ",fatal=" + ReadFatalDamage(collision) + ")";
                        continue;
                    }

                    if (value is int integer)
                    {
                        values[i] = i + ":int=" + integer;
                        continue;
                    }

                    values[i] = i + ":" + value.GetType().Name;
                }

                return string.Join(" ", values);
            }
            catch
            {
                return "args=unavailable";
            }
        }

        private static bool ReadFatalDamage(AttackCollisionData collision)
        {
            try
            {
                object boxed = collision;
                object result = _fatalDamageGetter?.Invoke(boxed, null);
                return result is bool fatal && fatal;
            }
            catch
            {
                return false;
            }
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }

        private static string Describe(MethodBase method)
        {
            if (method == null) return "method=null";
            return (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
        }

        internal static void Mark(string marker)
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
                        4096,
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
            catch
            {
                // Diagnostic logging must never affect gameplay.
            }
        }

        private static void ResetLog()
        {
            try
            {
                lock (Sync)
                {
                    File.WriteAllText(
                        LogPath,
                        "Guided Arrow impact trace - UTC - diagnostic only" + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
