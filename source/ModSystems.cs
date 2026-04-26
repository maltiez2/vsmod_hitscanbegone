using hitscanbegone.source;
using Vintagestory.API.Common;

namespace HitScanBegone;

public sealed class HitScanBegoneSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.RegisterCollectibleBehaviorClass("HitScanBegone:CollidersAuthoritativeBehavior", typeof(CollidersAuthoritativeBehavior));
    }
}