using System;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Primitives;
namespace BiliXbox
{
    public sealed partial class MainPage
    {
        private bool _adjustingVolume;
        private double _lastAudibleVolume = 1;
        private void InitializeVolume()
        {
            var saved = ApplicationData.Current.LocalSettings.Values["playerVolume"] as double?;
            double value = saved.HasValue && !double.IsNaN(saved.Value) && !double.IsInfinity(saved.Value)
                ? Math.Max(0, Math.Min(1, saved.Value)) : 1;
            _media.Volume = value; if (value > 0) _lastAudibleVolume = value;
            VolumeSlider.Value = value * 100;
            VolumeSlider.ValueChanged += Volume_Changed;
            UpdateVolumeLabel();
        }
        private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs args)
        {
            double value = Math.Max(0, Math.Min(100, args.NewValue)) / 100;
            _media.Volume = value;
            if (value > 0) { _lastAudibleVolume = value; _media.IsMuted = false; }
            ApplicationData.Current.LocalSettings.Values["playerVolume"] = value;
            _playerUi.Touch(DateTimeOffset.UtcNow); UpdateVolumeLabel();
        }
        private void UpdateVolumeLabel()
        {
            VolumeLabel.Text = Math.Round(_media.Volume * 100) + "%";
            VolumeButton.Content = "音量 " + (_media.IsMuted ? "静音" : VolumeLabel.Text);
            VolumeEditPanel.Visibility = _adjustingVolume ? Visibility.Visible : Visibility.Collapsed;
        }
        private void VolumeMute_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            if (_media.IsMuted || _media.Volume == 0) {
                _media.IsMuted = false; if (_media.Volume == 0) VolumeSlider.Value = _lastAudibleVolume * 100;
            } else _media.IsMuted = true;
            _playerUi.Touch(DateTimeOffset.UtcNow); UpdateVolumeLabel();
        }
        private void VolumeSelect_Click(object sender, RoutedEventArgs args)
        {
            if (IgnorePlayerClick()) return;
            _adjustingVolume = !_adjustingVolume;
            UpdateVolumeLabel();
            _playerUi.Touch(DateTimeOffset.UtcNow);
            SetStatus(_adjustingVolume ? "左右调节视频音量 · A 或 B 返回底栏" : "视频音量已设置");
        }
    }
}
