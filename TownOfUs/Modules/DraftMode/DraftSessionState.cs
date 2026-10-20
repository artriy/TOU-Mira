using System;
using System.Collections.Generic;
using System.Linq;

namespace TownOfUs.Modules.DraftMode;

public enum DraftOfferAcceptance
{
    Rejected,
    Duplicate,
    Accepted
}

public sealed class DraftSessionState
{
    private readonly HashSet<string> _closedSessions = new(StringComparer.Ordinal);
    private readonly List<DraftSlotState> _slots = new();
    private int _highestTurnNumber;

    public string SessionId { get; private set; } = string.Empty;
    public int HostId { get; private set; } = -1;
    public bool IsActive { get; private set; }
    public int TotalSlots { get; private set; }
    public IReadOnlyList<DraftSlotState> Slots => _slots;

    public bool Begin(string sessionId, int hostId, int totalSlots, IReadOnlyList<byte> playerIds, IReadOnlyList<int> slotNumbers)
    {
        if (IsActive || !Guid.TryParseExact(sessionId, "N", out _) || hostId < 0 ||
            _closedSessions.Contains(sessionId) || sessionId == SessionId || playerIds == null || slotNumbers == null ||
            totalSlots <= 0 || playerIds.Count != totalSlots || playerIds.Count != slotNumbers.Count ||
            playerIds.Distinct().Count() != playerIds.Count || slotNumbers.Distinct().Count() != slotNumbers.Count ||
            slotNumbers.Any(slot => slot <= 0 || slot > totalSlots)) return false;

        _slots.Clear();
        for (var i = 0; i < playerIds.Count; i++)
            _slots.Add(new DraftSlotState { PlayerId = playerIds[i], SlotNumber = slotNumbers[i] });
        SessionId = sessionId;
        HostId = hostId;
        TotalSlots = totalSlots;
        _highestTurnNumber = 0;
        IsActive = true;
        return true;
    }

    public bool Matches(string sessionId, int hostId, bool requireActive = true) =>
        (!requireActive || IsActive) && SessionId.Length != 0 && sessionId == SessionId && hostId == HostId;

    public DraftSlotState GetSlot(int slot)
    {
        foreach (var state in _slots) if (state.SlotNumber == slot) return state;
        return null!;
    }

    public DraftSlotState GetPlayer(byte playerId)
    {
        foreach (var state in _slots) if (state.PlayerId == playerId) return state;
        return null!;
    }

    public DraftOfferAcceptance Announce(string sessionId, int hostId, int slot, byte playerId, int turnNumber,
        int offerRevision, IReadOnlyList<ushort> offers, float secondsRemaining)
    {
        var state = GetSlot(slot);
        if (!Matches(sessionId, hostId) || state == null || state.PlayerId != playerId || state.HasPicked ||
            state.IsWithdrawn || turnNumber <= 0 || offerRevision <= 0 || offers == null || offers.Count == 0 ||
            offers.Count > 254 || offers.Any(roleId => roleId == 0) || offers.Distinct().Count() != offers.Count ||
            float.IsNaN(secondsRemaining) || float.IsInfinity(secondsRemaining) || secondsRemaining <= 0f ||
            turnNumber < _highestTurnNumber || turnNumber < state.TurnNumber || offerRevision < state.OfferRevision)
            return DraftOfferAcceptance.Rejected;
        if (turnNumber == state.TurnNumber && offerRevision == state.OfferRevision)
            return state.ActiveOffers.SequenceEqual(offers) ? DraftOfferAcceptance.Duplicate : DraftOfferAcceptance.Rejected;
        if (offerRevision <= state.OfferRevision) return DraftOfferAcceptance.Rejected;

        if (state.OfferRevision > 0) secondsRemaining = Math.Max(5f, secondsRemaining);
        _highestTurnNumber = turnNumber;
        state.TurnNumber = turnNumber;
        state.OfferRevision = offerRevision;
        state.IsPickingNow = true;
        state.TurnDuration = secondsRemaining;
        state.TurnTimeLeft = secondsRemaining;
        state.ActiveOffers.Clear();
        state.ActiveOffers.AddRange(offers);
        state.ClearPendingPick();
        return DraftOfferAcceptance.Accepted;
    }

