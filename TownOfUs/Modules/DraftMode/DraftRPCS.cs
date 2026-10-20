using Hazel;
using Reactor.Networking.Attributes;
using Reactor.Networking.Rpc;
using UnityEngine;
using MiraAPI.Utilities;
using MiraAPI.GameOptions;
using MiraAPI.Hud;
using TownOfUs.Options;
using TownOfUs.Patches;

namespace TownOfUs.Modules.DraftMode;

public static class DraftRpcs
{
    [MethodRpc((uint)TownOfUsRpc.DraftSubmitPick)]
    public static void RpcSubmitPick(PlayerControl sender, string sessionId, int turnNumber, int offerRevision, int index, int roleId)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || sender == null ||
            roleId < ushort.MinValue || roleId > ushort.MaxValue ||
            !DraftManager.IsCurrentOffer(sessionId, sender.PlayerId, turnNumber, offerRevision)) return;
        var state = DraftManager.GetStateForPlayer(sender.PlayerId);
        if (index == state.ActiveOffers.Count && !(OptionGroupSingleton<RoleOptions>.Instance?.ShowRandomOption.Value ?? false)) return;
        DraftManager.SubmitPick(sessionId, sender.PlayerId, turnNumber, offerRevision, index, (ushort)roleId);
    }


    [MethodRpc((uint)TownOfUsRpc.DraftRequestShuffle)]
    public static void RpcRequestShuffle(PlayerControl sender, string sessionId, int turnNumber, int offerRevision)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || sender == null ||
            !DraftManager.IsCurrentOffer(sessionId, sender.PlayerId, turnNumber, offerRevision)) return;
        if (DraftManager.GetStateForPlayer(sender.PlayerId).PendingPickIndex != 255) return;
        DraftEngineBehaviour.Instance?.RequestShuffle(sender.PlayerId);
    }

    [MethodRpc((uint)TownOfUsRpc.DraftPlayerWithdrawn)]
    public static void RpcPlayerWithdrawn(PlayerControl sender, string sessionId, byte playerId)
    {
        if (!DraftManager.IsAuthoritative(sender, sessionId)) return;
        DraftManager.WithdrawPlayer(sessionId, sender.OwnerId, playerId);
    }

    [MethodRpc((uint)TownOfUsRpc.DraftCancel)]
    public static void RpcCancelDraft(PlayerControl sender, string sessionId)
    {
        if (!DraftManager.IsAuthoritative(sender, sessionId, requireActive: false)) return;
        DraftManager.Reset(cancelledBeforeCompletion: true);
    }

    [MethodRpc((uint)TownOfUsRpc.DraftBroadcastRecap)]
    public static void RpcBroadcastRecap(PlayerControl sender, string sessionId, string recapData)
    {
        if (!DraftManager.IsAuthoritative(sender, sessionId) || !DraftManager.BeginRecap()) return;
        var mode = DraftRecapMode.Nothing;
        var entries = new List<(int slot, string label, string colorHex)>();
        if (!string.IsNullOrEmpty(recapData))
        {
            var tokens = recapData.Split('|');
            if (int.TryParse(tokens[0], out var modeInt) && Enum.IsDefined(typeof(DraftRecapMode), modeInt))
            {
                mode = (DraftRecapMode)modeInt;
                for (var i = 1; i < tokens.Length; i++)
                {
                    var parts = tokens[i].Split(':');
                    if (parts.Length != 3 || !int.TryParse(parts[0], out var slot) || slot <= 0) continue;
                    entries.Add((slot, parts[1], parts[2]));
                }
            }
        }
        if (mode == DraftRecapMode.Nothing || entries.Count == 0)
        {
            DraftManager.Reset(cancelledBeforeCompletion: false);
            return;
        }
        DraftStatusOverlay.SetState(OverlayState.BackgroundOnly);
        DraftRecapScreen.Show(entries, mode, onComplete: () =>
        {
            if (DraftManager.SessionId == sessionId && DraftManager.IsRecapping)
                DraftManager.Reset(cancelledBeforeCompletion: false);
        });
    }
}

