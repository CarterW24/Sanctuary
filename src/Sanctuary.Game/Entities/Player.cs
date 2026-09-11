using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Sanctuary.Core.Collections;
using Sanctuary.Core.IO;
using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Helpers;
using Sanctuary.Game.Interactions;
using Sanctuary.Game.Resources.Definitions.Combat;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Chat;
using Sanctuary.Packet.Common.Targets;
using Sanctuary.UdpLibrary;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.Game.Entities;

public sealed class Player : ClientPcData, IEntity
{
    private readonly UdpConnection _connection;
    private readonly IResourceManager _resourceManager;

    public bool Visible { get; set; }

    public IZone Zone { get; set; }
    public ZoneTile ZoneTile { get; private set; } = ZoneTile.Empty;
    public ConcurrentDictionary<ulong, Npc> VisibleNpcs { get; } = [];
    public ConcurrentDictionary<ulong, Player> VisiblePlayers { get; } = [];

    private int ZoneAreaId { get; set; }

    public int ChatBubbleForegroundColor { get; set; }
    public int ChatBubbleBackgroundColor { get; set; }
    public int ChatBubbleSize { get; set; }

    public bool IsAdmin { get; set; }
    public bool IsMod { get; set; }
    public ChatCommandRole ChatCommandRole => ChatHelper.GetRoleFromFlags(IsAdmin, IsMod);
    public DateTimeOffset? MutedUntil { get; set; }

    public ClientPcProfile ActiveProfile =>
        Profiles.FirstOrDefault(x => x.Id == ActiveProfileId) ?? Profiles.First();

    public Mount? Mount { get; set; }

    public List<FriendData> Friends { get; set; } = [];
    public List<IgnoreData> Ignores { get; set; } = [];

    public ConcurrentSet<ulong> IncomingFriendRequests { get; } = [];
    public ConcurrentSet<ulong> IncomingGuildInvites { get; } = [];

    public ConcurrentDictionary<ChatChannel, bool> ChatChannelStatus { get; set; } = [];

    public int StationCash { get; set; }
    public List<CoinStoreTransactionRecord> CoinStoreTransactions { get; set; } = [];

    public GuildData? GuildData { get; set; }

    public int TimezoneOffset { get; set; }

    public Vector4 StartingZonePosition { get; set; }
    public Quaternion StartingZoneRotation { get; set; }

    #region Combat

    private const int OutOfCombatSeconds = 6;

    private const float BasicDamageDelay = 0.15f;
    private const float SpecialDamageDelay = 0.4f;

    private const int MultiHitSpacingMs = 300;
    private const int SummonTickMs = 300;
    private const float SummonLeashRange = 30f;

    private static readonly Vector3[] SummonOffsets =
    [
        new(-2f, 0f, -2f),
        new(2f, 0f, -2f),
        new(0f, 0f, -3f),
        new(-3f, 0f, 1f),
        new(3f, 0f, 1f)
    ];

    private static int _nextProjectileId;

    private long _lastWorldCombatTicks;
    private bool _worldCombatActive;

    private long _nextBasicSwingTicks;

    public Vector3 Forward
    {
        get
        {
            var len = MathF.Sqrt(Rotation.X * Rotation.X + Rotation.Z * Rotation.Z);
            return len < 0.0001f
                ? new Vector3(0f, 0f, 1f)
                : new Vector3(Rotation.X / len, 0f, Rotation.Z / len);
        }
    }

    private const int PrimaryWeaponSlot = 7;

    public int GetEquippedWeaponDefinitionId()
    {
        if (!ActiveProfile.Items.TryGetValue(PrimaryWeaponSlot, out var profileItem))
            return 0;

        var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

        return clientItem?.Definition ?? 0;
    }

