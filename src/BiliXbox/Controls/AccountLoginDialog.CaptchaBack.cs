using System;
using Windows.Gaming.Input;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace BiliXbox.Controls
{
    public sealed partial class AccountLoginDialog
    {
        private readonly LoginCaptchaBackState _challengeBackState = new LoginCaptchaBackState();
        private DispatcherTimer _challengeBackTimer;

        private void StartCaptchaBackPolling()
        {
            StopCaptchaBackPolling();
            _challengeBackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _challengeBackTimer.Tick += CaptchaBackTick;
            _challengeBackTimer.Start();
        }

        private void StopCaptchaBackPolling()
        {
            if (_challengeBackTimer != null)
            {
                _challengeBackTimer.Stop();
                _challengeBackTimer.Tick -= CaptchaBackTick;
                _challengeBackTimer = null;
            }
            _challengeBackState.Reset();
        }

        private void CaptchaBackTick(object sender, object args)
        {
            if (_closed || _challengeOwner == null || _challengeOwner.IsCancellationRequested)
            { StopCaptchaBackPolling(); return; }
            try
            {
                var window = Window.Current;
                if (window == null || !window.Visible || window.CoreWindow.ActivationMode != CoreWindowActivationMode.ActivatedInForeground)
                { _challengeBackState.Reset(); return; }
                bool available = false, pressed = false;
                foreach (var gamepad in Gamepad.Gamepads)
                {
                    available = true;
                    pressed |= (gamepad.GetCurrentReading().Buttons & GamepadButtons.B) != 0;
                }
                if (_challengeBackState.Update(available, pressed)) CancelChallenge();
            }
            catch (Exception)
            {
                // Controller removal can race a reading. A fresh release is
                // required before another controller can cancel verification.
                _challengeBackState.Reset();
            }
        }
    }
}
