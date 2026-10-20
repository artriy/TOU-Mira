using TownOfUs.Modules.DraftMode;
using Xunit;

namespace TownOfUs.Draft.Tests;

public sealed class DraftSessionTests
{
    private const int Host = 7;
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NextSession = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void PublicRedactionCannotEraseThePickersPrivateRole()
    {
        var draft = Start();
        Announce(draft);

        Assert.True(draft.Confirm(Session, Host, 1, 1, 1, 100));
        Assert.False(draft.Confirm(Session, Host, 1, 1, 1, 0));

        Assert.True(draft.GetSlot(1).HasPicked);
        Assert.Equal((ushort)100, draft.GetSlot(1).ChosenRoleId);
    }

    [Fact]
    public void APrivateConfirmationCanRevealAnAlreadyRedactedPick()
    {
        var draft = Start();
        Announce(draft);

        Assert.True(draft.Confirm(Session, Host, 1, 1, 1, 0));
        Assert.True(draft.Confirm(Session, Host, 1, 1, 1, 100));

        Assert.True(draft.GetSlot(1).HasPicked);
        Assert.Equal((ushort)100, draft.GetSlot(1).ChosenRoleId);
    }

    [Fact]
    public void ACommittedRoleCannotBeReplacedByAnotherConfirmation()
    {
        var draft = Start();
        Announce(draft);
        draft.Confirm(Session, Host, 1, 1, 1, 100);

        Assert.False(draft.Confirm(Session, Host, 1, 1, 1, 101));
        Assert.Equal((ushort)100, draft.GetSlot(1).ChosenRoleId);
    }

    [Fact]
    public void ConfirmationFromAnotherHostCannotCommitASlot()
    {
        var draft = Start();
        Announce(draft);

        Assert.False(draft.Confirm(Session, 55, 1, 1, 1, 100));
        Assert.False(draft.GetSlot(1).HasPicked);
        Assert.True(draft.GetSlot(1).IsPickingNow);
    }

    [Fact]
    public void ConfirmationBeforeAnOfferCannotCommitASlot()
    {
        var draft = Start();

        Assert.False(draft.Confirm(Session, Host, 1, 0, 0, 100));
        Assert.False(draft.GetSlot(1).HasPicked);
    }

    [Fact]
    public void AnEndedSessionCannotBeReplayedAsANewDraft()
    {
        var draft = Start();
        draft.End();

        Assert.False(draft.Begin(Session, Host, 1, new byte[] { 0 }, new[] { 1 }));
        Assert.False(draft.IsActive);
    }

    [Fact]
    public void OldConfirmationsCannotAffectReusedSlots()
    {
        var draft = Start();
        Announce(draft);
        draft.End();
        Assert.True(draft.Begin(NextSession, Host, 1, new byte[] { 9 }, new[] { 1 }));
        draft.Announce(NextSession, Host, 1, 9, 1, 1, new ushort[] { 101 }, 10);

        Assert.False(draft.Confirm(Session, Host, 1, 1, 1, 100));
        Assert.False(draft.Submit(Session, 9, 1, 1, 0, 100));
        Assert.False(draft.GetSlot(1).HasPicked);
        Assert.Equal((byte)9, draft.GetSlot(1).PlayerId);
    }

    [Fact]
    public void DuplicateStartCannotResetAnActiveDraft()
    {
        var draft = Start();
        Announce(draft);
        draft.Submit(Session, 0, 1, 1, 0, 100);

        Assert.False(draft.Begin(Session, Host, 1, new byte[] { 0 }, new[] { 1 }));
        Assert.Equal((byte)0, draft.GetSlot(1).PendingPickIndex);
        Assert.True(draft.GetSlot(1).IsPickingNow);
    }

    [Fact]
    public void ANewStartCannotReplaceAnActiveSession()
    {
        var draft = Start();

        Assert.False(draft.Begin(NextSession, Host, 1, new byte[] { 8 }, new[] { 1 }));
        Assert.Equal(Session, draft.SessionId);
        Assert.Equal((byte)0, draft.GetSlot(1).PlayerId);
    }

    [Fact]
    public void DuplicatePlayersAndSlotsAreRejectedAtomically()
    {
        var draft = new DraftSessionState();

        Assert.False(draft.Begin(Session, Host, 2, new byte[] { 0, 0 }, new[] { 1, 2 }));
        Assert.False(draft.Begin(Session, Host, 2, new byte[] { 0, 1 }, new[] { 1, 1 }));
        Assert.False(draft.Begin(Session, Host, 2, new byte[] { 0, 1 }, new[] { 1, 3 }));
        Assert.True(draft.Begin(Session, Host, 2, new byte[] { 0, 1 }, new[] { 2, 1 }));
        Assert.Equal((byte)1, draft.GetSlot(1).PlayerId);
        Assert.Equal((byte)0, draft.GetSlot(2).PlayerId);
    }

    [Fact]
    public void DuplicateAnnouncementPreservesSubmittedChoiceAndRemainingTime()
    {
        var draft = Start();
        Announce(draft);
        draft.Tick(3);
        Assert.True(draft.Submit(Session, 0, 1, 1, 0, 100));

        Assert.Equal(DraftOfferAcceptance.Duplicate,
            draft.Announce(Session, Host, 1, 0, 1, 1, new ushort[] { 100, 101 }, 10));

        Assert.Equal(7f, draft.GetSlot(1).TurnTimeLeft);
        Assert.Equal((byte)0, draft.GetSlot(1).PendingPickIndex);
        Assert.Equal((ushort)100, draft.GetSlot(1).PendingPickRoleId);
    }

