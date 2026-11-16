using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.UI.Xaml.Controls;
namespace BiliXbox.Controls
{
    public sealed partial class CreatorHubView
    {
        private bool _allFollowingActivity;
        private Button _continuous;
        public bool ContinuousPlayback { get; private set; }
        private void ToggleContinuousPlayback()
        {
            ContinuousPlayback = !ContinuousPlayback;
            _continuous.Content = "连续播放：" + (ContinuousPlayback ? "开" : "关");
        }
        public async Task<VideoItem> NextPlaylistVideoAsync(string bvid, CancellationToken token)
        {
            if (!ContinuousPlayback || string.IsNullOrWhiteSpace(bvid)) return null;
            long mid = _mid;
            int index = _videos.FindIndex(item => item.Bvid == bvid);
            if (index < 0) return null;
            if (index + 1 < _videos.Count) return _videos[index + 1];
            if (!_videoHasMore) return null;
            var result = await _api.GetCreatorVideosAsync(mid, _videoPage + 1, token);
            token.ThrowIfCancellationRequested();
            if (mid != _mid || !ContinuousPlayback) return null;
            _videoPage++; _videoHasMore = result.HasMore;
            _videos.Clear(); _videos.AddRange(result.Videos); _videoList.Items.Clear();
            foreach (var item in _videos) _videoList.Items.Add(item);
            return _videos.FirstOrDefault();
        }
    }
}