    public int ResolveWieldType()
    {
        if (_resourceManager.ClientItemDefinitions.TryGetValue(GetEquippedWeaponDefinitionId(), out var weaponDefinition)
            && _resourceManager.ItemClasses.TryGetValue(weaponDefinition.Class, out var itemClass)
            && itemClass.WieldType != 0)
        {
            return itemClass.WieldType;
        }

        return _resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit) ? kit.WieldType : 0;
    }

    public int ResolveWieldType(int itemClassWieldType)
    {
        if (itemClassWieldType != 0)
            return itemClassWieldType;

        return _resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit) ? kit.WieldType : 0;
    }

    public void EnterWorldCombat()
    {
        _lastWorldCombatTicks = Environment.TickCount64;

        if (_worldCombatActive)
            return;

        _worldCombatActive = true;
        SendWorldCombatState(true);
    }

    private void WorldCombatStateTick()
    {
        if (!_worldCombatActive || Environment.TickCount64 - _lastWorldCombatTicks < OutOfCombatSeconds * 1000L)
            return;

        _worldCombatActive = false;
        SendWorldCombatState(false);
    }

    private void SendWorldCombatState(bool fighting)
    {
        SendTunneled(new EncounterOverworldCombatPacket { InWorldCombat = fighting });
        SendTunneled(new EncounterPacketIsFighting { InWorldCombat = fighting });
    }

    public bool SendToolbar()
    {
        if (!_resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit))
        {
            SendTunneled(new AbilityPacketSetDefinition { ProfileId = ActiveProfileId });
            return false;
        }

        var setDefinition = new AbilityPacketSetDefinition { ProfileId = kit.ProfileId };

        var weaponDefinitionId = GetEquippedWeaponDefinitionId();

        if (_resourceManager.ClientItemDefinitions.TryGetValue(weaponDefinitionId, out var weaponDefinition))
        {
            var (basic, special) = ResolveWeaponAbilities(kit, weaponDefinitionId);

            if (basic is not null)
            {
                setDefinition.AbilitySet.Abilities[0] = CreateToolbarSlot(kit.BasicSlotDefId, basic.IconId, weaponDefinition.NameId, manaCost: 0);
                SendAbilityDefinition(kit.BasicSlotDefId, basic);
            }

            if (special is not null)
            {
                setDefinition.AbilitySet.Abilities[1] = CreateToolbarSlot(kit.SpecialSlotDefId, special.IconId, weaponDefinition.NameId, special.EnergyCost);
                SendAbilityDefinition(kit.SpecialSlotDefId, special);
            }

            PreloadAbilityEffects(basic, special);
        }

        SendTunneled(setDefinition);

        MaxEnergy = kit.Energy.Max;
        // Resync energy against the new max.
        Energy = _energy;

        return true;
    }

    public bool TrySendAbilityDefinition(int abilityDefinitionId)
    {
        if (!_resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit))
            return false;

        var (basic, special) = ResolveWeaponAbilities(kit, GetEquippedWeaponDefinitionId());

        if (abilityDefinitionId == kit.BasicSlotDefId && basic is not null)
            SendAbilityDefinition(abilityDefinitionId, basic);
        else if (abilityDefinitionId == kit.SpecialSlotDefId && special is not null)
            SendAbilityDefinition(abilityDefinitionId, special);
        else
            return false;

        return true;
    }

    private void SendAbilityDefinition(int abilityDefinitionId, AbilityDefinition ability)
    {
        SendTunneled(new AbilityPacketAbilityDefinition
        {
            AbilityId = abilityDefinitionId,
            NameId = ability.NameId,
            DescriptionId = ability.DescriptionId,
            IconId = ability.IconId,
            ManaCost = ability.EnergyCost
        });
    }

    private static Ability CreateToolbarSlot(int abilityDefinitionId, int iconId, int nameId, int manaCost) => new()
    {
        Type = 3,
        InstanceId = abilityDefinitionId,
        ManaCost = manaCost,
        IconId = iconId,
        NameId = nameId,
        Unknown7 = 4,
        Unknown9 = 1,
        AbilityDefinitionId = abilityDefinitionId,
        ForceDismount = true
    };

    private void PreloadAbilityEffects(params AbilityDefinition?[] abilities)
    {
        var effectIds = new HashSet<int>();

        foreach (var ability in abilities)
        {
            if (ability is null)
                continue;

            effectIds.Add(ability.HitEffectId);
            effectIds.Add(ability.CastEffectId);
            effectIds.Add(ability.CasterEndEffectId);
            effectIds.Add(ability.EnemyExtraEffectId);

            if (ability.Summon is not null)
            {
                effectIds.Add(ability.Summon.SpawnEffectId);
                effectIds.Add(ability.Summon.HitEffectId);
            }
        }

        var warmPosition = new Vector4(Position.X, Position.Y - 400f, Position.Z, 1f);

        foreach (var effectId in effectIds)
        {
            if (effectId <= 0)
                continue;

            SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = 0,
                CompositeEffectId = effectId,
                LifetimeMs = 1500,
                Position = warmPosition
            });
        }
    }

    public bool TryExecuteAbility(AbilityPacketClientRequestStartAbility request)
    {
        if (request.Data.Id != 1)
            return false;

        if (!_resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit))
            return false;

        var weaponDefinitionId = GetEquippedWeaponDefinitionId();
        var (basic, special) = ResolveWeaponAbilities(kit, weaponDefinitionId);

        var ability = request.Data.Slot switch
        {
            <= 0 => basic,
            1 => special,
            _ => null
        };

        if (ability is null)
            return false;

        var now = Environment.TickCount64;
        if (now < _nextBasicSwingTicks)
            return true;
        _nextBasicSwingTicks = now + kit.BasicRecastMs;

        var recastMs = kit.BasicRecastMs;

        if (ability.EnergyCost > 0)
        {
            if (Energy < ability.EnergyCost)
                return false;

            Energy -= ability.EnergyCost;

            var energyShortfall = ability.EnergyCost - Energy;
            if (energyShortfall > 0)
                recastMs = energyShortfall * 1000 / kit.Energy.RegenPerSecond;
        }

        var reach = request.Data.Slot <= 0 && kit.BasicAutoTargetReach > 0f ? kit.BasicAutoTargetReach : kit.AutoTargetReach;

        var primaryTarget = CombatMath.ResolvePrimaryTarget(this, request.Guid, reach);
        var targetGuid = primaryTarget?.Guid ?? (request.Guid != 0 ? request.Guid : Guid);

        SendTunneledToVisible(new AbilityPacketStartCasting
        {
            CasterGuid = Guid,
            TargetGuid = targetGuid,
            AbilityId = request.Data.Slot <= 0 ? kit.BasicSlotDefId : kit.SpecialSlotDefId
        }, sendToSelf: true);

        var targets = CombatMath.ResolveEffectTargets(this, ability, primaryTarget, reach);
        var clientVictimBeat = ability.Projectile is null;

        SendTunneledToVisible(new AbilityPacketLaunchAndLand
        {
            Guid = Guid,
            Targets = [.. targets.Select(target => Target.CreateCharacterGuid((long)target.Guid))],
            CasterAnimationId = ability.AnimationId,
            CasterEffectId = ability.CastEffectId,
            RecastMs = recastMs,
            TargetAnimationId = clientVictimBeat ? ability.TargetAnimationId : 0,
            TargetEffectId = clientVictimBeat ? ability.HitEffectId : 0,
            TargetEffectDuration = clientVictimBeat ? ability.TargetEffectDurationMs / 1000f : 0f,
            ScheduleMeleeContact = clientVictimBeat ? ResolveContactEffectId(ability, weaponDefinitionId) : 0,
            ActionBarId = request.Data.Id,
            ActionBarSlot = Math.Max(request.Data.Slot, 0)
        }, sendToSelf: true);

        if (ability.WeaponEffectId > 0)
            ApplyWeaponEffect(ability.WeaponEffectId, ability.WeaponEffectDurationMs);

        if (ability.Summon is not null)
            SummonAllies(ability.Summon);

        if (ability.Heal is { Amount: > 0 } partyHeal && !string.Equals(partyHeal.Scope, "Self", StringComparison.OrdinalIgnoreCase))
            HealParty(partyHeal);

        if (targets.Count == 0)
        {
            if (ability.Projectile is not null)
                FireProjectileForward(ability.Projectile);

            return true;
        }

        EnterWorldCombat();

        var damage = CombatMath.DamageFor(this, ability);
        var hitCount = Math.Max(1, ability.HitCount);
        var casterEndEffectId = ability.CasterEndEffectId;
        foreach (var target in targets)
        {
            var damageDelay = request.Data.Slot <= 0 ? BasicDamageDelay : SpecialDamageDelay;

            if (ability.Projectile is not null)
            {
                FireProjectileAt(target, ability.Projectile);

                damageDelay = MathF.Max(damageDelay, CombatMath.FlightTimeTo(this, target, ability.Projectile));
            }

            for (var hit = 0; hit < hitCount; hit++)
            {
                ResolveDamageAfterDelay(target, kit, ability, damage, damageDelay + hit * MultiHitSpacingMs / 1000f, casterEndEffectId, !clientVictimBeat, hit == hitCount - 1);
                casterEndEffectId = 0;
            }
        }

        return true;
    }

    private void HealParty(AbilityHealDefinition heal)
    {
        var radiusSquared = heal.Radius * heal.Radius;

        foreach (var other in Zone.Players)
        {
            var dx = other.Position.X - Position.X;
            var dz = other.Position.Z - Position.Z;

            if (dx * dx + dz * dz <= radiusSquared)
                other.Heal(heal.Amount, Guid);
        }
    }

    private void ApplyWeaponEffect(int compositeEffectId, int durationMs)
    {
        const int PrimaryWeaponSlot = 7;

        SendTunneledToVisible(new PlayerUpdatePacketSlotCompositeEffectOverride
        {
            Guid = Guid,
            Slot = PrimaryWeaponSlot,
            CompositeEffect = compositeEffectId
        }, sendToSelf: true);

        if (durationMs <= 0)
            return;

        var zone = Zone;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs);

                if (Zone != zone)
                    return;

                SendTunneledToVisible(new PlayerUpdatePacketSlotCompositeEffectOverride
                {
                    Guid = Guid,
                    Slot = PrimaryWeaponSlot,
                    CompositeEffect = 0
                }, sendToSelf: true);
            }
            catch (Exception ex)
            {
                Zone.Logger.LogError(ex, "Weapon effect expiry failed.");
            }
        });
    }

    private int ResolveContactEffectId(AbilityDefinition ability, int weaponDefinitionId)
    {
        if (ability.ContactEffectId > 0)
            return ability.ContactEffectId;

        return _resourceManager.ClientItemDefinitions.TryGetValue(weaponDefinitionId, out var weapon)
            ? weapon.WeaponTrailEffectId
            : 0;
    }

    private (AbilityDefinition? Basic, AbilityDefinition? Special) ResolveWeaponAbilities(JobKitDefinition kit, int weaponDefinitionId)
    {
        var mapping = kit.Weapons.FirstOrDefault(w => w.WeaponDefIds.Contains(weaponDefinitionId));

        var basicId = mapping?.BasicAbilityId ?? kit.FallbackBasicAbilityId;
        var specialId = mapping?.SpecialAbilityId ?? 0;

        return (
            _resourceManager.CombatAbilities.TryGetValue(basicId, out var basic) ? basic : null,
            _resourceManager.CombatAbilities.TryGetValue(specialId, out var special) ? special : null);
    }

    private void ResolveDamageAfterDelay(Npc target, JobKitDefinition kit, AbilityDefinition ability,
        int baseDamage, float delaySeconds, int casterEndEffectId, bool sendHitEffect, bool lastHit)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)(delaySeconds * 1000));

                EnterWorldCombat();

                if (casterEndEffectId > 0)
                {
                    SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = Guid,
                        CompositeEffectId = casterEndEffectId,
                        LifetimeMs = 2000,
                        Position = Vector4.Zero
                    }, sendToSelf: true);
                }

                if (!target.IsAlive)
                    return;

                if (baseDamage <= 0)
                    return;

                var damage = CombatMath.RollDamage(this, kit.Traits, baseDamage, out var isCriticalHit);

                if (sendHitEffect && ability.HitEffectId > 0)
                {
                    SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = target.Guid,
                        CompositeEffectId = ability.HitEffectId,
                        Position = Vector4.Zero
                    }, sendToSelf: true);
                }

                if (ability.EnemyExtraEffectId > 0)
                {
                    SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = target.Guid,
                        CompositeEffectId = ability.EnemyExtraEffectId,
                        Position = Vector4.Zero
                    }, sendToSelf: true);
                }

                ApplyHit(Guid, target, damage, isCriticalHit, ability.Id);

                if (ability.Heal is { PercentOfDamage: > 0 } steal && string.Equals(steal.Scope, "Self", StringComparison.OrdinalIgnoreCase))
                    Heal(damage * steal.PercentOfDamage / 100, Guid);

                if (ability.EnergySteal is { Amount: > 0 } energySteal)
                    Energy += energySteal.Amount;

                if (lastHit && ability.Dot is { TickDamage: > 0, TickMs: > 0 } dot)
                    StartDot(target, ability, dot);
            }
            catch (Exception ex)
            {
                Zone.Logger.LogError(ex, "Ability damage resolution failed.");
            }
        });
    }

    private void StartDot(Npc target, AbilityDefinition ability, AbilityDotDefinition dot)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var elapsed = dot.TickMs; elapsed <= dot.DurationMs; elapsed += dot.TickMs)
                {
                    await Task.Delay(dot.TickMs);

                    if (!target.IsAlive || Zone != target.Zone)
                        return;

                    if (ability.HitEffectId > 0)
                    {
                        SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                        {
                            Guid = target.Guid,
                            CompositeEffectId = ability.HitEffectId,
                            Position = Vector4.Zero
                        }, sendToSelf: true);
                    }

                    ApplyHit(Guid, target, dot.TickDamage, false, ability.Id);
                }
            }
            catch (Exception ex)
            {
                Zone.Logger.LogError(ex, "Damage-over-time resolution failed.");
            }
        });
    }

    private void ApplyHit(ulong sourceGuid, Npc target, int damage, bool isCriticalHit, int abilityId)
    {
        var killed = target.ApplyDamage(damage);

        SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
        {
            Guid = sourceGuid,
            Guid2 = target.Guid,
            ShowFloatingText = true,
            Unknown2 = target.MaxHealth,
            Unknown3 = target.Health,
            Unknown4 = -damage,
            IsCriticalHit = isCriticalHit
        }, sendToSelf: true);

        Zone.Logger.LogInformation("Ability {ability} hit {name} ({guid}) for {damage} -> {health}/{maxHealth} (crit={crit}, killed={killed})",
            abilityId, target.Name, target.Guid, damage, target.Health, target.MaxHealth, isCriticalHit, killed);

        if (killed && target.RestoreOnDeath)
        {
            target.Health = target.MaxHealth;

            foreach (var other in Zone.Players)
                Zone.SendNpcHealth(other, target);
        }
    }

    private void SummonAllies(AbilitySummonDefinition summon)
    {
        for (var i = 0; i < Math.Max(1, summon.Count); i++)
        {
            if (!Zone.TryCreateNpc(null, out var npc))
                return;

            var offset = SummonOffsets[i % SummonOffsets.Length];
            var position = new Vector4(Position.X + offset.X, Position.Y, Position.Z + offset.Z, 1f);

            npc.ModelId = summon.ModelId;
            npc.Name = summon.Name;
            npc.WieldType = summon.WieldType;
            npc.Scale = 1f;
            npc.IsInteractable = false;
            npc.CursorId = 0;
            npc.Speed = summon.MoveSpeed;
            npc.Visible = true;
            npc.UpdatePosition(position, Rotation);

            PlaySummonEffect(npc, summon.SpawnEffectId, position);

            _ = RunSummonAsync(npc, summon);
        }
    }

    private async Task RunSummonAsync(Npc npc, AbilitySummonDefinition summon)
    {
        var zone = npc.Zone;
        var expires = Environment.TickCount64 + summon.LifetimeMs;
        var nextAttack = 0L;
        var moving = false;

        try
        {
            while (Environment.TickCount64 < expires && Zone == zone)
            {
                await Task.Delay(SummonTickMs);

                var target = CombatMath.NearestHostile(zone, npc.Position, SummonLeashRange);

                if (target is null)
                {
                    if (moving)
                    {
                        npc.MoveTo(new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z), true);
                        moving = false;
                    }

                    continue;
                }

                var dx = target.Position.X - npc.Position.X;
                var dz = target.Position.Z - npc.Position.Z;

                if (dx * dx + dz * dz > summon.AttackRange * summon.AttackRange)
                {
                    npc.MoveTo(new Vector3(target.Position.X, target.Position.Y, target.Position.Z), true);

                    if (!moving)
                    {
                        SendAnimation(npc.Guid, summon.RunAnimationId);
                        moving = true;
                    }

                    continue;
                }

                if (moving)
                {
                    npc.MoveTo(new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z), true);
                    moving = false;
                }

                var now = Environment.TickCount64;
                if (now < nextAttack)
                    continue;

                nextAttack = now + summon.AttackCooldownMs;

                SendAnimation(npc.Guid, summon.AttackAnimationId);

                if (summon.HitEffectId > 0)
                {
                    SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = target.Guid,
                        CompositeEffectId = summon.HitEffectId,
                        Position = Vector4.Zero
                    }, sendToSelf: true);
                }

                if (summon.AttackDamage > 0)
                {
                    EnterWorldCombat();
                    ApplyHit(Guid, target, summon.AttackDamage, false, 0);
                }
            }
        }
        catch (Exception ex)
        {
            Zone.Logger.LogError(ex, "Summon behaviour failed.");
        }
        finally
        {
            PlaySummonEffect(npc, summon.SpawnEffectId, npc.Position);
            npc.Dispose();
        }
    }

    private void SendAnimation(ulong guid, int animationId)
    {
        if (animationId <= 0)
            return;

        SendTunneledToVisible(new PlayerUpdatePacketSetAnimation
        {
            Guid = guid,
            AnimationId = animationId,
            PlayType = 0
        }, sendToSelf: true);
    }

    private void PlaySummonEffect(Npc npc, int effectId, Vector4 position)
    {
        if (effectId <= 0)
            return;

        SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = npc.Guid,
            CompositeEffectId = effectId,
            LifetimeMs = 2000,
            Position = position
        }, sendToSelf: true);
    }

    private void FireProjectileAt(Npc target, AbilityProjectileDefinition projectile)
    {
        var start = new Vector4(
            Position.X,
            Position.Y + projectile.MuzzleHeight,
            Position.Z,
            1f);

        SendTunneledToVisible(new PlayerUpdatePacketLaunchProjectile
        {
            ProjectileId = Interlocked.Increment(ref _nextProjectileId),
            Speed = projectile.Speed,
            FlightType = PlayerUpdatePacketLaunchProjectile.FlightTypeBeam,
            Direction = new Vector4(
                target.Position.X - start.X,
                target.Position.Y + 1f - start.Y,
                target.Position.Z - start.Z,
                0f),
            StartPosition = start,
            ModelFileName = projectile.ModelFileName,
            Source = Target.CreateCharacterGuid((long)Guid),
            Destination = Target.CreateCharacterGuid((long)target.Guid),
            SpinAxis = new Vector4(0f, 0f, 1f, 0f),
            TrailCompositeEffectId = projectile.TrailEffectId
        }, sendToSelf: true);
    }

    private void FireProjectileForward(AbilityProjectileDefinition projectile)
    {
        var forward = Forward;

        SendTunneledToVisible(new PlayerUpdatePacketLaunchProjectile
        {
            ProjectileId = Interlocked.Increment(ref _nextProjectileId),
            Speed = projectile.Speed,
            FlightType = PlayerUpdatePacketLaunchProjectile.FlightTypeBeam,
            FireForward = 1,
            Direction = new Vector4(forward.X, 0f, forward.Z, 0f),
            StartPosition = new Vector4(
                Position.X,
                Position.Y + projectile.MuzzleHeight,
                Position.Z,
                1f),
            ModelFileName = projectile.ModelFileName,
            Source = Target.CreateCharacterGuid((long)Guid),
            SpinAxis = new Vector4(0f, 0f, 1f, 0f),
            TrailCompositeEffectId = projectile.TrailEffectId
        }, sendToSelf: true);
    }

    #endregion

    public Player(BaseZone zone, UdpConnection connection, IResourceManager resourceManager)
    {
        Zone = zone;

        _connection = connection;
        _resourceManager = resourceManager;
    }

    #region Connection

    public void Send(ISerializablePacket packet)
    {
        var data = packet.Serialize();

        _connection.Send(UdpChannel.Reliable1, data);
    }

    public void SendToVisible(ISerializablePacket packet, bool sendToSelf = false)
    {
        var visiblePlayers = VisiblePlayers.ToFrozenDictionary();

        foreach (var visiblePlayer in visiblePlayers)
            visiblePlayer.Value.Send(packet);

        if (sendToSelf)
            Send(packet);
    }

    public void SendTunneled(ISerializablePacket packet)
    {
        var packetTunneled = new PacketTunneledClientPacket
        {
            Payload = packet.Serialize()
        };

        Send(packetTunneled);
    }

    [Obsolete]
    public void SendTunneled(byte[] buffer)
    {
        var packetTunneled = new PacketTunneledClientPacket
        {
            Payload = buffer
        };

        Send(packetTunneled);
    }

    public void SendTunneledToVisible(ISerializablePacket packet, bool sendToSelf = false)
    {
        var visiblePlayers = VisiblePlayers.ToFrozenDictionary();

        foreach (var visiblePlayer in visiblePlayers)
            visiblePlayer.Value.SendTunneled(packet);

        if (sendToSelf)
            SendTunneled(packet);
    }

    public bool IsMuted()
    {
        DateTimeOffset currentTime = DateTimeOffset.UtcNow;
        DateTimeOffset? mutedUntil = MutedUntil;
        return mutedUntil.HasValue && mutedUntil > currentTime;
    }
    
    public void Disconnect()
    {
        _connection.Disconnect();
    }

    public void Dismount()
    {
        if (Mount is null)
            return;

        SendTunneledToVisible(new PacketDismountResponse
        {
            RiderGuid = Guid,
            CompositeEffectId = 46
        }, sendToSelf: true);

        UpdateCharacterStats(
            CharacterStats.MaxMovementSpeed.Set(8f),
            CharacterStats.GlideEnabled.Set(0),
            CharacterStats.JumpHeight.Set(0f));

        SendTunneledToVisible(new PlayerUpdatePacketRemovePlayerGracefully
        {
            Guid = Mount.Guid,
            Animate = false,
            Delay = 0,
            EffectDelay = 0,
            CompositeEffectId = 0,
            Duration = 1000
        }, sendToSelf: true);

        Mount.Dispose();
        Mount = null;
    }

    #endregion

    #region Update

    public void UpdateEveryTick()
    {
    }

    public void UpdateEverySecond()
    {
        WorldCombatStateTick();
        EnergyRegenTick();
    }

    public int MaxHealth { get; private set; } = 2500;
    public int Health { get; private set; } = 2500;

    public void SetHealth(int health)
    {
        Health = Math.Clamp(health, 0, MaxHealth);

        SendTunneled(new ClientUpdatePacketHitpoints
        {
            CurrentHitpoints = Health,
            MaxHitpoints = MaxHealth
        });

        SendTunneledToVisible(new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = Guid,
            Hitpoints = Health,
            MaxHitpoints = MaxHealth
        }, sendToSelf: false);
    }

    public void Heal(int amount, ulong sourceGuid)
    {
        if (amount <= 0 || Health >= MaxHealth)
            return;

        SetHealth(Health + amount);

        SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
        {
            Guid = sourceGuid,
            Guid2 = Guid,
            ShowFloatingText = true,
            Unknown2 = MaxHealth,
            Unknown3 = Health,
            Unknown4 = amount
        }, sendToSelf: true);
    }

    private int _energy = 100;

    public int Energy
    {
        get => _energy;
        private set
        {
            _energy = Math.Min(value, MaxEnergy);

            SendTunneled(new ClientUpdatePacketMana
            {
                CurrentMana = _energy,
                MaxMana = MaxEnergy
            });
        }
    }

    private int MaxEnergy { get; set; } = 100;

    private void EnergyRegenTick()
    {
        if (!_resourceManager.CombatJobs.TryGetValue(ActiveProfileId, out var kit))
            return;

        if (Energy >= MaxEnergy)
            return;

        Energy += kit.Energy.RegenPerSecond;
    }

    public void UpdatePosition(Vector4 position, Quaternion rotation, bool updateZoneArea = true)
    {
        Position = position;
        Rotation = rotation;

        Mount?.UpdatePosition(position, rotation, updateZoneArea);

        if (Visible)
        {
            UpdateZoneTile();

            if (updateZoneArea)
                UpdateZoneArea();
        }
    }

    private void UpdateZoneTile()
    {
        var newZoneTile = Zone.GetTileFromPosition(Position);

        if (newZoneTile == ZoneTile)
            return;

        Zone.UpdateEntityZoneTile(this, ZoneTile, newZoneTile);

        ZoneTile = newZoneTile;
    }

    public void TeleportToZone(IZone zone, Vector4 position, Quaternion rotation)
    {
        if (Zone == zone)
            return;

        if (Zone is StartingZone)
        {
            StartingZonePosition = Position;
            StartingZoneRotation = Rotation;
        }

        if (Mount is not null)
            Mount.TeleportToZone(zone, position, rotation);

        // Alert/Remove visible entities
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisiblePlayers([this]);

        OnRemoveVisibleNpcs(VisibleNpcs.Values);
        OnRemoveVisiblePlayers(VisiblePlayers.Values);

        ZoneTile.Entities.Remove(Guid, out _);

        Zone.TryRemovePlayer(Guid);

        // Add to new zone/zonetile

        zone.TryAddPlayer(this);

        // Teleport to new zone

        Visible = false;

        Zone = zone;

        ZoneTile = ZoneTile.Empty;

        UpdatePosition(position, rotation);

        var packetClientBeginZoning = new PacketClientBeginZoning
        {
            Name = Zone.Name,
            Position = position,
            Rotation = rotation,
            Sky = "sky_deep_mines.xml",
            Id = Zone.Id,
            GeometryId = 214,
            OverrideUpdateRadius = true
        };

        SendTunneled(packetClientBeginZoning);
    }

    private void UpdateZoneArea()
    {
        if (Zone is not StartingZone startingZone)
            return;

        var zoneAreaId = startingZone.GetZoneAreaId(Position);

        if (ZoneAreaId == zoneAreaId)
            return;

        ZoneAreaId = zoneAreaId;

        var packetPOIChangeMessage = new PacketPOIChangeMessage
        {
            ZoneId = zoneAreaId
        };

        SendTunneled(packetPOIChangeMessage);
    }

    public void UpdateCharacterStats(params CharacterStat[] characterStats)
    {
        var clientUpdatePacketUpdateStat = new ClientUpdatePacketUpdateStat
        {
            Guid = Guid
        };

        clientUpdatePacketUpdateStat.Stats.AddRange(characterStats);

        SendTunneled(clientUpdatePacketUpdateStat);

        foreach (var characterStat in characterStats)
        {
            Stats[characterStat.Id] = characterStat;

            if (characterStat.Id == CharacterStatId.MaxMovementSpeed)
            {
                var playerUpdatePacketExpectedSpeed = new PlayerUpdatePacketExpectedSpeed
                {
                    Guid = Guid,
                    ExpectedSpeed = characterStat.Float
                };

                SendTunneledToVisible(playerUpdatePacketExpectedSpeed);
            }
        }
    }

    #endregion

    #region Events

    public void OnAddVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        foreach (var npc in npcs)
        {
            if (npc is Mount)
                continue;

            SendTunneled(npc.GetAddNpcPacket());

            if (npc.IsHostile)
                SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = npc.Disposition });
        }

        var playerUpdatePacketNpcRelevance = new PlayerUpdatePacketNpcRelevance();

        foreach (var npc in npcs)
        {
            if (npc.CursorId == 0)
                continue;

            playerUpdatePacketNpcRelevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
            {
                Guid = npc.Guid,
                HasCursor = true,
                CursorId = npc.CursorId
            });
        }

        if (playerUpdatePacketNpcRelevance.Entries.Count > 0)
            SendTunneled(playerUpdatePacketNpcRelevance);

        var playerUpdatePacketAddNotifications = new PlayerUpdatePacketAddNotifications();

        foreach (var npc in npcs)
        {
            if (npc.Notification is null)
                continue;

            playerUpdatePacketAddNotifications.Notifications.Add(npc.Notification);
        }

        if (playerUpdatePacketAddNotifications.Notifications.Count > 0)
            SendTunneled(playerUpdatePacketAddNotifications);

        foreach (var npc in npcs)
            VisibleNpcs.TryAdd(npc.Guid, npc);
    }

    public void OnAddVisiblePlayers(params IEnumerable<Player> players)
    {
        foreach (var player in players)
        {
            if (player.Mount is not null)
            {
                var addPc = player.GetAddPcPacket();
                addPc.MountGuid = 0;
                addPc.MountSeat = -1;
                addPc.MountQueuePosition = -1;
                addPc.NameVerticalOffset = 0;

                SendTunneled(addPc);
                SendTunneled(player.Mount.GetAddNpcPacket());
                SendTunneled(player.Mount.GetMountResponsePacket());
            }
            else
                SendTunneled(player.GetAddPcPacket());
        }

        foreach (var player in players)
            VisiblePlayers.TryAdd(player.Guid, player);
    }

    public void OnRemoveVisibleNpcs(params IEnumerable<Npc> npcs)
    {
        foreach (var npc in npcs)
        {
            if (npc is Mount)
                continue;

            SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = npc.Guid });
        }

        foreach (var npc in npcs)
            VisibleNpcs.TryRemove(npc.Guid, out _);
    }

    public void OnRemoveVisibleNpcGracefully(Npc npc, bool animate, int delay, int effectDelay,
        int compositeEffectId, int duration)
    {
        if (npc is Mount)
            return;

        SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
        {
            Guid = npc.Guid,
            Animate = animate,
            Delay = delay,
            EffectDelay = effectDelay,
            CompositeEffectId = compositeEffectId,
            Duration = duration
        });

        VisibleNpcs.TryRemove(npc.Guid, out _);
    }

    public void OnRemoveVisiblePlayers(params IEnumerable<Player> players)
    {
        foreach (var player in players)
        {
            SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = player.Guid });

            if (player.Mount is not null)
                SendTunneled(new PlayerUpdatePacketRemovePlayer { Guid = player.Mount.Guid });
        }

        foreach (var player in players)
            VisiblePlayers.TryRemove(player.Guid, out _);
    }

    public void OnInteract(Player player)
    {
        var commandPacketInteractionList = new CommandPacketInteractionList();

        commandPacketInteractionList.List.Guid = Guid;

        commandPacketInteractionList.List.Interactions.Add(InspectInteraction.Data);

        if (Friends.Any(x => x.Guid == player.Guid))
        {
            commandPacketInteractionList.List.Interactions.Add(RemoveFriendInteraction.Data);
        }
        else
        {
            commandPacketInteractionList.List.Interactions.Add(AddFriendInteraction.Data);
        }

        if (player.Ignores.Any(x => x.Guid == Guid))
        {
            commandPacketInteractionList.List.Interactions.Add(StopIgnoringInteraction.Data);
        }
        else
        {
            commandPacketInteractionList.List.Interactions.Add(IgnoreInteraction.Data);
        }

        if (GuildData is null && GuildInviteInteraction.CanInvite(player))
            commandPacketInteractionList.List.Interactions.Add(GuildInviteInteraction.Data);

        player.SendTunneled(commandPacketInteractionList);
    }

    #endregion

    public int GetFlairShardCompositeEffect()
    {
        const int FlairShardSlot = 13;

        if (ActiveProfile.Items.TryGetValue(FlairShardSlot, out var profileItem))
        {
            var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

            if (clientItem is not null)
            {
                if (_resourceManager.ClientItemDefinitions.TryGetValue(clientItem.Definition, out var clientItemDefinition))
                    return clientItemDefinition.CompositeEffectId;
            }
        }

        return 0;
    }

    public List<CharacterAttachmentData> GetAttachments()
    {
        var list = new List<CharacterAttachmentData>();

        foreach (var profileItem in ActiveProfile.Items)
        {
            var attachment = GetAttachment(profileItem.Key);

            if (attachment is null)
                continue;

            list.Add(attachment);
        }

        return list;
    }

    public CharacterAttachmentData? GetAttachment(int slot)
    {
        if (!ActiveProfile.Items.TryGetValue(slot, out var profileItem))
            return null;

        var clientItem = Items.FirstOrDefault(x => x.Id == profileItem.Id);

        if (clientItem is null)
            return null;

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(clientItem.Definition, out var clientItemDefinition))
            return null;

        var compositeEffectId = clientItemDefinition.CompositeEffectId;

        // Update the Weapon composite effect if we have a Flair Shard equipped.
        if (slot == 7)
        {
            var flairShardcompositeEffectId = GetFlairShardCompositeEffect();

            if (flairShardcompositeEffectId > 0)
                compositeEffectId = flairShardcompositeEffectId;
        }

        return new CharacterAttachmentData
        {
            ModelName = clientItemDefinition.ModelName,
            TextureAlias = clientItemDefinition.TextureAlias,
            TintAlias = clientItemDefinition.TintAlias,
            TintId = clientItem.Tint,
            CompositeEffectId = compositeEffectId,
            Slot = clientItemDefinition.Slot
        };
    }

    public PlayerUpdatePacketAddPc GetAddPcPacket()
    {
        var packet = new PlayerUpdatePacketAddPc
        {
            Guid = Guid,

            Name = Name,

            Model = Model,

            ChatBubbleForegroundColor = ChatBubbleForegroundColor,
            ChatBubbleBackgroundColor = ChatBubbleBackgroundColor,
            ChatBubbleSize = ChatBubbleSize,

            Position = Position,
            Rotation = Rotation,

            Attachments = GetAttachments(),

            WieldType = ResolveWieldType(),

            Head = Head,
            Hair = Hair,

            HairColor = HairColor,
            EyeColor = EyeColor,

            SkinTone = SkinTone,

            FacePaint = FacePaint,
            ModelCustomization = ModelCustomization,

            MaxMovementSpeed = Stats[CharacterStatId.MaxMovementSpeed],

            IsUnderage = Age < 18,
            IsMember = MembershipStatus != 0,

            // playerUpdatePacketAddPc.TemporaryAppearance = 277;

            ActiveProfileId = ActiveProfileId,

            MountQueuePosition = -1,
            MountSeat = -1,
        };

        var activeTitle = Titles.FirstOrDefault(x => x.Id == ActiveTitle);

        if (activeTitle is not null)
            packet.Title = activeTitle;

        if (Mount is not null)
        {
            packet.MountGuid = Mount.Guid;
            packet.MountSeat = Mount.Seat;
            packet.MountQueuePosition = Mount.QueuePosition;

            packet.NameVerticalOffset = Mount.Definition.NameVerticalOffset;
        }

        if (GuildData is not null)
            packet.Guilds.Add(0, GuildData.Guid);

        return packet;
    }

    #region Equatable

    public bool Equals(IEntity? other)
    {
        return Guid == other?.Guid;
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Player other)
            return Equals(other);

        return false;
    }

    public override int GetHashCode()
    {
        return Guid.GetHashCode();
    }

    public static bool operator ==(Player left, Player right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Player left, Player right)
    {
        return !(left == right);
    }

    #endregion

    public void Dispose()
    {
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisiblePlayers([this]);

        Mount?.Dispose();
        Mount = null;

        ZoneTile.Entities.Remove(Guid, out _);
        Zone.TryRemovePlayer(Guid);
    }
}
