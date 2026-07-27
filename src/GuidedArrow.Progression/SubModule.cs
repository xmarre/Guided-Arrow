using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace GuidedArrow.Progression
{
    public sealed class SubModule : MBSubModuleBase
    {
        private Harmony _harmony;
        private bool _openLatch;
        private readonly CharacterScreenButtonController _characterButton = new CharacterScreenButtonController();

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            _harmony = new Harmony("guidedarrow.progression.1.2.0");
            GuidedArrowPatches.Install(_harmony);
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            CampaignGameStarter starter = gameStarterObject as CampaignGameStarter;
            if (starter != null) starter.AddBehavior(new ProgressionCampaignBehavior());
        }

        public override void OnGameEnd(Game game)
        {
            _characterButton.Detach();
            ProgressionService.Detach();
            _openLatch = false;
            base.OnGameEnd(game);
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            _characterButton.Tick();
            if (Campaign.Current == null || Mission.Current != null) { _openLatch = false; return; }
            bool control = Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl);
            bool pressed = control && Input.IsKeyPressed(InputKey.U);
            if (pressed && !_openLatch) OpenScreen();
            _openLatch = pressed;
        }

        protected override void OnSubModuleUnloaded()
        {
            try { _harmony?.UnpatchAll("guidedarrow.progression.1.2.0"); } catch { }
            _characterButton.Detach();
            ProgressionService.Detach();
            base.OnSubModuleUnloaded();
        }

        internal static void OpenScreen()
        {
            if (Campaign.Current == null || ScreenManager.TopScreen is GuidedArrowMasteryScreen) return;
            ScreenManager.PushScreen(new GuidedArrowMasteryScreen());
        }
    }

    internal static class ConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("open", "guided_arrow")]
        public static string Open(List<string> args)
        {
            if (Campaign.Current == null) return "Guided Arrow Mastery requires an active campaign.";
            SubModule.OpenScreen();
            return "Guided Arrow Mastery opened.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("add_mastery_xp", "guided_arrow")]
        public static string AddXp(List<string> args)
        {
            ProgressionCampaignBehavior p = ProgressionService.Current;
            if (p == null) return "No active campaign progression state.";
            int amount;
            if (args == null || args.Count != 1 || !int.TryParse(args[0], out amount) || amount <= 0) return "Usage: guided_arrow.add_mastery_xp <positive amount>";
            p.AddXp(amount);
            return "Added " + amount + " Guided Arrow mastery XP.";
        }
    }
}
