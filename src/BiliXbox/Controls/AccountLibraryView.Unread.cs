using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace BiliXbox.Controls
{
    public sealed partial class AccountLibraryView
    {
        private readonly DynamicReadTracker _activityRead = new DynamicReadTracker();
        private readonly Dictionary<long, int> _authorOrder = new Dictionary<long, int>();
        private long _activitySelectedMid;
        private bool _unreadBusy;
        private int _unreadCursor;
        private void EnsureActivityReadAccount()
        {
            if (_activityRead.Account == _accountMid) return;
            Dictionary<string, string> saved = null;
            try { saved = ReadContainer().Values.Where(pair => pair.Value is string).ToDictionary(pair => pair.Key, pair => (string)pair.Value); }
            catch (Exception) { }
            _activityRead.Reset(_accountMid, saved);
        }
        private void SaveActivityRead()
        {
            if (!HasAccount || _activityRead.Account != _accountMid) return;
            try { var values = ReadContainer().Values; foreach (var pair in _activityRead.Export()) values[pair.Key] = pair.Value; }
            catch (Exception) { /* Read tracking must not prevent viewing a post. */ }
        }
        private ApplicationDataContainer ReadContainer() => ApplicationData.Current.LocalSettings
            .CreateContainer("activityRead." + _accountMid, ApplicationDataCreateDisposition.Always);
        private void OnActivityLoaded(IReadOnlyList<CreatorDynamicItem> posts)
        {
            if (!HasAccount || _state.Pane != LibraryPane.Activity) return;
            EnsureActivityReadAccount(); _activityRead.Observe(posts);
            if (_activitySelectedMid > 0 && _activityEntered)
            { _activityOrderLocked = true; _activityRead.MarkPageViewed(_activitySelectedMid, posts); SaveActivityRead(); }
            SortActivityAuthors();
        }
        private void OnActivityViewed(CreatorDynamicItem post)
        {
            if (!HasAccount || _state.Pane != LibraryPane.Activity || post == null) return;
            EnsureActivityReadAccount(); _activityRead.Observe(new[] { post });
            _activityOrderLocked = true;
            _activityRead.MarkViewed(post.AuthorMid, post.Id); SaveActivityRead(); SortActivityAuthors();
        }
        private async Task RefreshActivityUnreadAsync(CancellationTokenSource session, long account)
        {
            if (_unreadBusy || session == null || !HasAccount) return;
            _unreadBusy = true; EnsureActivityReadAccount();
            try
            {
                var updates = await _api.GetDynamicAuthorUpdatesAsync(session.Token);
                if (!CurrentActivitySession(session, account)) return;
                _activityRead.ApplyPortal(updates); SortActivityAuthors();
                var recent = await _api.GetFollowingDynamicsAsync("", session.Token);
                if (!CurrentActivitySession(session, account)) return;
                _activityRead.Observe(recent.Items); SortActivityAuthors();
                // Resolve old persistent portal flags using actual latest posts.
                // Bound work per refresh to avoid fan-out over a large follow list.
                var checks = updates.Where(item => _activityRead.NeedsLatestCheck(item.Mid)).ToArray();
                int start = checks.Length == 0 ? 0 : _unreadCursor % checks.Length;
                foreach (var update in checks.Skip(start).Concat(checks.Take(start)).Take(12))
                {
                    var latest = await _api.GetCreatorDynamicsAsync(update.Mid, "", session.Token);
                    if (!CurrentActivitySession(session, account)) return;
                    _activityRead.Observe(latest.Items); SortActivityAuthors();
                }
                _unreadCursor += 12;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (CurrentActivitySession(session, account)) _subtitle.Text = "未读状态暂未刷新，已有动态仍可查看"; }
            finally { if (session == _activityCts) _unreadBusy = false; }
        }
        private bool CurrentActivitySession(CancellationTokenSource session, long account)
            => session != null && session == _activityCts && !session.IsCancellationRequested && account == _accountMid && HasAccount && _state.Pane == LibraryPane.Activity;
        private void SortActivityAuthors()
        {
            if (_authors.Count == 0) return;
            foreach (var button in _authors) UpdateAuthorBadge(button);
            if (_activityOrderLocked) return;
            var selected = _authorIndex < _authors.Count ? _authors[_authorIndex] : null;
            bool restore = _authors.Contains(Windows.UI.Xaml.Input.FocusManager.GetFocusedElement() as Button);
            var byMid = _authors.ToDictionary(button => ((CreatorProfile)button.Tag).Mid);
            var defaultOrder = byMid.Keys.OrderBy(mid => _authorOrder[mid]);
            var ordered = _activityRead.OrderAuthors(defaultOrder).Select(mid => byMid[mid]).ToList();
            if (ordered.SequenceEqual(_authors)) return;
            _authors.Clear(); _authors.AddRange(ordered); _authorButtons.Children.Clear();
            foreach (var button in ordered)
            {
                _authorButtons.Children.Add(button);
            }
            if (_moreAuthors != null) _authorButtons.Children.Add(_moreAuthors);
            _authorIndex = selected == null ? 0 : Math.Max(0, _authors.IndexOf(selected));
            if (restore && !_activityEntered) RestoreActivityFocus();
        }
        private void UpdateAuthorBadge(Button button)
        {
            var author = (CreatorProfile)button.Tag; bool unread = _activityRead.IsUnread(author.Mid);
            var row = button.Content as StackPanel;
            if (row == null)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(new TextBlock { Text = author.Name, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 114, 153)), VerticalAlignment = VerticalAlignment.Center });
                button.Content = row;
            }
            row.Children[1].Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(button, author.Name + (unread ? "，有未读动态" : ""));
        }
    }
}
