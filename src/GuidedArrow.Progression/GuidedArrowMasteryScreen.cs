using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.Data;
using TaleWorlds.ScreenSystem;

namespace GuidedArrow.Progression
{
    internal sealed class GuidedArrowMasteryScreen : ScreenBase
    {
        private GuidedArrowMasteryVM _dataSource;
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;

        protected override void OnInitialize()
        {
            base.OnInitialize();
            _dataSource = new GuidedArrowMasteryVM(Close);
            _layer = new GauntletLayer("GuidedArrowMastery", 100) { IsFocusLayer = true };
            AddLayer(_layer);
            _layer.InputRestrictions.SetInputRestrictions();
            _movie = _layer.LoadMovie("GuidedArrowMastery", _dataSource);
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            ScreenManager.TrySetFocus(_layer);
        }

        protected override void OnDeactivate()
        {
            base.OnDeactivate();
            if (_layer != null)
            {
                _layer.IsFocusLayer = false;
                ScreenManager.TryLoseFocus(_layer);
            }
        }

        protected override void OnFinalize()
        {
            if (_layer != null && _movie != null) _layer.ReleaseMovie(_movie);
            if (_dataSource != null) _dataSource.OnFinalize();
            if (_layer != null) RemoveLayer(_layer);
            _movie = null; _layer = null; _dataSource = null;
            base.OnFinalize();
        }

        private static void Close()
        {
            if (ScreenManager.TopScreen is GuidedArrowMasteryScreen) ScreenManager.PopScreen();
        }
    }
}
