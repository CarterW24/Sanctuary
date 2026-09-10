using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Combat;
using Sanctuary.Game.Zones;

namespace Sanctuary.Game;

public static class CombatMath
{
    private const float SweepArcCosine = 0.819f;
    private const float SweepMaxReach = 15f;
    private const int MaxSweepTargets = 3;

    private const float ImpactProximity = 2f;
    private const float MaxFlightSeconds = 3f;

    public static int DamageFor(Player player, AbilityDefinition ability)
    {
        if (ability.DamageByLevel is null || ability.DamageByLevel.Count == 0)
            return ability.Damage;

        var rank = player.ActiveProfile.Rank;
        var bestLevel = -1;
        var damage = ability.Damage;

        foreach (var (level, value) in ability.DamageByLevel)
        {
            if (level <= rank && level > bestLevel)
            {
                bestLevel = level;
                damage = value;
            }
        }

        return damage;
    }

    public static int RollDamage(Player player, JobKitTraitDefinition traits, int baseDamage, out bool isCriticalHit)
    {
        isCriticalHit = false;

        var rank = player.ActiveProfile.Rank;
        var damage = (float)baseDamage;

        if (rank >= traits.DamageBonusLevel)
            damage *= 1f + traits.DamageBonus;

        var critChance = traits.BaseCritChancePercent;

        if (rank >= traits.DamageBonusLevel)
            critChance += traits.CritChanceBonus;

        if (critChance > 0 && Random.Shared.Next(100) < critChance)
        {
            var critMultiplier = traits.BaseCritMultiplier;

            if (rank >= traits.CritMultiplierLevel)
                critMultiplier += traits.CritMultiplierBonus;

            damage *= critMultiplier;
            isCriticalHit = true;
        }

        return Math.Max(1, (int)damage);
    }

    public static Npc? ResolvePrimaryTarget(Player player, ulong requestedGuid, float reach)
    {
        var forward = player.Forward;

        if (requestedGuid != 0 && player.Zone.TryGetNpc(requestedGuid, out var selected)
            && selected.IsDamageable && selected.IsAlive)
        {
            var selectedDx = selected.Position.X - player.Position.X;
            var selectedDz = selected.Position.Z - player.Position.Z;

            if (forward.X * selectedDx + forward.Z * selectedDz > 0f
                && selectedDx * selectedDx + selectedDz * selectedDz <= reach * reach)
            {
                return selected;
            }
        }

        Npc? nearest = null;
        var best = reach * reach;

        foreach (var npc in player.Zone.Npcs)
        {
            if (!npc.IsHostile || !npc.IsDamageable || !npc.IsAlive)
                continue;

            var dx = npc.Position.X - player.Position.X;
            var dz = npc.Position.Z - player.Position.Z;
            var distanceSquared = dx * dx + dz * dz;

            if (distanceSquared >= best || forward.X * dx + forward.Z * dz <= 0f)
                continue;

            best = distanceSquared;
            nearest = npc;
        }

        return nearest;
    }

    public static List<Npc> ResolveEffectTargets(Player player, AbilityDefinition ability, Npc? primaryTarget, float reach)
    {
        if (string.Equals(ability.EffectType, "AoeDamage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ability.EffectType, "AoeDamageHeal", StringComparison.OrdinalIgnoreCase))
        {
            return ability.Damage > 0 || ability.DamageByLevel is not null ? ResolveAoeTargets(player, ability.AoeRadius) : [];
        }

        if (string.Equals(ability.EffectType, "SingleTargetDamage", StringComparison.OrdinalIgnoreCase))
            return primaryTarget is null ? [] : [primaryTarget];

        if (string.Equals(ability.EffectType, "Summon", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ability.EffectType, "Buff", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return primaryTarget is null ? [] : ResolveSweepTargets(player, primaryTarget, reach);
    }

    private static List<Npc> ResolveAoeTargets(Player player, float radius)
    {
        var radiusSquared = radius * radius;
        var center = player.Position;

        return player.Zone.Npcs
            .Where(npc => npc.IsHostile && npc.IsDamageable && npc.IsAlive)
            .Where(npc =>
            {
                var dx = npc.Position.X - center.X;
                var dz = npc.Position.Z - center.Z;
                return dx * dx + dz * dz <= radiusSquared;
            })
            .ToList();
    }

    private static List<Npc> ResolveSweepTargets(Player player, Npc primaryTarget, float reach)
    {
        var targets = new List<Npc>();

        var px = player.Position.X;
        var pz = player.Position.Z;
        var dirX = primaryTarget.Position.X - px;
        var dirZ = primaryTarget.Position.Z - pz;
        var dirLength = MathF.Sqrt(dirX * dirX + dirZ * dirZ);

        if (dirLength < 0.01f)
        {
            targets.Add(primaryTarget);
            return targets;
        }

        dirX /= dirLength;
        dirZ /= dirLength;

        var arcReach = MathF.Min(reach, SweepMaxReach);
        var arcReachSquared = arcReach * arcReach;

        foreach (var npc in player.Zone.Npcs)
        {
            if (!npc.IsHostile || !npc.IsDamageable || !npc.IsAlive)
                continue;

            var dx = npc.Position.X - px;
            var dz = npc.Position.Z - pz;
            var distanceSquared = dx * dx + dz * dz;

            if (distanceSquared > arcReachSquared)
                continue;

            if (distanceSquared < 0.0001f)
            {
                targets.Add(npc);
                continue;
            }

            var inverseDistance = 1f / MathF.Sqrt(distanceSquared);
            var dot = dirX * dx * inverseDistance + dirZ * dz * inverseDistance;

            if (dot >= SweepArcCosine)
                targets.Add(npc);
        }

        if (targets.Count > MaxSweepTargets)
        {
            targets = [.. targets
                .OrderByDescending(npc =>
                {
                    var tx = npc.Position.X - px;
                    var tz = npc.Position.Z - pz;
                    var length = MathF.Sqrt(tx * tx + tz * tz);
                    return length < 0.0001f ? 1f : (dirX * tx + dirZ * tz) / length;
                })
                .Take(MaxSweepTargets)];
        }

        return targets;
    }

    public static Npc? NearestHostile(IZone zone, Vector4 origin, float range)
    {
        Npc? nearest = null;
        var best = range * range;

        foreach (var npc in zone.Npcs)
        {
            if (!npc.IsHostile || !npc.IsDamageable || !npc.IsAlive)
                continue;

            var dx = npc.Position.X - origin.X;
            var dz = npc.Position.Z - origin.Z;
            var distanceSquared = dx * dx + dz * dz;

            if (distanceSquared >= best)
                continue;

            best = distanceSquared;
            nearest = npc;
        }

        return nearest;
    }

    public static float FlightTimeTo(Player player, Npc target, AbilityProjectileDefinition projectile)
    {
        var dx = target.Position.X - player.Position.X;
        var dy = target.Position.Y + 1f - (player.Position.Y + projectile.MuzzleHeight);
        var dz = target.Position.Z - player.Position.Z;
        var distance = MathF.Max(0f, MathF.Sqrt(dx * dx + dy * dy + dz * dz) - ImpactProximity);

        return MathF.Min(distance / projectile.Speed, MaxFlightSeconds);
    }
}
