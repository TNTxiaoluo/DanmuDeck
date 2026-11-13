namespace BiliXbox.Controls
{
    public static class VideoInteractionSessionTests
    {
        public static void Run()
        {
            int count = 0;
            System.Action<bool, string> check = (value, message) => { if (!value) throw new System.Exception(message); count++; };
            var session = new VideoInteractionSession();
            check(!session.CanMutate && session.BeginMutation() == 0, "An unopened drawer cannot mutate");
            long anonymous = session.Open(200, 0);
            check(session.Active && session.Matches(anonymous, 0) && !session.CanMutate, "Anonymous target can render without write authority");
            check(!session.AcceptRead(anonymous, 200, 0, true), "Anonymous read never grants mutation state");
            long generation = session.Open(200, 30);
            check(!session.Verified && session.BeginMutation() == 0, "Opening alone never submits a write");
            check(!session.AcceptRead(generation, 201, 30, true), "Other-video read cannot arm actions");
            check(!session.AcceptRead(generation, 200, 31, true), "Other-account read cannot arm actions");
            check(session.AcceptRead(generation, 200, 30, false) && session.CanMutate, "Current server read arms deliberate interaction");
            long first = session.BeginMutation();
            check(first > 0 && session.InFlight && !session.CanMutate, "One submitted action locks mutation controls");
            check(session.BeginMutation() == 0, "Repeated A cannot duplicate a pending mutation");
            check(!session.CompleteMutation(generation, first + 1, true) && session.InFlight, "Unrelated completion cannot unlock pending POST");
            check(session.AcceptRead(generation, 200, 30, true) && !session.CanMutate, "Read while write is pending cannot rearm actions");
            check(session.CompleteMutation(generation, first, false), "Unknown outcome is recorded for current attempt");
            check(session.IsUncertain && !session.InFlight && !session.CanMutate, "Unknown result disables a second write");
            check(session.AcceptRead(generation, 200, 30, false) && session.IsUncertain && !session.CanMutate,
                "Background read must not silently consent to repeating an uncertain coin request");
            session.Stop();
            check(!session.Active && !session.CanMutate, "Closing removes action authority");
            long reopened = session.Open(200, 30);
            check(session.IsUncertain && !session.CanMutate, "Unknown write survives reopening same video/account");
            check(session.AcceptRead(reopened, 200, 30, true) && session.CanMutate && !session.IsUncertain,
                "An explicit successful refresh clears uncertainty after observing actual state");
            long second = session.BeginMutation();
            check(second > first, "Attempts remain distinct across resource reopen");
            session.Stop();
            check(!session.CompleteMutation(reopened, second, true), "Late completion after close cannot mutate hidden surface");
            long otherAccount = session.Open(200, 31);
            check(!session.IsUncertain && session.AcceptRead(otherAccount, 200, 31, false) && session.CanMutate,
                "Previous account uncertainty does not leak into new account");
            check(!session.Matches(otherAccount, 30), "External account switch invalidates captured response ownership");
            long oldAttempt = session.BeginMutation();
            long otherVideo = session.Open(201, 31);
            check(!session.CompleteMutation(otherAccount, oldAttempt, true), "Previous video's write cannot mark new video liked");
            check(session.AcceptRead(otherVideo, 201, 31, false) && session.CanMutate, "New video's own read permits interaction");
            long confirmed = session.BeginMutation();
            check(session.CompleteMutation(otherVideo, confirmed, true) && session.Verified && session.CanMutate,
                "Verified actual result unlocks future deliberate actions");
            check(!session.CompleteMutation(otherVideo, confirmed, false) && !session.IsUncertain,
                "Duplicate completion cannot reverse known successful result");
            long originalAgain = session.Open(200, 31);
            check(session.IsUncertain && session.AcceptRead(originalAgain, 200, 31, false) && !session.CanMutate,
                "Navigation during a pending POST remembers uncertainty on its original target");

            var draft = new VideoFavoriteSelection();
            draft.Reset(new[] { Pair(1, true), Pair(2, false), Pair(3, false) });
            check(!draft.Changed && draft.Added.Count == 0 && draft.Removed.Count == 0, "Opening favorites never produces mutations");
            check(draft.Contains(1) && !draft.Contains(2), "Folder draft reflects server membership");
            check(!draft.Toggle(99) && !draft.Changed, "Unknown/foreign folder cannot enter outgoing mutation");
            check(draft.Toggle(2) && draft.Changed && draft.Added.Count == 1 && draft.Added[0] == 2,
                "Selecting a folder adds exactly its ID");
            check(draft.Toggle(1) && draft.Removed.Count == 1 && draft.Removed[0] == 1, "Deselecting known folder produces explicit removal");
            check(draft.Toggle(2) && draft.Added.Count == 0, "Toggling back removes uncommitted additions");
            check(draft.Toggle(1) && !draft.Changed, "Reverting all selections produces no POST");
            draft.Toggle(3); draft.Toggle(2);
            check(draft.Added.Count == 2 && draft.Added[0] == 2 && draft.Added[1] == 3, "Outgoing additions are stable and unique");
            try { draft.Reset(new[] { Pair(8, true), Pair(8, false) }); throw new System.Exception("Invalid duplicate data accepted"); }
            catch (System.ArgumentException) { check(draft.Contains(1) && draft.Added.Count == 2, "Malformed refresh preserves existing favorite draft atomically"); }
            try { draft.Reset(new[] { Pair(0, true) }); throw new System.Exception("Invalid folder accepted"); }
            catch (System.ArgumentException) { check(draft.Contains(2), "Invalid folder ID does not damage draft"); }
            draft.Reset(new[] { Pair(1, true), Pair(2, false), Pair(3, false) });
            check(!draft.Changed && !draft.Contains(2), "Back/reload discards unpublished checkbox changes");
            draft.Toggle(1);
            check(draft.Added.Count == 0 && draft.Removed.Count == 1, "Unfavorite-all is explicit removal, never an implicit empty request");
            System.Console.WriteLine("PASS: " + count + " production video-interaction session, unknown-write isolation and favorite-draft checks (no network writes).");
        }
        private static System.Collections.Generic.KeyValuePair<long, bool> Pair(long id, bool selected)
            => new System.Collections.Generic.KeyValuePair<long, bool>(id, selected);
    }
}
