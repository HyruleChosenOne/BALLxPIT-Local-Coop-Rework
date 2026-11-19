using System;
using BALLxPITLocalCoop.Core;

namespace BALLxPITLocalCoop.Input
{
    public enum InputPairingMode
    {
        Auto = 0,
        P1KeyboardP2Gamepad = 1,
        P1GamepadP2Keyboard = 2,
        TwoPlayersKeyboardP1Mouse = 3,
        TwoPlayersKeyboardP2Mouse = 4,
        DualGamepad = 5
    }

    /// <summary>
    /// Smoothly interpolates and clamps an aiming angle once per frame.
    /// </summary>
    public sealed class ControllerFrameAim
    {
        private bool _ready;
        private int _frame = -1;
        private float _angle;

        public void Reset()
        {
            _ready = false;
            _frame = -1;
        }

        public float Update(int frame, float seconds, float initial, float target, float speed, float min, float max)
        {
            if (!_ready)
            {
                _ready = true;
                _angle = initial;
            }

            _angle = Math.Max(min, Math.Min(max, _angle));
            if (_frame != frame)
            {
                _frame = frame;
                _angle = CoopAimRules.TurnDegrees(_angle, target, speed * Math.Max(0f, seconds));
            }
            return _angle;
        }
    }

    /// <summary>
    /// Tracks persistent P1/P2 Rewired controller slot assignments across disconnects/reconnects
    /// without hijacking P2's controller to P1 when P1 unplugs mid-run.
    /// </summary>
    public sealed class ControllerRoles
    {
        public int First = -1;
        public int Second = -1;
        private string _firstIdentity;
        private string _secondIdentity;

        public bool HasRememberedSlots => _firstIdentity != null || _secondIdentity != null;

        public void Reset()
        {
            First = -1;
            Second = -1;
            _firstIdentity = null;
            _secondIdentity = null;
        }

        public void Update(int[] connected, string[] identities, bool preserveSlots = true)
        {
            if (connected == null || identities == null || connected.Length != identities.Length)
            {
                First = -1;
                Second = -1;
                return;
            }

            First = FindByIdentity(connected, identities, _firstIdentity, -1);
            Second = FindByIdentity(connected, identities, _secondIdentity, First);
            if (Second >= 0 && GetIdentity(connected, identities, Second) == GetIdentity(connected, identities, First))
            {
                Second = -1;
            }

            if (!preserveSlots && First < 0 && Second >= 0)
            {
                First = Second;
                _firstIdentity = _secondIdentity;
                Second = -1;
                _secondIdentity = null;
            }

            for (int i = 0; i < connected.Length; i++)
            {
                if (connected[i] == First || connected[i] == Second)
                {
                    continue;
                }

                if (First < 0 && identities[i] != _secondIdentity)
                {
                    First = connected[i];
                    _firstIdentity = identities[i];
                }
                else if (Second < 0 && identities[i] != _firstIdentity)
                {
                    Second = connected[i];
                    _secondIdentity = identities[i];
                }
            }
        }

        private static int FindByIdentity(int[] ids, string[] keys, string wanted, int excluded)
        {
            if (wanted == null)
            {
                return -1;
            }
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] != excluded && keys[i] == wanted)
                {
                    return ids[i];
                }
            }
            return -1;
        }

        private static string GetIdentity(int[] ids, string[] keys, int id)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == id)
                {
                    return keys[i];
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Edge-triggered pause input debounce across P1 Start, P2 Start, and Escape.
    /// Properly syncs held button state on menu transitions to prevent false re-triggers.
    /// </summary>
    public sealed class PauseInputGate
    {
        private readonly bool[] _previous = new bool[3];
        private bool _primed;
        private bool _hadFocus;
        private int _lastFrame = -1;
        private float _blockedUntil;

        public void Reset()
        {
            _primed = false;
            _hadFocus = false;
            _lastFrame = -1;
            _blockedUntil = 0f;
            _previous[0] = _previous[1] = _previous[2] = false;
        }

        public void Transition(int frame, float now, bool firstHeld = true, bool secondHeld = true, bool escapeHeld = true)
        {
            _lastFrame = frame;
            _blockedUntil = now + 0.18f;
            _previous[0] = firstHeld;
            _previous[1] = secondHeld;
            _previous[2] = escapeHeld;
        }

        public bool Update(int frame, float now, bool focused, bool first, bool second, bool escape) =>
            Read(frame, now, focused, allowed: true, first, second, escape);

        public bool Read(int frame, float now, bool focused, bool allowed, bool first, bool second, bool escape)
        {
            if (frame == _lastFrame)
            {
                return false;
            }
            _lastFrame = frame;

            bool rising = (first && !_previous[0]) || (second && !_previous[1]) || (escape && !_previous[2]);
            _previous[0] = first;
            _previous[1] = second;
            _previous[2] = escape;

            bool reacquiredFocus = !_primed || !_hadFocus;
            _primed = true;
            _hadFocus = focused;

            if (!focused || reacquiredFocus || !allowed || now < _blockedUntil || !rising)
            {
                return false;
            }

            _blockedUntil = now + 0.18f;
            return true;
        }
    }
}
