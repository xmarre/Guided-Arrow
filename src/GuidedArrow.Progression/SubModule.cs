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
        private enum CharacterScreenOpenPhase
        {
            None,
            CloseCharacterState,
            WaitForCampaignMap,
            SettleCampaignMap
        }

        private Harmony _harmony;
        private bool _openLatch;
        private bool _pendingCharacterScreenOpen;
        private int _pendingCharacterScreenOpenFrames;
        private int _pendingCharacterScreenOpenTimeoutFrames;
        private CharacterScreenOpenPhase _characterScreenOpenPhase;
        private readonly CharacterScreenButtonController _characterButton;

        public SubModule()
        {
            _characterButton = new CharacterScreenButtonController(RequestOpenFromCharacterScreen);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            _harmony = new Harmony("guidedarrow.progression.1.3.0");
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
            ProgressionRuntimeSettingsPatch.RestoreActive();
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
            ProgressionRuntimeSettingsPatch.RestoreActive();
            try { _harmony?.UnpatchAll("guidedarrow.progression.1.3.0"); } catch { }
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
            _pendingCharacterScreenOpenTimeoutFrames = 300;
            _characterScreenOpenPhase = CharacterScreenOpenPhase.CloseCharacterState;
            _openLatch = false;
        }

        private bool HandlePendingCharacterScreenOpen()
        {
            if (!_pendingCharacterScreenOpen) return false;

            if (Campaign.Current == null || Mission.Current != null || Game.Current == null)
            {
                CancelPendingCharacterScreenOpen("Guided Arrow Mastery can only open from an active campaign screen.");
                return false;
            }

            _pendingCharacterScreenOpenTimeoutFrames--;
            if (_pendingCharacterScreenOpenTimeoutFrames <= 0)
            {
                CancelPendingCharacterScreenOpen("Guided Arrow could not leave the character screen. Return to the campaign map and press Ctrl+U.");
                return false;
            }

            if (_pendingCharacterScreenOpenFrames > 0)
            {
                _pendingCharacterScreenOpenFrames--;
                return true;
            }

            ScreenBase top = ScreenManager.TopScreen;
            switch (_characterScreenOpenPhase)
            {
                case CharacterScreenOpenPhase.CloseCharacterState:
                    if (!CharacterScreenButtonController.IsCharacterDeveloperScreen(top))
                    {
                        // Newer Bannerlord builds can remove the screen before the game-state
                        // transition is observable. Continue waiting for the campaign map instead
                        // of treating the normal transition as a failed click.
                        _characterScreenOpenPhase = CharacterScreenOpenPhase.WaitForCampaignMap;
                        return true;
                    }

                    try
                    {
                        // The character-development screen is driven by CharacterDeveloperState.
                        // Close it through the native game-state stack before opening our own screen.
                        Game.Current.GameStateManager.PopState();
                        _characterScreenOpenPhase = CharacterScreenOpenPhase.WaitForCampaignMap;
                        _pendingCharacterScreenOpenFrames = 1;
                    }
                    catch (Exception exception)
                    {
                        CancelPendingCharacterScreenOpen("Guided Arrow could not close the character screen: " + exception.GetType().Name + ". Return to the campaign map and press Ctrl+U.");
                        return false;
                    }
                    return true;

                case CharacterScreenOpenPhase.WaitForCampaignMap:
                    if (CharacterScreenButtonController.IsCharacterDeveloperScreen(top))
                    {
                        return true;
                    }

                    if (!IsCampaignMapScreen(top))
                    {
                        // Bannerlord 1.4.x may expose an intermediate transition screen for several
                        // frames. Keep the request alive until the real map screen appears or the
                        // bounded timeout reports an actionable error.
                        return true;
                    }

                    // Give the native map bar and panel-switching layer time to finish
                    // rebuilding after CharacterDeveloperState is removed.
                    _characterScreenOpenPhase = CharacterScreenOpenPhase.SettleCampaignMap;
                    _pendingCharacterScreenOpenFrames = 2;
                    return true;

                case CharacterScreenOpenPhase.SettleCampaignMap:
                    if (!IsCampaignMapScreen(top))
                    {
                        _characterScreenOpenPhase = CharacterScreenOpenPhase.WaitForCampaignMap;
                        return true;
                    }

                    _pendingCharacterScreenOpen = false;
                    _pendingCharacterScreenOpenFrames = 0;
                    _pendingCharacterScreenOpenTimeoutFrames = 0;
                    _characterScreenOpenPhase = CharacterScreenOpenPhase.None;
                    try
                    {
                        ScreenManager.PushScreen(new GuidedArrowMasteryScreen());
                    }
                    catch (Exception exception)
                    {
                        CancelPendingCharacterScreenOpen("Guided Arrow Mastery failed to open: " + exception.GetType().Name + ". Guided Arrow remains enabled. Return to the campaign map and press Ctrl+U.");
                    }
                    return true;

                default:
                    CancelPendingCharacterScreenOpen("Guided Arrow Mastery opening was cancelled because its screen transition lost state.");
                    return false;
            }
        }

        private void CancelPendingCharacterScreenOpen(string message = null)
        {
            _pendingCharacterScreenOpen = false;
            _pendingCharacterScreenOpenFrames = 0;
            _pendingCharacterScreenOpenTimeoutFrames = 0;
            _characterScreenOpenPhase = CharacterScreenOpenPhase.None;
            _characterButton.Resume();
            if (!string.IsNullOrEmpty(message))
                InformationManager.DisplayMessage(new InformationMessage(message));
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
            try
            {
                ScreenManager.PushScreen(new GuidedArrowMasteryScreen());
            }
            catch (Exception exception)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "Guided Arrow Mastery failed to open: " + exception.GetType().Name + ". Return to the campaign map and try again."));
            }
        }

        private static bool IsCampaignMapScreen(ScreenBase screen)
        {
            if (screen == null) return false;
            Type type = screen.GetType();
            string name = type.Name ?? string.Empty;
            string fullName = type.FullName ?? name;
            return name.Equals("MapScreen", StringComparison.OrdinalIgnoreCase) ||
                   name.IndexOf("CampaignMapScreen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fullName.IndexOf("CampaignMap", StringComparison.OrdinalIgnoreCase) >= 0;
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
