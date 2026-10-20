using HarmonyLib;
using Il2CppInterop.Runtime;
using MiraAPI.GameOptions;
using TownOfUs.Modules.DraftMode;
using TownOfUs.Options;
using TownOfUs.Roles.Other;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace TownOfUs.Patches.DraftMode
{
    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
    public static class GameStartPatch
    {
        private static string _nativeStartSession = string.Empty;

        public static bool OwnsNativeStart => !string.IsNullOrEmpty(_nativeStartSession) &&
            _nativeStartSession == DraftManager.SessionId && DraftEngineBehaviour.Instance?.HasPendingStart == true;

        public static bool TryBeginDraftGame(string sessionId, out string reason)
        {
            reason = "Draft cancelled: the host or lobby changed before game start.";
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
                AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined ||
                !GameStartManager.InstanceExists || sessionId != DraftManager.SessionId ||
                DraftEngineBehaviour.Instance?.HasPendingStart != true) return false;
            if (!DraftApplier.TryValidatePending(out reason)) return false;
            if (_nativeStartSession == sessionId)
            {
                reason = "Draft native countdown has already started.";
                return false;
            }
            _nativeStartSession = sessionId;
            GameStartManager.Instance.ReallyBegin(false);
            return true;
        }

        public static void ClearNativeStart()
        {
            _nativeStartSession = string.Empty;
        }

        [HarmonyPrefix]
        public static bool Prefix(GameStartManager __instance, out bool __state)
        {
            __state = true;

            if (!AmongUsClient.Instance.AmHost)
            {
                MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Info, "[GameStartPatch] Not host, allowing normal start");
                return true;
            }

            if (DraftEngineBehaviour.Instance?.HasPendingStart == true)
            {
                __state = false;
                if (OwnsNativeStart && DraftApplier.TryValidatePending(out _))
                    return false;
                DraftApplier.Abort("Draft cancelled: the pending draft cannot start. Start a new draft.");
                return false;
            }

            if (DraftManager.IsDraftActive)
            {
                MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Warning, "[GameStartPatch] Draft already active, blocking start");
                __state = false;
                return false;
            }
            var roleOpts = OptionGroupSingleton<RoleOptions>.Instance;
            if (roleOpts == null)
            {
                MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Warning, "[GameStartPatch] RoleOptions not found");
                return true;
            }

            var distrib = roleOpts.CurrentRoleDistribution();
            if (distrib is not RoleDistribution.Draft)
            {
                MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Info, $"[GameStartPatch] Not draft mode (distribution: {distrib}), allowing normal start");
                return true;
            }

            MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Info, "[GameStartPatch] DRAFT MODE DETECTED - Starting draft");

            var players = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => p != null && p.Data != null && !p.Data.Disconnected && !SpectatorRole.TrackedSpectators.Contains(p.PlayerId))
                .ToList();

            __state = false;
            if (!DraftApplier.TryValidateConfiguration(out var configError))
            {
                DraftApplier.Abort(configError);
                return false;
            }
            if (players.Count == 0)
            {
                DraftApplier.Abort("Draft cancelled: no eligible participants. Disable spectator mode for at least one player.");
                return false;
            }

            MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Info, $"[GameStartPatch] Starting draft with {players.Count} players");
            var shuffledSlots = Enumerable.Range(1, players.Count)
                .OrderBy(_ => Random.value)
                .ToList();
            var pidToSlot = new Dictionary<byte, int>();
            for (int i = 0; i < players.Count; i++)
                pidToSlot[players[i].PlayerId] = shuffledSlots[i];
            var engine = DraftEngineBehaviour.Instance;
            if (engine == null)
            {
                MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Info, "[GameStartPatch] Creating new DraftEngineBehaviour");
                var go = new GameObject("DraftEngineBehaviour");
                Object.DontDestroyOnLoad(go);
                engine = go.AddComponent(Il2CppType.From(typeof(DraftEngineBehaviour))).TryCast<DraftEngineBehaviour>()!;
            }

            if (engine == null)
            {
                MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Error, "[GameStartPatch] Failed to create DraftEngineBehaviour");
                return false;
            }

            engine.StartHostDraft(players.Count, pidToSlot);
            MiscUtils.LogInfo(Events.TownOfUsEventHandlers.LogLevel.Info, "[GameStartPatch] Draft started, blocking normal game start");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(GameStartManager __instance, bool __state)
        {
            if (!__state)
            {
                return;
            }

            DraftScreenController.Hide();
            DraftStatusOverlay.SetState(OverlayState.Hidden);
            DraftCancelButton.Hide();

        }
    }

    [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ReallyBegin))]
    public static class DraftReallyBeginPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !DraftApplier.HasPendingDraft && !DraftManager.IsDraftActive &&
                OptionGroupSingleton<RoleOptions>.Instance.CurrentRoleDistribution() is not RoleDistribution.Draft)
                return true;
            if (GameStartPatch.OwnsNativeStart && DraftApplier.TryValidatePending(out _)) return true;
            DraftApplier.Abort("Draft cancelled: native game start has no valid session-owned draft. Start a new draft.");
            return false;
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.StartGame))]
    public static class DraftNativeStartGamePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !DraftApplier.HasPendingDraft && !DraftManager.IsDraftActive &&
                OptionGroupSingleton<RoleOptions>.Instance.CurrentRoleDistribution() is not RoleDistribution.Draft)
                return true;
            if (GameStartPatch.OwnsNativeStart && DraftApplier.TryValidatePending(out _)) return true;
            DraftApplier.Abort("Draft cancelled: roster or session changed during native countdown. Start a new draft.");
            if (GameStartManager.InstanceExists) GameStartManager.Instance.ResetStartState();
            return false;
        }
    }

    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    public static class DraftIntroCutsceneBeginPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            DraftScreenController.Hide();
            DraftStatusOverlay.SetState(OverlayState.Hidden);
            DraftCancelButton.Hide();
        }
    }

    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
    public static class DraftShipStatusStartPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            DraftScreenController.Hide();
            DraftStatusOverlay.SetState(OverlayState.Hidden);
            DraftCancelButton.Hide();
        }
    }
}