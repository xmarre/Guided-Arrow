using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow;

internal static class MissileDamageBridge
{
	internal sealed class ResolvedLaunchData
	{
		public WeaponData WeaponData;

		public WeaponStatsData[] WeaponStatsData;

		public float DamageBonus;

		public float BaseSpeed;

		public Agent Shooter;

		public ResolvedLaunchData Clone()
		{
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			return new ResolvedLaunchData
			{
				WeaponData = WeaponData,
				WeaponStatsData = ((WeaponStatsData != null) ? ((WeaponStatsData[])WeaponStatsData.Clone()) : null),
				DamageBonus = DamageBonus,
				BaseSpeed = BaseSpeed,
				Shooter = Shooter
			};
		}
	}

	private sealed class RecentLaunch
	{
		public int MissileIndex;

		public Agent Shooter;

		public Vec3 Position;

		public Vec3 Direction;

		public long CapturedTimestamp;

		public ResolvedLaunchData Data;
	}

	private sealed class MissionLaunchState
	{
		public readonly object Sync = new object();

		public readonly Dictionary<int, ResolvedLaunchData> ByMissileIndex = new Dictionary<int, ResolvedLaunchData>();

		public readonly List<RecentLaunch> Recent = new List<RecentLaunch>();
	}

	private sealed class OverrideRequest
	{
		public Mission Mission;

		public Agent Shooter;

		public ResolvedLaunchData Data;

		public bool Consumed;
	}

	private sealed class OverrideScope : IDisposable
	{
		private readonly OverrideRequest _request;

		private readonly OverrideRequest _previous;

		private bool _disposed;

		public OverrideScope(OverrideRequest request, OverrideRequest previous)
		{
			_request = request;
			_previous = previous;
		}

		public void Dispose()
		{
			if (!_disposed)
			{
				_disposed = true;
				if (_activeOverride == _request)
				{
					_activeOverride = _previous;
				}
			}
		}
	}

	private sealed class CaptureState
	{
		public Agent Shooter;

		public Vec3 Position;

		public Vec3 Direction;
	}

	private static readonly ConditionalWeakTable<Mission, MissionLaunchState> States = new ConditionalWeakTable<Mission, MissionLaunchState>();

	[ThreadStatic]
	private static OverrideRequest _activeOverride;

	private static Harmony _harmony;

	private static bool _installAttempted;

	private static bool _installed;

	private static string _installFailure;

	internal static bool IsInstalled => _installed;

	internal static string InstallFailure => _installFailure ?? string.Empty;

