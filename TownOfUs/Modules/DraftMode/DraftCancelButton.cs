using MiraAPI.Hud;
using TownOfUs.Buttons;
using UnityEngine;

namespace TownOfUs.Modules.DraftMode;

public sealed class DraftCancelButton : TownOfUsButton
{
    public static void Show()
    {
        CustomButtonSingleton<DraftCancelButton>.Instance.Disabled = false;
    }

    public static void Hide()
    {
        CustomButtonSingleton<DraftCancelButton>.Instance.Disabled = true;
    }

    public override string Name => MiraLocaleManager.Get("TouDraftCancelButton", "Cancel Draft");
    public override float InitialCooldown => 0.001f;
    public override float Cooldown => 0.001f;

    public override bool ZeroIsInfinite { get; set; } = true;
    public override ButtonLocation Location => ButtonLocation.BottomRight;

    public override Color TextOutlineColor => TownOfUsColors.Impostor;

    public override LoadableAsset<Sprite> Sprite => TouImpAssets.HexBombSprite;

    public override bool Disabled { get; set; } = true;

    public override bool Enabled(RoleBehaviour? role)
    {
        return AmongUsClient.Instance.AmHost && !Disabled;
    }

    public override bool CanUse()
    {
        return base.CanUse() && DraftManager.IsDraftActive && !Disabled;
    }

    protected override void OnClick()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!DraftManager.IsDraftActive) return;
        DraftEngineBehaviour.Instance?.CancelDraft();
        Hide();
    }
}

