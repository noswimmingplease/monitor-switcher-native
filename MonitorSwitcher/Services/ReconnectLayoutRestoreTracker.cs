using System;

namespace WorkMonitorSwitcher.Services
{
    /// <summary>
    /// Tracks whether the selected profile's exact saved monitor set has
    /// transitioned from incomplete to active. The first observation establishes
    /// a baseline, and suppressed transitions are consumed so an app-initiated
    /// display action cannot trigger a later duplicate restore.
    /// </summary>
    internal sealed class ReconnectLayoutRestoreTracker
    {
        private string _profileIdentity = string.Empty;
        private bool _hasObservation;
        private bool _exactSavedMonitorSetWasActive;

        public bool Observe(
            string profileIdentity,
            bool exactSavedMonitorSetActive,
            bool allowTrigger)
        {
            var normalisedProfileIdentity = (profileIdentity ?? string.Empty).Trim();
            if (!_hasObservation ||
                !string.Equals(
                    _profileIdentity,
                    normalisedProfileIdentity,
                    StringComparison.OrdinalIgnoreCase))
            {
                _profileIdentity = normalisedProfileIdentity;
                _exactSavedMonitorSetWasActive = exactSavedMonitorSetActive;
                _hasObservation = true;
                return false;
            }

            var becameActive = !_exactSavedMonitorSetWasActive && exactSavedMonitorSetActive;
            _exactSavedMonitorSetWasActive = exactSavedMonitorSetActive;
            return becameActive && allowTrigger;
        }

        public void Reset()
        {
            _profileIdentity = string.Empty;
            _exactSavedMonitorSetWasActive = false;
            _hasObservation = false;
        }
    }
}
