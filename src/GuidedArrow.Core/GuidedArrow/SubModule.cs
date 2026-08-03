using TaleWorlds.MountAndBlade;

namespace GuidedArrow;

public sealed class SubModule : MBSubModuleBase
{
	protected override void OnSubModuleLoad()
	{
		base.OnSubModuleLoad();
		MissileDamageBridge.Install();
	}

	public override void OnMissionBehaviorInitialize(Mission mission)
	{
		base.OnMissionBehaviorInitialize(mission);
		if (mission != null)
		{
			mission.AddMissionBehavior(new GuidedArrowBehavior());
		}
	}
}
