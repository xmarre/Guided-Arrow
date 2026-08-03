using TaleWorlds.MountAndBlade;

namespace GuidedArrow;

public sealed class SubModule : MBSubModuleBase
{
	protected override void OnSubModuleLoad()
	{
		((MBSubModuleBase)this).OnSubModuleLoad();
		MissileDamageBridge.Install();
	}

	public override void OnMissionBehaviorInitialize(Mission mission)
	{
		((MBSubModuleBase)this).OnMissionBehaviorInitialize(mission);
		if (mission != null)
		{
			mission.AddMissionBehavior((MissionBehavior)(object)new GuidedArrowBehavior());
		}
	}
}
