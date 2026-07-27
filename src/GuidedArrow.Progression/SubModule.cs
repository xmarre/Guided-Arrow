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
        private bool _pendingCharacterScreenOpen;
        private int _pendingCharacterScreenOpenFrames;
        private readonly CharacterScreenButtonController _characterButton;

        public SubModule()
        {
            _characterButton = new CharacterScreenButtonController(RequestOpenFromCharacterScreen);
        }

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
            CancelPendingCharacterScreenOpen();
            _characterButton.Detach();
            ProgressionService.Detach();
            _openLatch = false;
            base.OnGameEnd(game);
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            if (HandlePendingCharacterScreenOpen())
            {
                _openLatch = false;
                return;
            }

            ScreenBase top = ScreenManager.TopScreen;
            if (!(top is GuidedArrowMasteryScreen) && CharacterScreenButtonController.IsCharacterDeveloperScreen(top))
            {
                _characterButton.Resume();
            }
            _characterButton.Tick();

            if (Campaign.Current == null || Mission.Current != null || !IsCampaignMapScreen(ScreenManager.TopScreen))
            {
                _openLatch = false;
                return;
            }

            bool control = Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl);
            bool pressed = control && Input.IsKeyPressed(InputKey.U);
            if (pressed && !_openLatch) OpenScreenFromCampaignMap();
            _openLatch = pressed;
        }

        protected override void OnSubModuleUnloaded()
        {
            try { _harmony?.UnpatchAll("guidedarrow.progression.1.2.0"); } catch { }
            CancelPendingCharacterScreenOpen();
            _characterButton.Detach();
            ProgressionService.Detach();
            base.OnSubModuleUnloaded();
        }

        private void RequestOpenFromCharacterScreen()
        {
            if (Campaign.Current == null || Mission.Current != null) return;
            if (!CharacterScreenButtonController.IsCharacterDeveloperScreen(ScreenManager.TopScreen)) return;

            _characterButton.Suspend();
            _pendingCharacterScreenOpen = true;
            _pendingCharacterScreenOpenFrames = 1;
            _openLatch = false;
        }

        private bool HandlePendingCharacterScreenOpen()
        {
            if (!_pendingCharacterScreenOpen) return false;

            if (Campaign.Current == null || Mission.Current != null)
            {
                CancelPendingCharacterScreenOpen();
                return false;
            }

            if (_pendingCharacterScreenOpenFrames > 0)
            {
                _pendingCharacterScreenOpenFrames--;
                return true;
            }

            ScreenBase top = ScreenManager.TopScreen;
            if (!CharacterScreenButtonController.IsCharacterDeveloperScreen(top))
            {
                CancelPendingCharacterScreenOpen();
                return false;
            }

            _pendingCharacterScreenOpen = false;
            _pendingCharacterScreenOpenFrames = 0;
            ScreenManager.PushScreen(new GuidedArrowMasteryScreen());
            return true;
        }

        private void CancelPendingCharacterScreenOpen()
        {
            _pendingCharacterScreenOpen = false;
            _pendingCharacterScreenOpenFrames = 0;
            _characterButton.Resume();
        }

        internal static void OpenScreen()
        {
            OpenScreenFromCampaignMap();
        }

        private static void OpenScreenFromCampaignMap()
        {
            if (Campaign.Current == null || Mission.Current != null) return;
            if (ScreenManager.TopScreen is GuidedArrowMasteryScreen) return;
            if (!IsCampaignMapScreen(ScreenManager.TopScreen)) return;
            ScreenManager.PushScreen(new GuidedArrowMasteryScreen());
        }

        private static bool IsCampaignMapScreen(ScreenBase screen)
        {
            if (screen == null) return false;
            string name = screen.GetType().Name;
            return name.Equals("MapScreen", StringComparison.OrdinalIgnoreCase) ||
                   name.IndexOf("CampaignMapScreen", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class ConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("open", "guided_arrow")]
        public static string Open(List<string> args)
        {
            if (Campaign.Current == null) return "Guided Arrow Mastery requires an active campaign.";
            if (Mission.Current != null) return "Guided Arrow Mastery cannot be opened during a mission.";
            SubModule.OpenScreen();
            return ScreenManager.TopScreen is GuidedArrowMasteryScreen
                ? "Guided Arrow Mastery opened."
                : "Return to the campaign map before opening Guided Arrow Mastery.";
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
