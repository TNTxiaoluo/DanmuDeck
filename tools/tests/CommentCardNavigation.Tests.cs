using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The UWP shell is deliberately small: action selection and activation below
// are extracted from CommentCard.cs, rather than reimplemented in this fixture.
namespace Windows.UI.Xaml { public enum Visibility { Visible, Collapsed } }
namespace Windows.UI.Xaml.Controls
{
    public class Control { public bool IsEnabled { get; set; } = true; public Visibility Visibility { get; set; } }
    public class Button : Control { }
}
namespace BiliXbox.Controls
{
    internal enum PlayerDirection { Left, Right, Up, Down }
    public sealed partial class CommentCard
    {
        private readonly Dictionary<Control, Action> _actions = new Dictionary<Control, Action>();
        private readonly List<Control> _orderedActions = new List<Control>();
        private readonly List<Control> _pictureActions = new List<Control>();
        private Button _likeButton;
        private Button _hateButton;
        private bool _reactionBusy;
        public bool IsExpanded { get; set; }
        public bool ShowThreadButton { get; set; } = true;
        private int _primaryRequests;
        private void RequestPrimaryAction() { ++_primaryRequests; }
        private static int _checks;
        private Control Add(Action action, bool picture = false)
        {
            var control = new Button();
            _actions[control] = action; _orderedActions.Add(control);
            if (picture) _pictureActions.Add(control);
            return control;
        }
        private static void Check(bool condition, string message)
        { ++_checks; if (!condition) throw new InvalidOperationException(message); }
        public static string RunTests()
        {
            _checks = 0;
            var card = new CommentCard { IsExpanded = true };
            int likes = 0, hates = 0, pictures = 0, threads = 0;
            var picture = card.Add(() => pictures++, true);
            card._likeButton = (Button)card.Add(() => likes++);
            card._hateButton = (Button)card.Add(() => hates++);
            var thread = card.Add(() => threads++);
            Check(card.PrimaryActionControl(false) == card._likeButton, "A on a list comment selects its like icon.");
            Check(likes == 0 && hates == 0 && pictures == 0, "Entering action mode does not send a vote or open an image.");
            Check(card.TryActivate(card.PrimaryActionControl(false)) && likes == 1, "A on the like icon invokes exactly one vote action.");
            var next = card.NavigateAction(card._likeButton, PlayerDirection.Right);
            Check(next == card._hateButton, "Right from like chooses dislike in the same comment.");
            Check(card.TryActivate(next) && hates == 1 && likes == 1, "Confirming dislike does not also like the comment.");
            Check(card.NavigateAction(next, PlayerDirection.Left) == card._likeButton, "Left returns from dislike to like.");
            Check(card.NavigateAction(picture, PlayerDirection.Left) == picture, "Left edge stays in the comment action group.");
            Check(card.NavigateAction(thread, PlayerDirection.Right) == thread, "Right edge cannot escape into a different comment.");
            Check(card.NavigateAction(card._likeButton, PlayerDirection.Up) == picture, "Up can reach a comment picture.");
            Check(card.NavigateAction(card._likeButton, PlayerDirection.Down) == card._hateButton, "Down advances within an action group.");
            Check(card.PrimaryActionControl(true) == picture, "A on a thread picture comment selects its picture before reactions.");
            Check(card.TryActivate(picture) && pictures == 1, "Confirming the picture invokes the image action once.");
            Check(card.NavigateAction(picture, PlayerDirection.Right) == card._likeButton, "Right from the image reaches like.");
            Check(card.OwnsAction(picture) && card.OwnsAction(card._hateButton), "Pictures and reactions resolve to the same parent card.");
            Check(!card.OwnsAction(new Button()) && !card.OwnsAction(null), "Unrelated controls do not become comment actions.");
            Check(!card.TryActivate(new Button()) && !card.TryActivate(null), "Unrelated or missing controls cannot invoke actions.");
            card.SetReactionBusy(true);
            Check(card._reactionBusy && !card.TryActivate(card._likeButton) && likes == 1, "A pending vote cannot be submitted twice.");
            Check(card.PrimaryActionControl(false) == picture, "A pending vote still allows picture navigation.");
            Check(card.NavigateAction(picture, PlayerDirection.Right) == thread, "Navigation skips both temporarily disabled vote controls.");
            card.SetReactionBusy(false);
            Check(card.PrimaryActionControl(false) == card._likeButton && card._hateButton.IsEnabled, "Vote controls return after the request completes.");
            picture.Visibility = Visibility.Collapsed;
            Check(card.PrimaryActionControl(true) == card._likeButton, "Unavailable pictures cannot trap focus.");
            Check(!card.TryActivate(picture) && pictures == 1, "A hidden image cannot accidentally open from stale focus.");
            card.IsExpanded = true;
            Check(card.NavigateAction(new Button(), PlayerDirection.Right) == card._likeButton, "A stale action target recovers inside the visible card.");
            thread.IsEnabled = false; card.SetReactionBusy(true);
            Check(card.PrimaryActionControl(false) == null && card.NavigateAction(null, PlayerDirection.Right) == null, "An entirely disabled card has no fabricated focus target.");
            Check(threads == 0, "Navigating reactions never opens thread details without confirmation.");
            card = new CommentCard();
            int summaryWrites = 0;
            var summaryLike = card.Add(() => summaryWrites++);
            var summaryPicture = card.Add(() => summaryWrites++, true);
            Check(card.TryActivate(summaryLike) && card._primaryRequests == 1 && summaryWrites == 0, "Native summary reaction confirmation opens the floor instead of voting before thread entry.");
            Check(card.TryActivate(summaryPicture) && card._primaryRequests == 2 && summaryWrites == 0, "Native summary picture confirmation follows the same floor-entry action.");
            card.IsExpanded = true; card.ShowThreadButton = false;
            Check(card.TryActivate(summaryLike) && summaryWrites == 1 && card._primaryRequests == 2, "Expanded thread confirmation invokes its chosen reaction exactly once.");
            return "Comment card navigation: " + _checks + " checks passed.";
        }
    }
}
