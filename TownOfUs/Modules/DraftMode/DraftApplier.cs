using AmongUs.GameOptions;
using MiraAPI.GameOptions;
using MiraAPI.GameEnd;
using MiraAPI.Roles;
using TownOfUs.GameOver;
using TownOfUs.Interfaces;
using TownOfUs.Options;
using TownOfUs.Options.Modifiers.Alliance;
using TownOfUs.Options.Roles.Neutral;
using TownOfUs.Options.Modifiers;
using TownOfUs.Roles;
using TownOfUs.Roles.Crewmate;
using TownOfUs.Roles.Neutral;
using TownOfUs.Roles.Other;

namespace TownOfUs.Modules.DraftMode;

public static class DraftApplier
{
    private sealed record Assignment(byte PlayerId, int ClientId, ushort RoleId);
    private static Assignment[] _pending = [];
    private static Dictionary<byte, int> _spectators = [];
    private static string _sessionId = string.Empty;
    private static int _hostId = -1;

    public static bool HasPendingDraft => _pending.Length != 0;
    public static bool StartupFailed { get; private set; }

    public static bool TryValidateConfiguration(out string reason)
    {
        reason = string.Empty;
        if (OptionGroupSingleton<AllianceModifierOptions>.Instance.CrewpostorChance.Value > 0 &&
            OptionGroupSingleton<CrewpostorOptions>.Instance.CrewpostorReplacesImpostor.Value)
        {
            reason = "Draft cancelled: Crewpostor replacement changes a chosen role. Turn off Crewpostor Replaces Impostor, set Crewpostor chance to 0, or use a non-draft distribution.";
            return false;
        }
        return true;
    }

    public static bool TryValidateTargets(IReadOnlyList<ushort> roleIds, out string reason)
    {
        var roles = new DraftStartupRole[roleIds.Count];
        var lovers = OptionGroupSingleton<LoversOptions>.Instance;
        for (var i = 0; i < roleIds.Count; i++)
        {
            var role = RoleManager.Instance.GetRole((RoleTypes)roleIds[i]);
            if (role == null || (ushort)role.Role != roleIds[i])
            {
                reason = "Draft cancelled: a chosen role is unavailable.";
                return false;
            }
            GetTargetMasks(role, out var targetMask, out var requiredMask);
            roles[i] = new DraftStartupRole(targetMask, requiredMask,
                (role is not IUnlovable unlovable || !unlovable.IsUnlovable) &&
                (lovers.NeutralLovers || role.IsCrewmate() || role.IsImpostor()),
                role.IsImpostor() || lovers.NeutralLovers && role.GetRoleAlignment() == RoleAlignment.NeutralKilling);
        }
        var modifiers = OptionGroupSingleton<AllianceModifierOptions>.Instance;
        var failure = DraftStartupRules.ValidateTargets(roles, modifiers.CrewpostorChance.Value > 0,
            modifiers.LoversChance.Value > 0, (int)lovers.LovingImpPercent);
        reason = failure switch
        {
            DraftStartupFailure.ExecutionerTarget => "Draft cancelled: Executioner needs an eligible crewmate target (not Power or Vigilante), plus another when Crewpostor is enabled. Change the role list or modifier options.",
            DraftStartupFailure.FairyTarget => "Draft cancelled: Fairy needs a target allowed by Evil Target Percent without taking Executioner's last target. Change the role list or target/modifier options.",
            DraftStartupFailure.LoversPair => "Draft cancelled: Lovers need two eligible distinct players after target/modifier reservations. Change the role list, Killer Probability, Neutral Lovers or modifier chances.",
            DraftStartupFailure.CrewpostorTarget => "Draft cancelled: Crewpostor needs an eligible crewmate. Add a role that can become Crewpostor or set Crewpostor chance to 0.",
            _ => string.Empty
        };
        return failure == DraftStartupFailure.None;
    }

    public static bool IsExecutionerTarget(RoleBehaviour role) => role.IsCrewmate() &&
        role.GetRoleAlignment() != RoleAlignment.CrewmatePower && role is not VigilanteRole;

    public static void GetTargetMasks(RoleBehaviour role, out int targetMask, out int requiredMask)
    {
        targetMask = IsExecutionerTarget(role) ? 1 : 0;
        if (role.IsCrewmate() && (role is not ILoyalCrewmate loyal || loyal.CanBeCrewpostor)) targetMask |= 8;
        if (role is not FairyRole &&
            role.GetRoleAlignment() is not (RoleAlignment.NeutralOutlier or RoleAlignment.NeutralEvil))
        {
            if (role.IsCrewmate()) targetMask |= 2;
            if (role.IsImpostor() || role.GetRoleAlignment() == RoleAlignment.NeutralKilling) targetMask |= 4;
        }
        var evilPercent = (int)OptionGroupSingleton<FairyOptions>.Instance.EvilTargetPercent;
        requiredMask = role switch
        {
            ExecutionerRole => 1,
            FairyRole => evilPercent == 0 ? 2 : evilPercent == 100 ? 4 : 6,
            _ => 0
        };
    }