    public bool IsCurrentOffer(string sessionId, byte playerId, int turnNumber, int offerRevision)
    {
        var state = GetPlayer(playerId);
        return IsActive && sessionId == SessionId && state != null && !state.HasPicked && !state.IsWithdrawn &&
               state.IsPickingNow && state.TurnNumber == turnNumber && state.OfferRevision == offerRevision;
    }

    public bool Submit(string sessionId, byte playerId, int turnNumber, int offerRevision, int index, ushort roleId)
    {
        if (!IsCurrentOffer(sessionId, playerId, turnNumber, offerRevision)) return false;
        var state = GetPlayer(playerId);
        if (index < 0 || index > 254 || index > state.ActiveOffers.Count ||
            (index == state.ActiveOffers.Count ? roleId != 0 : state.ActiveOffers[index] != roleId) ||
            state.PendingPickIndex != 255) return false;
        state.PendingPickIndex = (byte)index;
        state.PendingPickRoleId = roleId;
        state.PendingPickTurnNumber = turnNumber;
        state.PendingPickOfferRevision = offerRevision;
        return true;
    }

    public bool Confirm(string sessionId, int hostId, int slot, int turnNumber, int offerRevision, ushort roleId)
    {
        var state = GetSlot(slot);
        if (!Matches(sessionId, hostId) || state == null || state.IsWithdrawn || state.TurnNumber != turnNumber ||
            state.OfferRevision != offerRevision || turnNumber <= 0 || offerRevision <= 0 ||
            (state.ChosenRoleId != 0 && state.ChosenRoleId != roleId)) return false;
        if (state.HasPicked && (roleId == 0 || state.ChosenRoleId == roleId)) return false;
        state.HasPicked = true;
        if (roleId != 0) state.ChosenRoleId = roleId;
        state.IsPickingNow = false;
        state.TurnTimeLeft = 0f;
        state.ClearPendingPick();
        return true;
    }

    public bool Withdraw(string sessionId, int hostId, byte playerId)
    {
        var state = GetPlayer(playerId);
        if (!Matches(sessionId, hostId) || state == null || state.IsWithdrawn) return false;
        state.IsWithdrawn = true;
        state.IsPickingNow = false;
        state.HasPicked = false;
        state.ChosenRoleId = 0;
        state.TurnTimeLeft = 0f;
        state.ClearPendingPick();
        return true;
    }

    public void Tick(float elapsed)
    {
        if (!IsActive || float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed <= 0f) return;
        foreach (var state in _slots)
            if (state.IsPickingNow && !state.HasPicked && !state.IsWithdrawn)
                state.TurnTimeLeft = Math.Max(0f, state.TurnTimeLeft - elapsed);
    }

    public void End()
    {
        if (SessionId.Length != 0) _closedSessions.Add(SessionId);
        IsActive = false;
        TotalSlots = 0;
        _slots.Clear();
    }

    public void LeaveLobby()
    {
        End();
        _closedSessions.Clear();
        SessionId = string.Empty;
        HostId = -1;
    }
}

public sealed class DraftSlotState
{
    public byte PlayerId;
    public int SlotNumber;
    public ushort ChosenRoleId;
    public bool HasPicked;
    public bool IsWithdrawn;
    public bool IsPickingNow;
    public int TurnNumber;
    public int OfferRevision;
    public float TurnDuration;
    public float TurnTimeLeft;
    public List<ushort> ActiveOffers { get; } = new();
    public byte PendingPickIndex = 255;
    public ushort PendingPickRoleId;
    public int PendingPickTurnNumber = -1;
    public int PendingPickOfferRevision = -1;

    public void ClearPendingPick()
    {
        PendingPickIndex = 255;
        PendingPickRoleId = 0;
        PendingPickTurnNumber = -1;
        PendingPickOfferRevision = -1;
    }
}