public sealed class DraftSessionSnapshot
{
    public string SessionId = string.Empty;
    public int HostId;
    public int TotalSlots;
    public List<byte> PlayerIds { get; } = [];
    public List<int> SlotNumbers { get; } = [];
}

[RegisterCustomRpc((uint)TownOfUsRpc.DraftStart)]
public sealed class DraftStartRpc(TownOfUsPlugin plugin, uint id)
    : PlayerCustomRpc<TownOfUsPlugin, DraftSessionSnapshot>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.Before;

    public override void Write(MessageWriter writer, DraftSessionSnapshot? data)
    {
        data ??= new DraftSessionSnapshot();
        writer.Write(data.SessionId);
        writer.Write(data.HostId);
        writer.Write(data.TotalSlots);
        writer.Write((byte)data.PlayerIds.Count);
        for (var i = 0; i < data.PlayerIds.Count; i++)
        {
            writer.Write(data.PlayerIds[i]);
            writer.Write(data.SlotNumbers[i]);
        }
    }

    public override DraftSessionSnapshot Read(MessageReader reader)
    {
        var data = new DraftSessionSnapshot
        {
            SessionId = reader.ReadString(), HostId = reader.ReadInt32(), TotalSlots = reader.ReadInt32()
        };
        var count = reader.ReadByte();
        for (var i = 0; i < count; i++)
        {
            data.PlayerIds.Add(reader.ReadByte());
            data.SlotNumbers.Add(reader.ReadInt32());
        }
        return data;
    }

    public override void Handle(PlayerControl innerNetObject, DraftSessionSnapshot? data)
    {
        if (data == null || innerNetObject == null || AmongUsClient.Instance == null ||
            innerNetObject.OwnerId != AmongUsClient.Instance.HostId || data.HostId != innerNetObject.OwnerId ||
            !DraftManager.BeginSession(data.SessionId, data.HostId, data.TotalSlots, data.PlayerIds, data.SlotNumbers)) return;
        DraftNetworkHelper.ActivateDraftUi();
    }
}

public sealed class DraftTurnAnnouncement
{
    public string SessionId = string.Empty;
    public int TurnNumber;
    public int OfferRevision;
    public float SecondsRemaining;
    public int ShufflesRemaining;
    public int Slot;
    public byte PickerId;
    public List<ushort> RoleIds { get; } = [];
    public List<string> RoleNames { get; } = [];
}

[RegisterCustomRpc((uint)TownOfUsRpc.DraftAnnounceTurn)]
public sealed class DraftAnnounceTurnRpc(TownOfUsPlugin plugin, uint id)
    : PlayerCustomRpc<TownOfUsPlugin, DraftTurnAnnouncement>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.Before;

    public override void Write(MessageWriter writer, DraftTurnAnnouncement? data)
    {
        data ??= new DraftTurnAnnouncement();
        writer.Write(data.SessionId);
        writer.Write(data.TurnNumber);
        writer.Write(data.OfferRevision);
        writer.Write(data.SecondsRemaining);
        writer.Write(data.ShufflesRemaining);
        writer.Write(data.Slot);
        writer.Write(data.PickerId);
        var count = (byte)Math.Min(data.RoleIds.Count, data.RoleNames.Count);
        writer.Write(count);
        for (var i = 0; i < count; i++)
        {
            writer.Write(data.RoleIds[i]);
            writer.Write(data.RoleNames[i]);
        }
    }

    public override DraftTurnAnnouncement Read(MessageReader reader)
    {
        var data = new DraftTurnAnnouncement
        {
            SessionId = reader.ReadString(), TurnNumber = reader.ReadInt32(), OfferRevision = reader.ReadInt32(),
            SecondsRemaining = reader.ReadSingle(), ShufflesRemaining = reader.ReadInt32(),
            Slot = reader.ReadInt32(), PickerId = reader.ReadByte()
        };
        var count = reader.ReadByte();
        for (var i = 0; i < count; i++)
        {
            data.RoleIds.Add(reader.ReadUInt16());
            data.RoleNames.Add(reader.ReadString());
        }
        return data;
    }

    public override void Handle(PlayerControl innerNetObject, DraftTurnAnnouncement? data)
    {
        if (data == null || data.ShufflesRemaining < 0 ||
            !DraftManager.IsAuthoritative(innerNetObject, data.SessionId)) return;
        var acceptance = DraftManager.AnnounceTurn(data.SessionId, innerNetObject.OwnerId, data.Slot, data.PickerId,
            data.TurnNumber, data.OfferRevision, data.RoleIds, data.SecondsRemaining);
        if (acceptance == DraftOfferAcceptance.Rejected) return;
        if (PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.PlayerId == data.PickerId)
        {
            CustomButtonSingleton<DraftShuffleButton>.Instance.SetUses(data.ShufflesRemaining);
            if (acceptance == DraftOfferAcceptance.Duplicate)
            {
                if (DraftScreenController.Instance != null) DraftScreenController.Instance.ShufflePending = false;
                return;
            }
            DraftAudio.PlayYourTurn();
            DraftScreenController.TargetPickerId = data.PickerId;
            DraftScreenController.Show(data.RoleIds.ToArray(), data.RoleNames.ToArray());
        }
        else if (acceptance == DraftOfferAcceptance.Accepted && DraftScreenController.Instance == null)
        {
            DraftStatusOverlay.SetState(OverlayState.Waiting);
        }
    }
}