	internal static void Install()
	{
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Expected O, but got Unknown
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_016e: Expected O, but got Unknown
		//IL_016e: Expected O, but got Unknown
		//IL_0177: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Expected O, but got Unknown
		//IL_018a: Expected O, but got Unknown
		if (_installAttempted)
		{
			return;
		}
		_installAttempted = true;
		try
		{
			MethodInfo[] methods = typeof(Mission).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo methodInfo = methods.FirstOrDefault(IsSupportedAddMissileAux);
			MethodInfo methodInfo2 = methods.FirstOrDefault(IsSupportedAddMissileSingleUsageAux);
			if (methodInfo == null)
			{
				throw new MissingMethodException(typeof(Mission).FullName, "AddMissileAux");
			}
			if (methodInfo2 == null)
			{
				throw new MissingMethodException(typeof(Mission).FullName, "AddMissileSingleUsageAux");
			}
			MethodInfo method = typeof(MissileDamageBridge).GetMethod("AddMissileAuxPrefix", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo method2 = typeof(MissileDamageBridge).GetMethod("AddMissileAuxPostfix", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo method3 = typeof(MissileDamageBridge).GetMethod("AddMissileSingleUsageAuxPrefix", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo method4 = typeof(MissileDamageBridge).GetMethod("AddMissileSingleUsageAuxPostfix", BindingFlags.Static | BindingFlags.NonPublic);
			if (method == null || method2 == null || method3 == null || method4 == null)
			{
				throw new MissingMethodException(typeof(MissileDamageBridge).FullName, "Harmony patch methods");
			}
			_harmony = new Harmony("guidedarrow.resolvedmissiledamage");
			_harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(method), new HarmonyMethod(method2), (HarmonyMethod)null, (HarmonyMethod)null);
			_harmony.Patch((MethodBase)methodInfo2, new HarmonyMethod(method3), new HarmonyMethod(method4), (HarmonyMethod)null, (HarmonyMethod)null);
			_installed = true;
		}
		catch (Exception ex)
		{
			try
			{
				if (_harmony != null)
				{
					_harmony.UnpatchAll(_harmony.Id);
				}
			}
			catch
			{
			}
			_harmony = null;
			_installed = false;
			_installFailure = ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static bool IsSupportedAddMissileAux(MethodInfo method)
	{
		if (method == null || method.Name != "AddMissileAux" || method.ReturnType != typeof(int))
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length >= 15 && parameters[2].ParameterType == typeof(Agent) && parameters[3].ParameterType == typeof(WeaponData).MakeByRefType() && parameters[4].ParameterType == typeof(WeaponStatsData[]) && parameters[5].ParameterType == typeof(float))
		{
			return parameters[9].ParameterType == typeof(float);
		}
		return false;
	}

	private static bool IsSupportedAddMissileSingleUsageAux(MethodInfo method)
	{
		if (method == null || method.Name != "AddMissileSingleUsageAux" || method.ReturnType != typeof(int))
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length >= 15 && parameters[2].ParameterType == typeof(Agent) && parameters[3].ParameterType == typeof(WeaponData).MakeByRefType() && parameters[4].ParameterType == typeof(WeaponStatsData).MakeByRefType() && parameters[5].ParameterType == typeof(float))
		{
			return parameters[9].ParameterType == typeof(float);
		}
		return false;
	}

	private static CaptureState CaptureLaunchState(Agent shooter, Vec3 position, Vec3 direction)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		return new CaptureState
		{
			Shooter = shooter,
			Position = position,
			Direction = direction
		};
	}

	private static bool TryApplyOverride(Mission mission, Agent shooter, ref WeaponData weaponData, ref WeaponStatsData[] weaponStatsData, ref float damageBonus, ref float baseSpeed)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		OverrideRequest activeOverride = _activeOverride;
		if (activeOverride == null || activeOverride.Consumed || activeOverride.Data == null || activeOverride.Mission != mission || activeOverride.Shooter != shooter)
		{
			return false;
		}
		ResolvedLaunchData data = activeOverride.Data;
		if (data.WeaponStatsData == null || data.WeaponStatsData.Length == 0)
		{
			return false;
		}
		weaponData = data.WeaponData;
		weaponStatsData = (WeaponStatsData[])data.WeaponStatsData.Clone();
		damageBonus = data.DamageBonus;
		baseSpeed = data.BaseSpeed;
		activeOverride.Consumed = true;
		return true;
	}