    [Fact]
    public void ChangedCardsNeedANewOfferRevision()
    {
        var draft = Start();
        Announce(draft);
        draft.Tick(2);

        Assert.Equal(DraftOfferAcceptance.Rejected,
            draft.Announce(Session, Host, 1, 0, 1, 1, new ushort[] { 101, 100 }, 10));

        Assert.Equal(new ushort[] { 100, 101 }, draft.GetSlot(1).ActiveOffers);
        Assert.Equal(8f, draft.GetSlot(1).TurnTimeLeft);
    }

    [Fact]
    public void ARefreshRejectsClicksFromThePreviousOffer()
    {
        var draft = Start();
        Announce(draft);
        draft.Announce(Session, Host, 1, 0, 1, 2, new ushort[] { 100, 101 }, 8);

        Assert.False(draft.Submit(Session, 0, 1, 1, 0, 100));
        Assert.True(draft.Submit(Session, 0, 1, 2, 0, 100));
        Assert.Equal(2, draft.GetSlot(1).PendingPickOfferRevision);
    }

    [Fact]
    public void OldAnnouncementsCannotRollAnotherSlotBackToAnEarlierBatch()
    {
        var draft = Start(2);
        draft.Announce(Session, Host, 1, 0, 8, 1, new ushort[] { 100 }, 10);

        Assert.Equal(DraftOfferAcceptance.Rejected,
            draft.Announce(Session, Host, 2, 1, 7, 1, new ushort[] { 101 }, 10));
        Assert.False(draft.GetSlot(2).IsPickingNow);
    }

    [Fact]
    public void ConcurrentSlotsCanReceiveTheSameBatchNumber()
    {
        var draft = Start(2);

        Assert.Equal(DraftOfferAcceptance.Accepted,
            draft.Announce(Session, Host, 1, 0, 8, 1, new ushort[] { 100 }, 5));
        Assert.Equal(DraftOfferAcceptance.Accepted,
            draft.Announce(Session, Host, 2, 1, 8, 1, new ushort[] { 101 }, 10));
        draft.Tick(3);

        Assert.Equal(2f, draft.GetSlot(1).TurnTimeLeft);
        Assert.Equal(7f, draft.GetSlot(2).TurnTimeLeft);
    }

    [Fact]
    public void LateRefreshGrantsAPickWindowRegardlessOfOriginalDuration()
    {
        var draft = Start(2);
        draft.Announce(Session, Host, 1, 0, 1, 1, new ushort[] { 100 }, 60);
        draft.Announce(Session, Host, 2, 1, 1, 1, new ushort[] { 101 }, 60);
        draft.Tick(59.8f);

        Assert.Equal(DraftOfferAcceptance.Accepted,
            draft.Announce(Session, Host, 1, 0, 1, 2, new ushort[] { 102 }, 0.2f));

        Assert.Equal(5f, draft.GetSlot(1).TurnTimeLeft);
        Assert.InRange(draft.GetSlot(2).TurnTimeLeft, 0.19f, 0.21f);
        Assert.False(draft.Submit(Session, 0, 1, 1, 0, 100));
    }

    [Fact]
    public void SubmissionMustMatchBothCardIndexAndRole()
    {
        var draft = Start();
        Announce(draft);

        Assert.False(draft.Submit(Session, 0, 1, 1, 0, 101));
        Assert.False(draft.Submit(Session, 0, 1, 1, 1, 100));
        Assert.False(draft.Submit(Session, 0, 1, 1, -1, 100));
        Assert.True(draft.Submit(Session, 0, 1, 1, 1, 101));
        Assert.False(draft.Submit(Session, 0, 1, 1, 0, 100));
        Assert.Equal((ushort)101, draft.GetSlot(1).PendingPickRoleId);
    }

    [Fact]
    public void AWithdrawnPlayerCannotPickOrBeReconfirmed()
    {
        var draft = Start();
        Announce(draft);
        draft.Confirm(Session, Host, 1, 1, 1, 100);

        Assert.True(draft.Withdraw(Session, Host, 0));
        Assert.False(draft.Confirm(Session, Host, 1, 1, 1, 100));
        Assert.False(draft.Submit(Session, 0, 1, 1, 0, 100));
        Assert.True(draft.GetSlot(1).IsWithdrawn);
        Assert.False(draft.GetSlot(1).HasPicked);
        Assert.Equal((ushort)0, draft.GetSlot(1).ChosenRoleId);
    }

    [Fact]
    public void CancellingRejectsEveryMessageFromTheEndedSession()
    {
        var draft = Start();
        Announce(draft);
        draft.End();

        Assert.False(draft.Confirm(Session, Host, 1, 1, 1, 100));
        Assert.False(draft.Submit(Session, 0, 1, 1, 0, 100));
        Assert.Equal(DraftOfferAcceptance.Rejected,
            draft.Announce(Session, Host, 1, 0, 1, 2, new ushort[] { 100 }, 10));
        Assert.Empty(draft.Slots);
    }

    private static DraftSessionState Start(int count = 1)
    {
        var draft = new DraftSessionState();
        Assert.True(draft.Begin(Session, Host, count,
            Enumerable.Range(0, count).Select(index => (byte)index).ToArray(),
            Enumerable.Range(1, count).ToArray()));
        return draft;
    }

    private static void Announce(DraftSessionState draft)
    {
        Assert.Equal(DraftOfferAcceptance.Accepted,
            draft.Announce(Session, Host, 1, 0, 1, 1, new ushort[] { 100, 101 }, 10));
    }
}