public sealed class DraftPickConfirmedData
{
    public string SessionId = string.Empty;
    public int TurnNumber;
    public int OfferRevision;
    public int Slot;
    public ushort RoleId;
    public bool TimedOut;
}

[RegisterCustomRpc((uint)TownOfUsRpc.DraftPickConfirmed)]
public sealed class DraftPickConfirmedRpc(TownOfUsPlugin plugin, uint id)
    : PlayerCustomRpc<TownOfUsPlugin, DraftPickConfirmedData>(plugin, id)
{
    public override RpcLocalHandling LocalHandling => RpcLocalHandling.Before;

    public override void Write(MessageWriter writer, DraftPickConfirmedData? data)
    {
        data ??= new DraftPickConfirmedData();
        writer.Write(data.SessionId);
        writer.Write(data.TurnNumber);
        writer.Write(data.OfferRevision);
        writer.Write(data.Slot);
        writer.Write(data.RoleId);
        writer.Write(data.TimedOut);
    }

    public override DraftPickConfirmedData Read(MessageReader reader) => new()
    {
        SessionId = reader.ReadString(), TurnNumber = reader.ReadInt32(), OfferRevision = reader.ReadInt32(),
        Slot = reader.ReadInt32(), RoleId = reader.ReadUInt16(), TimedOut = reader.ReadBoolean()
    };

    public override void Handle(PlayerControl innerNetObject, DraftPickConfirmedData? data)
    {
        if (data == null || !DraftManager.IsAuthoritative(innerNetObject, data.SessionId)) return;
        DraftManager.ConfirmPick(data.SessionId, innerNetObject.OwnerId, data.Slot, data.TurnNumber, data.OfferRevision, data.RoleId);
    }
}

public static class DraftNetworkHelper
{
    public static void ActivateDraftUi()
    {
        HudManagerPatches.ResetZoom();
        DraftAudio.PlayDraftStart();
        DraftSidebarManager.Activate();
        DraftCancelButton.Show();
        DraftShuffleButton.ShowAndReset();
    }

    public static void SendPickToHost(int index, ushort roleId, byte pickerId = 255)
    {
        var localPlayer = PlayerControl.LocalPlayer;
        if (localPlayer == null || (pickerId != 255 && pickerId != localPlayer.PlayerId)) return;
        var state = DraftManager.GetStateForPlayer(localPlayer.PlayerId);
        if (state == null) return;
        DraftRpcs.RpcSubmitPick(localPlayer, DraftManager.SessionId, state.TurnNumber, state.OfferRevision, index, roleId);
    }

    public static void BroadcastSlotNotifications(string sessionId, int totalSlots, Dictionary<byte, int> pidToSlot)
    {
        if (!AmongUsClient.Instance.AmHost || sessionId != DraftManager.SessionId || pidToSlot == null) return;
        var snapshot = new DraftSessionSnapshot { SessionId = sessionId, HostId = AmongUsClient.Instance.HostId, TotalSlots = totalSlots };
        foreach (var pair in pidToSlot)
        {
            snapshot.PlayerIds.Add(pair.Key);
            snapshot.SlotNumbers.Add(pair.Value);
        }
        ActivateDraftUi();
        Rpc<DraftStartRpc>.Instance.Send(PlayerControl.LocalPlayer, snapshot);
    }

