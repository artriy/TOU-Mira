using UnityEngine;

namespace TownOfUs.Modules.DraftMode;

public static class DraftManager
{
    private static readonly DraftSessionState Session = new();
    private static readonly Dictionary<byte, float> DisconnectSuspectSince = [];
    private const float DisconnectConfirmSeconds = 1.5f;
    private static bool _hostInvalidated;

    public static bool IsDraftActive => Session.IsActive;
    public static string SessionId => Session.SessionId;
    public static int SessionHostId => Session.HostId;
    public static int CurrentTurnNumber { get; private set; }
    public static int CurrentTurnSlot { get; private set; } = -1;
    public static int TotalSlots => Session.TotalSlots;
    public static bool IsRecapping { get; private set; }
    public static IEnumerable<int> TurnOrder => States.Where(state => !state.IsWithdrawn).Select(state => state.SlotNumber).OrderBy(slot => slot);
    public static IReadOnlyList<DraftSlotState> States => Session.Slots;

    public static bool BeginSession(string sessionId, int hostId, int totalSlots, List<byte> playerIds, List<int> slotNumbers)
    {
        if (!Session.Begin(sessionId, hostId, totalSlots, playerIds, slotNumbers)) return false;
        CurrentTurnNumber = 0;
        CurrentTurnSlot = -1;
        IsRecapping = false;
        _hostInvalidated = false;
        DisconnectSuspectSince.Clear();
        DraftTicker.EnsureExists();
        DraftSidebarManager.InvalidateCache();
        return true;
    }

    public static bool IsAuthoritative(PlayerControl sender, string sessionId, bool requireActive = true) =>
        sender != null && AmongUsClient.Instance != null && sender.OwnerId == AmongUsClient.Instance.HostId &&
        Session.Matches(sessionId, sender.OwnerId, requireActive);

    public static bool IsCurrentOffer(string sessionId, byte playerId, int turnNumber, int offerRevision) =>
        AmongUsClient.Instance != null && AmongUsClient.Instance.HostId == SessionHostId &&
        Session.IsCurrentOffer(sessionId, playerId, turnNumber, offerRevision);

    public static DraftOfferAcceptance AnnounceTurn(string sessionId, int hostId, int slot, byte playerId,
        int turnNumber, int offerRevision, List<ushort> offers, float secondsRemaining)
    {
        var accepted = Session.Announce(sessionId, hostId, slot, playerId, turnNumber, offerRevision, offers, secondsRemaining);
        if (accepted != DraftOfferAcceptance.Accepted) return accepted;
        CurrentTurnNumber = Math.Max(CurrentTurnNumber, turnNumber);
        CurrentTurnSlot = slot;
        DraftSidebarManager.InvalidateCache();
        DraftStatusOverlay.Refresh();
        return accepted;
    }

    public static void SubmitPick(string sessionId, byte playerId, int turnNumber, int offerRevision, int index, ushort roleId)
    {
        if (!IsCurrentOffer(sessionId, playerId, turnNumber, offerRevision) ||
            !Session.Submit(sessionId, playerId, turnNumber, offerRevision, index, roleId)) return;
        DraftEngineBehaviour.Instance?.TryApplySubmittedPick(playerId, (byte)index, roleId);
    }

    public static bool ConfirmPick(int slot, ushort roleId)
    {
        var state = GetStateForSlot(slot);
        if (state == null) return false;
        return ConfirmPick(SessionId, SessionHostId, slot, state.TurnNumber, state.OfferRevision, roleId);
    }

    public static bool ConfirmPick(string sessionId, int hostId, int slot, int turnNumber, int offerRevision, ushort roleId)
    {
        if (!Session.Confirm(sessionId, hostId, slot, turnNumber, offerRevision, roleId)) return false;
        var state = GetStateForSlot(slot);
        if (roleId != 0 && PlayerControl.LocalPlayer != null && state.PlayerId == PlayerControl.LocalPlayer.PlayerId)
        {
            DraftStatusOverlay.NotifyLocalPlayerPicked(roleId);
            DraftScreenController.Hide();
            DraftScreenController.ShowFinalPickNotification(roleId);
        }
        DraftSidebarManager.InvalidateCache();
        DraftStatusOverlay.Refresh();
        return true;
    }


