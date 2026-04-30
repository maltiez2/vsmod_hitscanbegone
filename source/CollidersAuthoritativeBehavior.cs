using CollidersLib.Items;
using OpenTK.Mathematics;
using OverhaulLib.Utils;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace hitscanbegone.source;

public class CollidersAuthoritativeBehavior : CollectibleBehaviorAnimationAuthoritative
{
    public CollidersAuthoritativeBehavior(CollectibleObject collObj) : base(collObj)
    {
    }

    public static bool IgnoreTerrainBehind { get; set; } = true;
    public static bool CollideWithTerrain { get; set; } = false;
    public static bool StopOnTerrainHit { get; set; } = false;
    public static bool HitOnlyOneEntity { get; set; } = true;
    public static bool StopOnEntityHit { get; set; } = false;
    public int[] CollidersOrder { get; set; } = [0];
    public float ForcedDurationSeconds { get; set; } = 1;


    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        ClientColliders = collObj?.GetCollectibleBehavior<ItemCollidersBehaviorClient>(true);
        ServerColliders = collObj?.GetCollectibleBehavior<ItemCollidersBehaviorServer>(true);
        if (ServerColliders != null && api.Side == EnumAppSide.Server)
        {
            ServerColliders.OnCollision += OnCollisionDetected;
        }
        if (ClientColliders != null && api.Side == EnumAppSide.Client)
        {
            ClientColliders.OnCollision += OnCollisionDetected;
            CollidersOrder = ClientColliders.Colliders.Keys.ToArray();
        }
    }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);

        CollidersOrder = properties["collidersPriority"].AsObject(CollidersOrder);
    }

    public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (byEntity is not EntityPlayer player) return;
        StartAttackColliders(slot, byEntity);
        handling = EnumHandling.PreventSubsequent;
        handHandling = EnumHandHandling.PreventDefault;
        ClientColliders?.ResetColliders(player, slot);
    }

    public override bool OnHeldAttackStep(float secondsPassed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (byEntity is not EntityPlayer player) return false;
        handling = EnumHandling.PreventSubsequent;
        bool result = StepAttackColliders(slot, player, secondsPassed);
        return result;
    }

    public override bool OnHeldAttackCancel(float secondsPassed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handling)
    {
        handling = EnumHandling.PreventSubsequent;
        return false;
    }



    public virtual void StartAttackColliders(ItemSlot slot, EntityAgent byEntity)
    {
        string animationCode = collObj.GetHeldTpHitAnimation(slot, byEntity);

        byEntity.Attributes.SetInt("didattack", 0);

        byEntity.AnimManager.RegisterFrameCallback(new AnimFrameCallback()
        {
            Animation = animationCode,
            Frame = getSoundAtFrame(byEntity, animationCode),
            Callback = () => playStrikeSound(byEntity)
        });

        byEntity.AnimManager.RegisterFrameCallback(new AnimFrameCallback()
        {
            Animation = animationCode,
            Frame = getHitDamageAtFrame(byEntity, animationCode),
            Callback = () => HitEntity(byEntity)
        });
    }

    public virtual bool StepAttackColliders(ItemSlot slot, EntityPlayer byEntity, float secondsPassed)
    {
        if (ClientColliders != null && byEntity.Api.Side == EnumAppSide.Client && ActiveByPlayer.ContainsKey(byEntity.EntityId) && ActiveByPlayer[byEntity.EntityId])
        {
            _ = ClientColliders.CheckForCollisions(byEntity, byEntity.ActiveHandItemSlot);
        }

        if (secondsPassed < ForcedDurationSeconds) return true;

        string animationCode = collObj.GetHeldTpHitAnimation(slot, byEntity);
        bool result = byEntity.AnimManager.IsAnimationActive(animationCode);
        if (!result)
        {
            ActiveByPlayer[byEntity.EntityId] = false;
        }
        return result || byEntity.Api.Side == EnumAppSide.Server;
    }

    public virtual void HitEntity(EntityAgent byEntity)
    {
        if (ClientColliders != null && byEntity.Api.Side == EnumAppSide.Client)
        {
            ActiveByPlayer[byEntity.EntityId] = true;
        }
    }



    protected ItemCollidersBehaviorClient? ClientColliders;
    protected ItemCollidersBehaviorServer? ServerColliders;
    protected List<long> AttackedEntities = [];
    protected Dictionary<long, bool> ActiveByPlayer = [];


    protected virtual bool AttackEntity(EntityAgent byEntity, Entity target, Vector3d nitPosition)
    {
        long selectedEntityId = target.EntityId;
        long mountEntityId = byEntity.MountedOn?.Entity?.EntityId ?? 0;
        long selfEntityId = byEntity.EntityId;

        if (selectedEntityId == mountEntityId || selectedEntityId == selfEntityId)
        {
            return false;
        }

        if (byEntity.World is IClientWorldAccessor world && byEntity.Attributes.GetInt("didattack") == 0)
        {
            EntitySelection selection = new()
            {
                Entity = target,
                HitPosition = new(nitPosition.X, nitPosition.Y, nitPosition.Z),
                Position = target.Pos.XYZ,
                Face = BlockFacing.DOWN,
                SelectionBoxIndex = 0
            };

            world.TryAttackEntity(selection);
            byEntity.Attributes.SetInt("didattack", 1);
            world.AddCameraShake(0.25f);
            return true;
        }
        else if (byEntity.Attributes.GetInt("didattack") == 0 && selectedEntityId != mountEntityId)
        {
            byEntity.Attributes.SetInt("didattack", 1);
            return true;
        }

        return false;
    }
    protected virtual void OnCollisionDetected(EntityPlayer byEntity, ItemSlot inSlot, List<ColliderItemCollisionData> collisions)
    {
        List<SingleItemCollisionData> sorted = ClientColliders?.SortCollisions(byEntity, collisions, [0, 1]) ?? [];
        List<SingleItemCollisionData> validatedCollisions = ValidateCollisions(sorted);

        if (validatedCollisions.Count == 0) return;

        foreach (SingleItemCollisionData collision in validatedCollisions)
        {
            if (collision.Target == null || collision.EntityCollision == null) break;

            if (AttackEntity(byEntity, collision.Target, collision.EntityCollision.Value.IntersectionPoint))
            {
                return;
            }
        }
    }
    protected virtual List<SingleItemCollisionData> ValidateCollisions(List<SingleItemCollisionData> collisionsSorted)
    {
        List<SingleItemCollisionData> collisions = [];
        bool hitTerrain = false;

        foreach (SingleItemCollisionData collision in collisionsSorted)
        {
            if (collision.EntityCollision != null && collision.BehindTerrain)
            {
                continue;
            }

            if (collision.TerrainCollision != null && CollideWithTerrain)
            {
                collisions.Add(collision);
                hitTerrain = true;
                if (StopOnTerrainHit)
                {
                    break;
                }
            }
            else if (!hitTerrain && collision.Target != null && collision.EntityCollision != null)
            {
                collisions.Add(collision);

                if (StopOnEntityHit)
                {
                    break;
                }
            }
        }

        return collisions;
    }
}