    public static bool StorePendingDraftStates(IReadOnlyList<DraftSlotState> slotStates)
    {
        ClearPendingDraftStates();
        if (slotStates == null || slotStates.Count == 0 || string.IsNullOrEmpty(DraftManager.SessionId)) return false;
        var assignments = new List<Assignment>(slotStates.Count);
        var slots = new HashSet<int>();
        var players = new HashSet<byte>();
        foreach (var state in slotStates)
        {
            var player = MiscUtils.PlayerById(state.PlayerId);
            if (player == null || player.Data == null || player.Data.Disconnected ||
                !state.HasPicked || state.ChosenRoleId == 0 || !slots.Add(state.SlotNumber) ||
                !players.Add(state.PlayerId) || SpectatorRole.TrackedSpectators.Contains(state.PlayerId)) return false;
            assignments.Add(new Assignment(state.PlayerId, player.OwnerId, state.ChosenRoleId));
        }
        _pending = assignments.ToArray();
        _spectators = PlayerControl.AllPlayerControls.ToArray()
            .Where(player => player != null && player.Data != null && !player.Data.Disconnected &&
                SpectatorRole.TrackedSpectators.Contains(player.PlayerId))
            .ToDictionary(player => player.PlayerId, player => player.OwnerId);
        _sessionId = DraftManager.SessionId;
        _hostId = AmongUsClient.Instance.HostId;
        if (TryValidatePending(out _)) return true;
        ClearPendingDraftStates();
        return false;
    }

    public static bool TryValidatePending(out string reason)
    {
        reason = "Draft cancelled: pending roles, session or roster changed. Start a new draft.";
        if (!HasPendingDraft || _sessionId != DraftManager.SessionId || _hostId != AmongUsClient.Instance.HostId ||
            !AmongUsClient.Instance.AmHost ||
            OptionGroupSingleton<RoleOptions>.Instance.CurrentRoleDistribution() is not RoleDistribution.Draft) return false;
        var currentPlayers = PlayerControl.AllPlayerControls.ToArray()
            .Where(player => player != null && player.Data != null && !player.Data.Disconnected).ToArray();
        if (currentPlayers.Length != _pending.Length + _spectators.Count) return false;
        foreach (var player in currentPlayers)
        {
            if (_spectators.TryGetValue(player.PlayerId, out var owner))
            {
                if (owner != player.OwnerId || !SpectatorRole.TrackedSpectators.Contains(player.PlayerId)) return false;
            }
            else if (!_pending.Any(assignment => assignment.PlayerId == player.PlayerId && assignment.ClientId == player.OwnerId) ||
                     SpectatorRole.TrackedSpectators.Contains(player.PlayerId)) return false;
        }
        if (!TryValidateConfiguration(out reason)) return false;
        var roleIds = _pending.Select(assignment => assignment.RoleId).ToArray();
        if (!TryValidateTargets(roleIds, out reason)) return false;
        var feasibility = DraftPoolBuilder.BuildRules(_pending.Length).FindCompletion(roleIds, out _);
        if (feasibility == DraftFeasibility.Feasible) return true;
        reason = feasibility == DraftFeasibility.SearchLimit
            ? "Draft cancelled: changed role settings exceeded the search limit. Start a new draft."
            : "Draft cancelled: role-list buckets, quotas, enabled amounts or exclusions changed after selection. Start a new draft.";
        return false;
    }

    public static bool TryApplyPending(out string reason)
    {
        StartupFailed = false;
        if (!TryValidatePending(out reason)) return false;
        foreach (var assignment in _pending)
            MiscUtils.PlayerById(assignment.PlayerId)!.RpcSetRole((RoleTypes)assignment.RoleId);
        ClearPendingDraftStates();
        return true;
    }

    public static void ClearPendingDraftStates()
    {
        _pending = [];
        _spectators.Clear();
        _sessionId = string.Empty;
        _hostId = -1;
        Patches.DraftMode.GameStartPatch.ClearNativeStart();
    }

    public static void Abort(string reason)
    {
        StartupFailed = true;
        MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Error, reason);
        if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.Data != null)
            MiscUtils.AddSystemChat(PlayerControl.LocalPlayer.Data, "Draft", reason);
        DraftEngineBehaviour.Instance?.CancelDraft();
        ClearPendingDraftStates();
        if (AmongUsClient.Instance.AmHost && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started && GameManager.Instance != null)
            GameManager.Instance.RpcEndGame(CustomGameOver.GameOverReason<DrawGameOver>(), false);
    }
}