    public static void SendTurnAnnouncement(int slot, byte playerId, List<ushort> roleIds, List<string> roleNames,
        int turnNumber, int offerRevision, float secondsRemaining, int shufflesRemaining)
    {
        if (!AmongUsClient.Instance.AmHost || roleIds == null || roleNames == null || roleIds.Count != roleNames.Count) return;
        var announcement = new DraftTurnAnnouncement
        {
            SessionId = DraftManager.SessionId, TurnNumber = turnNumber, OfferRevision = offerRevision,
            SecondsRemaining = secondsRemaining, ShufflesRemaining = shufflesRemaining, Slot = slot, PickerId = playerId
        };
        announcement.RoleIds.AddRange(roleIds);
        announcement.RoleNames.AddRange(roleNames);
        Rpc<DraftAnnounceTurnRpc>.Instance.Send(PlayerControl.LocalPlayer, announcement);
    }

    public static void BroadcastPickConfirmed(int slot, ushort roleId, bool timedOut = false)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        var state = DraftManager.GetStateForSlot(slot);
        if (state == null) return;
        var confirmation = new DraftPickConfirmedData
        {
            SessionId = DraftManager.SessionId, TurnNumber = state.TurnNumber, OfferRevision = state.OfferRevision,
            Slot = slot, RoleId = roleId, TimedOut = timedOut
        };
        if (OptionGroupSingleton<RoleOptions>.Instance?.DraftSidebarDisplay.Value == DraftRecapMode.Nothing)
        {
            var picker = MiscUtils.PlayerById(state.PlayerId);
            var client = picker != null ? AmongUsClient.Instance.GetClient(picker.OwnerId) : null;
            if (client != null)
                Rpc<DraftPickConfirmedRpc>.Instance.SendTo(PlayerControl.LocalPlayer, client.Id, confirmation);
            confirmation = new DraftPickConfirmedData
            {
                SessionId = confirmation.SessionId, TurnNumber = confirmation.TurnNumber, OfferRevision = confirmation.OfferRevision,
                Slot = slot, RoleId = 0, TimedOut = timedOut
            };
        }
        Rpc<DraftPickConfirmedRpc>.Instance.Send(PlayerControl.LocalPlayer, confirmation);
    }


    public static void RequestShuffle()
    {
        var localPlayer = PlayerControl.LocalPlayer;
        if (localPlayer == null) return;
        var state = DraftManager.GetStateForPlayer(localPlayer.PlayerId);
        if (state == null) return;
        if (DraftScreenController.Instance == null || !DraftScreenController.Instance.CanRequestShuffle) return;
        DraftScreenController.Instance.ShufflePending = true;
        DraftRpcs.RpcRequestShuffle(localPlayer, DraftManager.SessionId, state.TurnNumber, state.OfferRevision);
    }

    public static void BroadcastPlayerWithdrawn(byte playerId)
    {
        if (AmongUsClient.Instance.AmHost)
            DraftRpcs.RpcPlayerWithdrawn(PlayerControl.LocalPlayer, DraftManager.SessionId, playerId);
    }

    public static void BroadcastCancelDraft()
    {
        if (AmongUsClient.Instance.AmHost)
            DraftRpcs.RpcCancelDraft(PlayerControl.LocalPlayer, DraftManager.SessionId);
    }

    public static void BroadcastRecap(List<RecapEntry> entries, DraftRecapMode mode)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        var recapData = ((int)mode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (mode != DraftRecapMode.Nothing && entries != null)
            recapData += "|" + string.Join("|", entries.Select(entry =>
                $"{entry.SlotNumber}:{(mode == DraftRecapMode.Role ? entry.RoleName : entry.TeamLabel).Replace(":", "").Replace("|", "")}:{entry.ColorHex}"));
        DraftRpcs.RpcBroadcastRecap(PlayerControl.LocalPlayer, DraftManager.SessionId, recapData);
    }
}
