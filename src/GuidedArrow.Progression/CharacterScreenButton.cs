using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.Data;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace GuidedArrow.Progression
{
    internal sealed class CharacterScreenButtonVM : ViewModel
    {
        [DataSourceProperty]
        public string ButtonText => "Guided Arrow Mastery";

        public void ExecuteOpenMastery()
        {
            SubModule.OpenScreen();
        }
    }

    internal sealed class CharacterScreenButtonController
    {
        private ScreenBase _screen;
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private CharacterScreenButtonVM _dataSource;

        internal void Tick()
        {
            ScreenBase top = ScreenManager.TopScreen;
            bool shouldShow = top != null && IsCharacterDeveloperScreen(top);

            if (!shouldShow)
            {
                Detach();
                return;
            }

            if (ReferenceEquals(_screen, top) && _layer != null) return;
            Detach();
            Attach(top);
        }

        internal void Detach()
        {
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
                _dataSource = new CharacterScreenButtonVM();
                _layer = new GauntletLayer("GuidedArrowCharacterButton", 220);
                _screen.AddLayer(_layer);
                _movie = _layer.LoadMovie("GuidedArrowCharacterButton", _dataSource);
            }
            catch
            {
                Detach();
            }
        }

        private static bool IsCharacterDeveloperScreen(ScreenBase screen)
        {
            string name = screen.GetType().Name;
            return name.IndexOf("CharacterDeveloperScreen", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
