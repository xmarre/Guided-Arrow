using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.Data;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace GuidedArrow.Progression
{
    internal sealed class CharacterScreenButtonVM : ViewModel
    {
        private readonly Action _openMastery;

        internal CharacterScreenButtonVM(Action openMastery)
        {
            _openMastery = openMastery;
        }

        [DataSourceProperty]
        public string ButtonText => "Guided Arrow Mastery";

        public void ExecuteOpenMastery()
        {
            _openMastery?.Invoke();
        }
    }

    internal sealed class CharacterScreenButtonController
    {
        private readonly Action _openMastery;
        private ScreenBase _screen;
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private CharacterScreenButtonVM _dataSource;
        private bool _suspended;

        internal CharacterScreenButtonController(Action openMastery)
        {
            _openMastery = openMastery;
        }

        internal void Tick()
        {
            if (_suspended)
            {
                Detach();
                return;
            }

            ScreenBase top = ScreenManager.TopScreen;
            bool shouldShow = IsCharacterDeveloperScreen(top);

            if (!shouldShow)
            {
                Detach();
                return;
            }

            if (ReferenceEquals(_screen, top) && _layer != null) return;
            Detach();
            Attach(top);
        }

        internal void Suspend()
        {
            _suspended = true;
            Detach();
        }

        internal void Resume()
        {
            _suspended = false;
        }

        internal void Detach()
        {
            try
            {
                _layer?.InputRestrictions.ResetInputRestrictions();
            }
            catch { }

            try
            {
                if (_layer != null && _movie != null) _layer.ReleaseMovie(_movie);
            }
            catch { }

            try
            {
                if (_dataSource != null) _dataSource.OnFinalize();
            }
            catch { }

            try
            {
                if (_screen != null && _layer != null) _screen.RemoveLayer(_layer);
            }
            catch { }

            _movie = null;
            _dataSource = null;
            _layer = null;
            _screen = null;
        }

        private void Attach(ScreenBase screen)
        {
            try
            {
                _screen = screen;
                _dataSource = new CharacterScreenButtonVM(_openMastery);
                _layer = new GauntletLayer("GuidedArrowCharacterButton", 220);
                _layer.InputRestrictions.SetInputRestrictions();
                _movie = _layer.LoadMovie("GuidedArrowCharacterButton", _dataSource);
                _screen.AddLayer(_layer);
            }
            catch
            {
                Detach();
            }
        }

        internal static bool IsCharacterDeveloperScreen(ScreenBase screen)
        {
            if (screen == null) return false;
            string name = screen.GetType().Name;
            return name.IndexOf("CharacterDeveloperScreen", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