	private static bool TryApplySingleUsageOverride(Mission mission, Agent shooter, ref WeaponData weaponData, ref WeaponStatsData weaponStatsData, ref float damageBonus, ref float baseSpeed)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		OverrideRequest activeOverride = _activeOverride;
		if (activeOverride == null || activeOverride.Consumed || activeOverride.Data == null || activeOverride.Mission != mission || activeOverride.Shooter != shooter)
		{
			return false;
		}
		ResolvedLaunchData data = activeOverride.Data;
		if (data.WeaponStatsData == null || data.WeaponStatsData.Length == 0)
		{
			return false;
		}
		weaponData = data.WeaponData;
		weaponStatsData = data.WeaponStatsData[0];
		damageBonus = data.DamageBonus;
		baseSpeed = data.BaseSpeed;
		activeOverride.Consumed = true;
		return true;
	}

	private static void AddMissileAuxPrefix(Mission __instance, Agent __2, ref WeaponData __3, ref WeaponStatsData[] __4, ref float __5, ref Vec3 __6, ref Vec3 __7, ref float __9, out CaptureState __state)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		__state = CaptureLaunchState(__2, __6, __7);
		if (__instance != null)
		{
			TryApplyOverride(__instance, __2, ref __3, ref __4, ref __5, ref __9);
		}
	}

	private static void AddMissileAuxPostfix(Mission __instance, int __result, Agent __2, ref WeaponData __3, WeaponStatsData[] __4, float __5, float __9, CaptureState __state)
	{
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		if (__instance == null || __result < 0)
		{
			return;
		}
		try
		{
			if (__4 != null && __4.Length != 0 && !float.IsNaN(__5) && !float.IsInfinity(__5) && !float.IsNaN(__9) && !float.IsInfinity(__9) && !(__9 <= 0f))
			{
				StoreResolvedLaunch(__instance, __result, __2, __3, __4, __5, __9, __state);
			}
		}
		catch
		{
		}
	}

	private static void AddMissileSingleUsageAuxPrefix(Mission __instance, Agent __2, ref WeaponData __3, ref WeaponStatsData __4, ref float __5, ref Vec3 __6, ref Vec3 __7, ref float __9, out CaptureState __state)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		__state = CaptureLaunchState(__2, __6, __7);
		if (__instance != null)
		{
			TryApplySingleUsageOverride(__instance, __2, ref __3, ref __4, ref __5, ref __9);
		}
	}

	private static void AddMissileSingleUsageAuxPostfix(Mission __instance, int __result, Agent __2, ref WeaponData __3, ref WeaponStatsData __4, float __5, float __9, CaptureState __state)
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		if (__instance == null || __result < 0)
		{
			return;
		}
		try
		{
			if (!float.IsNaN(__5) && !float.IsInfinity(__5) && !float.IsNaN(__9) && !float.IsInfinity(__9) && !(__9 <= 0f))
			{
				StoreResolvedLaunch(__instance, __result, __2, __3, (WeaponStatsData[])(object)new WeaponStatsData[1] { __4 }, __5, __9, __state);
			}
		}
		catch
		{
		}
	}

	private static void StoreResolvedLaunch(Mission mission, int missileIndex, Agent shooter, WeaponData weaponData, WeaponStatsData[] weaponStatsData, float damageBonus, float baseSpeed, CaptureState captureState)
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		if (mission == null || missileIndex < 0 || weaponStatsData == null || weaponStatsData.Length == 0)
		{
			return;
		}
		MissionLaunchState orCreateValue = States.GetOrCreateValue(mission);
		ResolvedLaunchData resolvedLaunchData = new ResolvedLaunchData
		{
			WeaponData = weaponData,
			WeaponStatsData = (WeaponStatsData[])weaponStatsData.Clone(),
			DamageBonus = damageBonus,
			BaseSpeed = baseSpeed,
			Shooter = shooter
		};
		RecentLaunch item = new RecentLaunch
		{
			MissileIndex = missileIndex,
			Shooter = shooter,
			Position = (captureState?.Position ?? Vec3.Zero),
			Direction = (captureState?.Direction ?? Vec3.Zero),
			CapturedTimestamp = Stopwatch.GetTimestamp(),
			Data = resolvedLaunchData
		};
		lock (orCreateValue.Sync)
		{
			orCreateValue.ByMissileIndex[missileIndex] = resolvedLaunchData;
			orCreateValue.Recent.Add(item);
			while (orCreateValue.Recent.Count > 64)
			{
				orCreateValue.Recent.RemoveAt(0);
			}
		}
	}

	internal static bool TryGetResolvedLaunch(Mission mission, int missileIndex, Agent expectedShooter, out ResolvedLaunchData data)
	{
		data = null;
		if (!_installed || mission == null || missileIndex < 0 || !States.TryGetValue(mission, out var value))
		{
			return false;
		}
		lock (value.Sync)
		{
			if (!value.ByMissileIndex.TryGetValue(missileIndex, out var value2) || value2 == null || (expectedShooter != null && value2.Shooter != null && expectedShooter != value2.Shooter))
			{
				return false;
			}
			data = value2.Clone();
			return true;
		}
	}

	internal static bool TryGetResolvedLaunchForShot(Mission mission, int missileIndex, Agent expectedShooter, Vec3 expectedLaunchPosition, Vec3 expectedLaunchVelocity, out ResolvedLaunchData data)
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0148: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
		if (TryGetResolvedLaunch(mission, missileIndex, expectedShooter, out data))
		{
			return true;
		}
		data = null;
		if (!_installed || mission == null || expectedShooter == null || !States.TryGetValue(mission, out var value))
		{
			return false;
		}
		Vec3 val = NormalizeSafe(expectedLaunchVelocity);
		bool flag = IsFinite(val) && ((Vec3)(ref val)).LengthSquared > 0.5f;
		bool flag2 = IsFinite(expectedLaunchPosition);
		long timestamp = Stopwatch.GetTimestamp();
		RecentLaunch recentLaunch = null;
		double num = double.MaxValue;
		lock (value.Sync)
		{
			for (int num2 = value.Recent.Count - 1; num2 >= 0; num2--)
			{
				RecentLaunch recentLaunch2 = value.Recent[num2];
				if (recentLaunch2 == null || recentLaunch2.Data == null || recentLaunch2.Shooter != expectedShooter)
				{
					continue;
				}
				double num3 = ((recentLaunch2.CapturedTimestamp > 0) ? ((double)(timestamp - recentLaunch2.CapturedTimestamp) / (double)Stopwatch.Frequency) : double.MaxValue);
				if (double.IsNaN(num3) || double.IsInfinity(num3) || num3 < -0.05 || num3 > 1.5)
				{
					continue;
				}
				double num4 = Math.Max(0.0, num3) * 8.0;
				if (flag2 && IsFinite(recentLaunch2.Position))
				{
					Vec3 val2 = recentLaunch2.Position - expectedLaunchPosition;
					float lengthSquared = ((Vec3)(ref val2)).LengthSquared;
					if (!IsFinite(lengthSquared) || lengthSquared > 400f)
					{
						continue;
					}
					num4 += Math.Sqrt(Math.Max(0f, lengthSquared)) * 0.25;
				}
				if (flag)
				{
					Vec3 val3 = NormalizeSafe(recentLaunch2.Direction);
					if (!IsFinite(val3) || ((Vec3)(ref val3)).LengthSquared <= 0.5f)
					{
						continue;
					}
					float num5 = val.x * val3.x + val.y * val3.y + val.z * val3.z;
					if (!IsFinite(num5) || num5 < 0.9f)
					{
						continue;
					}
					num4 += (1.0 - Math.Min(1.0, num5)) * 25.0;
				}
				if (recentLaunch2.MissileIndex == missileIndex)
				{
					num4 -= 100.0;
				}
				if (!(num4 >= num))
				{
					recentLaunch = recentLaunch2;
					num = num4;
				}
			}
			if (recentLaunch == null)
			{
				return false;
			}
			data = recentLaunch.Data.Clone();
			value.ByMissileIndex[missileIndex] = recentLaunch.Data.Clone();
			return true;
		}
	}

	private static Vec3 NormalizeSafe(Vec3 value)
	{
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		float lengthSquared = ((Vec3)(ref value)).LengthSquared;
		if (!IsFinite(lengthSquared) || lengthSquared <= 1E-06f)
		{
			return Vec3.Zero;
		}
		return value / (float)Math.Sqrt(lengthSquared);
	}

	private static bool IsFinite(float value)
	{
		if (!float.IsNaN(value))
		{
			return !float.IsInfinity(value);
		}
		return false;
	}

	private static bool IsFinite(Vec3 value)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		if (IsFinite(value.x) && IsFinite(value.y))
		{
			return IsFinite(value.z);
		}
		return false;
	}

	internal static IDisposable OverrideNextSyntheticMissile(Mission mission, Agent shooter, ResolvedLaunchData data)
	{
		if (!_installed || mission == null || shooter == null || data == null)
		{
			return null;
		}
		OverrideRequest activeOverride = _activeOverride;
		return new OverrideScope(_activeOverride = new OverrideRequest
		{
			Mission = mission,
			Shooter = shooter,
			Data = data.Clone(),
			Consumed = false
		}, activeOverride);
	}

	internal static void Forget(Mission mission, int missileIndex)
	{
		if (mission == null || missileIndex < 0 || !States.TryGetValue(mission, out var value))
		{
			return;
		}
		lock (value.Sync)
		{
			value.ByMissileIndex.Remove(missileIndex);
		}
	}

	internal static void ClearMission(Mission mission)
	{
		if (mission == null || !States.TryGetValue(mission, out var value))
		{
			return;
		}
		lock (value.Sync)
		{
			value.ByMissileIndex.Clear();
			value.Recent.Clear();
		}
	}
}
