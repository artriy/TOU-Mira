using System.Collections;
using AmongUs.GameOptions;
using Il2CppInterop.Runtime.Attributes;
using MiraAPI.GameOptions;
using Reactor.Utilities;
using Reactor.Utilities.Attributes;
using TownOfUs.Options;
using TownOfUs.Patches.DraftMode;
using UnityEngine;

namespace TownOfUs.Modules.DraftMode;

[RegisterInIl2Cpp]
public class DraftEngineBehaviour(IntPtr iPtr) : MonoBehaviour(iPtr)
{
    public static DraftEngineBehaviour Instance { get; private set; } = null!;
    public bool IsRunning => _running;
    public bool HasPendingStart => _pendingStart;
    private bool _running;
    private bool _pendingStart;
    private string _sessionId = string.Empty;
    private int _hostId;
    private int _turn;
    private IEnumerator? _sessionCoroutine;
    private IRng _rng = null!;
    private DraftRules _rules = null!;
    private readonly Dictionary<byte, int> _participants = new();
    private readonly Dictionary<byte, int> _owners = new();
    private readonly Dictionary<byte, ushort> _accepted = new();
    private readonly Dictionary<int, List<ushort>> _offers = new();
    private readonly Dictionary<int, int> _revisions = new();
    private readonly Dictionary<int, float> _deadlines = new();
    private readonly Dictionary<int, int> _shuffles = new();
    private readonly List<KeyValuePair<byte, int>> _pendingPlayers = new(15);
    private readonly List<int> _activeSlots = new(2);
    private List<ushort> _legal = new();
    private float _nextDisconnectCheck;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        CancelDraft();
        Instance = null!;
    }

    [HideFromIl2Cpp]
    public void StartHostDraft(int totalSlots, Dictionary<byte, int> pidToSlot)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (_running || _pendingStart) CancelDraft();
        _sessionId = Guid.NewGuid().ToString("N");
        _hostId = AmongUsClient.Instance.HostId;
        _participants.Clear();
        _owners.Clear();
        _accepted.Clear();
        _offers.Clear();
        _revisions.Clear();
        _deadlines.Clear();
        _shuffles.Clear();
        _turn = 0;
        _rng = DeterministicRng.CreateRandomlySeeded();
        if (pidToSlot.Count is < 1 or > 15 || totalSlots != pidToSlot.Count ||
            pidToSlot.Values.Distinct().Count() != pidToSlot.Count || pidToSlot.Values.Any(slot => slot < 1))
        {
            FailDraft("The draft roster must contain 1 to 15 players with distinct slots.");
            return;
        }
        foreach (var pair in pidToSlot)
        {
            var player = MiscUtils.PlayerById(pair.Key);
            if (player == null || player.Data == null || player.Data.Disconnected)
            { FailDraft("A draft participant disconnected before initialization."); return; }
            _participants.Add(pair.Key, pair.Value);
            _owners.Add(pair.Key, player.OwnerId);
        }
        if (!DraftManager.BeginSession(_sessionId, _hostId, totalSlots, pidToSlot.Keys.ToList(), pidToSlot.Values.ToList()))
        {
            FailDraft("The draft session could not be initialized. Start a new draft.");
            return;
        }
        DraftNetworkHelper.BroadcastSlotNotifications(_sessionId, totalSlots, pidToSlot);
        if (!DraftApplier.TryValidateConfiguration(out var reason)) { FailDraft(reason); return; }
        _rules = DraftPoolBuilder.BuildRules(_participants.Count);
        if (!RefreshLegalCandidates()) return;
        _running = true;
        _nextDisconnectCheck = Time.time;
        DraftCancelButton.Show();
        _sessionCoroutine = HostDraftLoop(_sessionId);
        Coroutines.Start(_sessionCoroutine);
    }

    [HideFromIl2Cpp]
    private bool IsCurrentSession(string sessionId) => _sessionId == sessionId &&
        DraftManager.SessionId == sessionId && (!_running || DraftManager.IsDraftActive) && AmongUsClient.Instance != null &&
        AmongUsClient.Instance.AmHost && AmongUsClient.Instance.HostId == _hostId &&
        AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Joined;

    [HideFromIl2Cpp]
    private IEnumerator HostDraftLoop(string sessionId)
    {
        while (_running && IsCurrentSession(sessionId))
        {
            if (Time.time >= _nextDisconnectCheck)
            {
                _nextDisconnectCheck = Time.time + 0.25f;
                if (!WithdrawDisconnectedPlayers()) yield break;
            }
            if (_accepted.Count == _participants.Count) break;
            if (_offers.Count == 0)
            {
                _pendingPlayers.Clear();
                foreach (var pair in _participants)
                    if (!_accepted.ContainsKey(pair.Key)) _pendingPlayers.Add(pair);
                _pendingPlayers.Sort(static (left, right) => left.Value.CompareTo(right.Value));
                _turn++;
                var concurrency = Math.Clamp((int)OptionGroupSingleton<RoleOptions>.Instance.ConcurrentPicks.Value, 1, 2);
                for (var i = 0; i < Math.Min(concurrency, _pendingPlayers.Count); i++)
                {
                    if (!AnnounceOffers(_pendingPlayers[i].Key, false)) yield break;
                }
            }
            _activeSlots.Clear();
            _activeSlots.AddRange(_offers.Keys);
            foreach (var slot in _activeSlots)
            {
                var state = DraftManager.GetStateForSlot(slot);
                if (state == null || state.IsWithdrawn) continue;
                var remaining = Math.Max(0f, _deadlines[slot] - Time.time);
                state.TurnTimeLeft = remaining;
                if (remaining <= 0f) CommitPick(state, ChooseWeighted(_offers[slot]), true);
                if (!_running) yield break;
            }
            yield return null;
        }
        if (!_running) yield break;
        if (!IsCurrentSession(sessionId)) { CancelDraft(); yield break; }
        _running = false;
        var states = DraftManager.GetAllStates();
        if (states.Count != _participants.Count || states.Any(state => !state.HasPicked || state.ChosenRoleId == 0) ||
            !DraftApplier.StorePendingDraftStates(states))
        {
            FailDraft("The completed draft no longer matches the active roster. Start a new draft.");
            yield break;
        }
        _pendingStart = true;
        var recapMode = OptionGroupSingleton<RoleOptions>.Instance.DraftRecap.Value;
        var entries = new List<RecapEntry>();
        foreach (var state in states)
        {
            var role = MiscUtils.GetRegisteredRole((RoleTypes)state.ChosenRoleId);
            var roleName = DraftRolePool.GetRoleNameFromId(state.ChosenRoleId);
            var label = recapMode == DraftRecapMode.Role ? roleName : DraftUiManager.GetTeamLabel(role);
            var color = recapMode == DraftRecapMode.Role ? role.TeamColor : MiscUtils.GetRoleFactionColor(role, true);
            entries.Add(new RecapEntry(state.SlotNumber, roleName, label.ToUpperInvariant(), ColorUtility.ToHtmlStringRGB(color)));
        }
        DraftNetworkHelper.BroadcastRecap(entries, recapMode);
        var recapDeadline = Time.time + (recapMode == DraftRecapMode.Nothing ? 0f : 6f);
        while (_pendingStart && IsCurrentSession(sessionId) && Time.time < recapDeadline) yield return null;
        if (!_pendingStart) yield break;
        if (!IsCurrentSession(sessionId)) { CancelDraft(); yield break; }
        if (!GameStartPatch.TryBeginDraftGame(sessionId, out var reason)) FailDraft(reason);
        _sessionCoroutine = null;
    }

    [HideFromIl2Cpp]
    private bool WithdrawDisconnectedPlayers()
    {
        var disconnected = _participants.Keys.Where(playerId =>
            !HasOriginalOwner(playerId) || DraftManager.IsPlayerDisconnected(playerId)).ToArray();
        if (disconnected.Length == 0) return true;
        foreach (var playerId in disconnected)
        {
            var slot = _participants[playerId];
            _participants.Remove(playerId);
            _owners.Remove(playerId);
            _accepted.Remove(playerId);
            _offers.Remove(slot);
            _deadlines.Remove(slot);
            DraftNetworkHelper.BroadcastPlayerWithdrawn(playerId);
        }
        if (_participants.Count == 0) { FailDraft("All draft participants disconnected."); return false; }
        _rules = DraftPoolBuilder.BuildRules(_participants.Count);
        if (!RefreshLegalCandidates()) return false;
        return RefreshActiveOffers();
    }

    [HideFromIl2Cpp]
    private bool HasOriginalOwner(byte playerId) => _owners.TryGetValue(playerId, out var owner) &&
        MiscUtils.PlayerById(playerId) is { } player && player.OwnerId == owner;

    [HideFromIl2Cpp]
    private bool RefreshLegalCandidates()
    {
        var accepted = _accepted.Values.ToArray();
        if (accepted.Length == _rules.BucketCount)
        {
            var status = _rules.FindCompletion(accepted, out _);
            if (status != DraftFeasibility.Feasible) { FailFeasibility(status); return false; }
            _legal.Clear();
            return true;
        }
        _legal = _rules.GetLegalCandidates(accepted, out var feasibility);
        if (feasibility == DraftFeasibility.Feasible && _legal.Count > 0) return true;
        FailFeasibility(feasibility);
        return false;
    }

    [HideFromIl2Cpp]
    private void FailFeasibility(DraftFeasibility feasibility) => FailDraft(feasibility == DraftFeasibility.SearchLimit
        ? "Draft configuration is too constrained to resolve within the search limit. Reduce exclusions or use narrower role buckets."
        : "Draft configuration has no legal completion. Check active role-list slots, enabled role amounts, faction totals, subtype limits, exclusions and target roles.");

    [HideFromIl2Cpp]
    private bool AnnounceOffers(byte playerId, bool refresh)
    {
        if (!_participants.TryGetValue(playerId, out var slot) || _accepted.ContainsKey(playerId)) return false;
        var state = DraftManager.GetStateForPlayer(playerId);
        if (state == null || state.IsWithdrawn) { FailDraft("A draft participant lost their slot."); return false; }
        var previous = _offers.GetValueOrDefault(slot);
        var remaining = new List<ushort>(_legal);
        var selected = new List<ushort>();
        var wanted = Math.Max(1, (int)OptionGroupSingleton<RoleOptions>.Instance.OfferedRolesCount.Value);
        while (selected.Count < wanted && remaining.Count > 0)
        {
            var guaranteed = remaining.Where(id => GetRole(id).Chance >= 100).ToList();
            var tier = guaranteed.Count > 0 ? guaranteed : remaining;
            var fresh = previous == null ? tier : tier.Where(id => !previous.Contains(id)).ToList();
            var chosen = ChooseWeighted(fresh.Count > 0 ? fresh : tier);
            selected.Add(chosen);
            remaining.Remove(chosen);
        }
        if (selected.Count == 0) { FailDraft("No legal draft offers remain."); return false; }
        _offers[slot] = selected;
        _revisions[slot] = _revisions.GetValueOrDefault(slot) + 1;
        var duration = Math.Max(5f, OptionGroupSingleton<RoleOptions>.Instance.TurnDurationSeconds.Value);
        _deadlines[slot] = refresh ? Math.Max(_deadlines.GetValueOrDefault(slot), Time.time + 5f) : Time.time + duration;
        var names = selected.Select(id => DraftRolePool.GetRoleNameFromId(id)).ToList();
        var shufflesRemaining = Math.Max(0, (int)OptionGroupSingleton<RoleOptions>.Instance.ShufflesPerPlayer.Value - _shuffles.GetValueOrDefault(slot));
        DraftNetworkHelper.SendTurnAnnouncement(slot, playerId, selected, names, _turn, _revisions[slot],
            _deadlines[slot] - Time.time, shufflesRemaining);
        return true;
    }

    [HideFromIl2Cpp]
    private DraftRuleRole GetRole(ushort id) => _rules.GetRole(id);

    [HideFromIl2Cpp]
    private ushort ChooseWeighted(List<ushort> candidates)
    {
        var total = 0;
        foreach (var id in candidates) total += GetRole(id).Chance;
        var roll = _rng.NextInt(total);
        foreach (var id in candidates)
        {
            roll -= GetRole(id).Chance;
            if (roll < 0) return id;
        }
        throw new InvalidOperationException("A draft choice requires a nonempty weighted offer.");
    }

    [HideFromIl2Cpp]
    public void TryApplySubmittedPick(byte playerId, byte index, ushort roleId)
    {
        if (!_running || !IsCurrentSession(_sessionId) || !HasOriginalOwner(playerId) || _accepted.ContainsKey(playerId)) return;
        var state = DraftManager.GetStateForPlayer(playerId);
        if (state == null || !state.IsPickingNow || state.IsWithdrawn || state.TurnNumber != _turn ||
            !_offers.TryGetValue(state.SlotNumber, out var offered) || state.OfferRevision != _revisions[state.SlotNumber]) return;
        if (!_deadlines.TryGetValue(state.SlotNumber, out var deadline) || Time.time >= deadline) return;
        var random = roleId == 0 && index == offered.Count && OptionGroupSingleton<RoleOptions>.Instance.ShowRandomOption.Value;
        if (!random && (roleId == 0 || index >= offered.Count || offered[index] != roleId)) return;
        CommitPick(state, random ? ChooseWeighted(offered) : roleId, false);
    }

    [HideFromIl2Cpp]
    private void CommitPick(DraftSlotState state, ushort roleId, bool timedOut)
    {
        if (!HasOriginalOwner(state.PlayerId) || DraftManager.IsPlayerDisconnected(state.PlayerId)) return;
        if (!_running || roleId == 0 || !_participants.ContainsKey(state.PlayerId) || _accepted.ContainsKey(state.PlayerId)) return;
        if (!_offers.TryGetValue(state.SlotNumber, out var offered) || !offered.Contains(roleId) || !_legal.Contains(roleId))
        { AnnounceOffers(state.PlayerId, true); return; }
        _rules = DraftPoolBuilder.BuildRules(_participants.Count);
        var proposal = _accepted.Values.Append(roleId).ToArray();
        var feasibility = _rules.FindCompletion(proposal, out _);
        if (feasibility != DraftFeasibility.Feasible) { FailFeasibility(feasibility); return; }
        if (!DraftManager.ConfirmPick(state.SlotNumber, roleId))
        {
            FailDraft("The draft confirmation no longer matches the current offer.");
            return;
        }
        _accepted.Add(state.PlayerId, roleId);
        _offers.Remove(state.SlotNumber);
        _deadlines.Remove(state.SlotNumber);
        DraftNetworkHelper.BroadcastPickConfirmed(state.SlotNumber, roleId, timedOut);
        if (!RefreshLegalCandidates()) return;
        RefreshActiveOffers();
    }

    [HideFromIl2Cpp]
    private bool RefreshActiveOffers()
    {
        foreach (var slot in _offers.Keys.ToArray())
        {
            if (_offers[slot].All(_legal.Contains)) continue;
            var state = DraftManager.GetStateForSlot(slot);
            if (state == null || !AnnounceOffers(state.PlayerId, true)) return false;
        }
        return true;
    }

    public void RequestShuffle(byte playerId, bool consumeUse = true)
    {
        if (!_running || !IsCurrentSession(_sessionId) || !_participants.TryGetValue(playerId, out var slot) ||
            !HasOriginalOwner(playerId) || _accepted.ContainsKey(playerId) || !_offers.ContainsKey(slot)) return;
        if (!_deadlines.TryGetValue(slot, out var deadline) || Time.time >= deadline) return;
        if (consumeUse)
        {
            var max = Math.Max(0, (int)OptionGroupSingleton<RoleOptions>.Instance.ShufflesPerPlayer.Value);
            if (_shuffles.GetValueOrDefault(slot) >= max)
            {
                var state = DraftManager.GetStateForPlayer(playerId);
                DraftNetworkHelper.SendTurnAnnouncement(slot, playerId, _offers[slot],
                    _offers[slot].Select(id => DraftRolePool.GetRoleNameFromId(id)).ToList(),
                    state.TurnNumber, state.OfferRevision, deadline - Time.time, 0);
                return;
            }
            _shuffles[slot] = _shuffles.GetValueOrDefault(slot) + 1;
        }
        _rules = DraftPoolBuilder.BuildRules(_participants.Count);
        if (!RefreshLegalCandidates()) return;
        AnnounceOffers(playerId, true);
        RefreshActiveOffers();
    }

    public void CompletePendingStart() => _pendingStart = false;

    [HideFromIl2Cpp]
    private void FailDraft(string reason)
    {
        MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Error, $"[DraftEngine] {reason}");
        if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.Data != null)
            MiscUtils.AddSystemChat(PlayerControl.LocalPlayer.Data, "Draft", $"Draft cancelled: {reason}");
        CancelDraft();
    }

    public void CancelDraft()
    {
        var sessionId = _sessionId;
        var ownedSession = !string.IsNullOrEmpty(sessionId) && DraftManager.SessionId == sessionId;
        _running = false;
        _pendingStart = false;
        DraftApplier.ClearPendingDraftStates();
        if (_sessionCoroutine != null)
        {
            Coroutines.Stop(_sessionCoroutine);
            _sessionCoroutine = null;
        }
        _offers.Clear();
        _deadlines.Clear();
        _legal.Clear();
        if (!ownedSession) return;
        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && AmongUsClient.Instance.HostId == _hostId)
            DraftNetworkHelper.BroadcastCancelDraft();
        if (DraftManager.SessionId == sessionId) DraftManager.Reset(cancelledBeforeCompletion: true);
    }
}