    public static void WithdrawPlayer(string sessionId, int hostId, byte playerId)
    {
        if (!Session.Withdraw(sessionId, hostId, playerId)) return;
        if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == playerId)
        {
            DraftScreenController.Hide();
            DraftStatusOverlay.DestroyRoleCard();
        }
        DraftSidebarManager.InvalidateCache();
        DraftStatusOverlay.Refresh();
    }

    public static int GetSlotForPlayer(byte playerId) => GetStateForPlayer(playerId)?.SlotNumber ?? -1;
    public static DraftSlotState GetStateForSlot(int slot) => Session.GetSlot(slot);
    public static DraftSlotState GetStateForPlayer(byte playerId) => Session.GetPlayer(playerId);
    public static IReadOnlyList<DraftSlotState> GetAllStates() => States.Where(state => !state.IsWithdrawn).ToList();

    public static bool IsPlayerDisconnected(byte playerId)
    {
        var player = PlayerControl.AllPlayerControls?.ToArray().FirstOrDefault(player => player != null && player.PlayerId == playerId);
        if (player == null || player.Data == null || player.Data.Disconnected || player.GetComponent<DummyBehaviour>() != null) return true;
        if (AmongUsClient.Instance.NetworkMode is NetworkModes.LocalGame or NetworkModes.FreePlay)
        {
            DisconnectSuspectSince.Remove(playerId);
            return false;
        }
        if (AmongUsClient.Instance.GetClient(player.OwnerId) != null)
        {
            DisconnectSuspectSince.Remove(playerId);
            return false;
        }
        if (!DisconnectSuspectSince.TryGetValue(playerId, out var suspectSince))
        {
            DisconnectSuspectSince[playerId] = Time.time;
            return false;
        }
        return Time.time - suspectSince >= DisconnectConfirmSeconds;
    }
    public static void Tick(float elapsed)
    {
        if (SessionId.Length == 0 || _hostInvalidated) return;
        if (AmongUsClient.Instance == null || SessionHostId != AmongUsClient.Instance.HostId)
        {
            _hostInvalidated = true;
            if (DraftEngineBehaviour.Instance != null) DraftEngineBehaviour.Instance.CancelDraft();
            Reset(cancelledBeforeCompletion: true);
            return;
        }
        Session.Tick(elapsed);
    }

    public static bool BeginRecap()
    {
        if (!IsDraftActive || IsRecapping) return false;
        IsRecapping = true;
        DraftScreenController.Hide();
        DraftCancelButton.Hide();
        DraftShuffleButton.HideAndReset();
        DraftSidebarManager.Deactivate();
        DraftStatusOverlay.DestroyRoleCard();
        return true;
    }

    public static void Reset(bool cancelledBeforeCompletion)
    {
        Session.End();
        IsRecapping = false;
        if (cancelledBeforeCompletion) DraftApplier.ClearPendingDraftStates();
        CurrentTurnNumber = 0;
        CurrentTurnSlot = -1;
        DisconnectSuspectSince.Clear();
        DraftScreenController.Hide();
        DraftRecapScreen.Hide();
        DraftCancelButton.Hide();
        DraftShuffleButton.HideAndReset();
        DraftStatusOverlay.DestroyRoleCard();
        DraftStatusOverlay.SetState(OverlayState.Hidden);
        DraftSidebarManager.Deactivate();
        DraftSidebarManager.InvalidateCache();
        if (cancelledBeforeCompletion && GameStartManager.Instance != null) GameStartManager.Instance.ResetStartState();
    }

    public static void LeaveLobby()
    {
        DraftEngineBehaviour.Instance?.CancelDraft();
        Reset(cancelledBeforeCompletion: true);
        Session.LeaveLobby();
    }
}

public class RecapEntry(int slotNumber, string roleName, string teamLabel = null!, string colorHex = null!)
{
    public int SlotNumber { get; } = slotNumber;
    public string RoleName { get; } = roleName;
    public string TeamLabel { get; } = teamLabel ?? "Unknown";
    public string ColorHex { get; } = colorHex ?? "FFFFFF";
}
