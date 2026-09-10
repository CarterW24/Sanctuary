using System.Collections.Generic;

namespace Sanctuary.Game.Resources.Definitions.Combat;

public sealed class JobKitWeaponMapping
{
    public string Comment { get; set; } = string.Empty;

    public List<int> WeaponDefIds { get; set; } = [];

    public int BasicAbilityId { get; set; }
    public int SpecialAbilityId { get; set; }
}

public sealed class JobKitEnergyDefinition
{
    public int Max { get; set; } = 100;
    public int RegenPerSecond { get; set; } = 4;
}

public sealed class JobKitTraitDefinition
{
    public int DamageBonusLevel { get; set; } = 5;
    public float DamageBonus { get; set; }
    public int BaseCritChancePercent { get; set; } = 10;
    public int CritChanceBonus { get; set; }
    public int CritMultiplierLevel { get; set; } = 10;
    public float CritMultiplierBonus { get; set; }
    public float BaseCritMultiplier { get; set; } = 1.5f;
}

public sealed class JobKitDefinition
{
    public int ProfileId { get; set; }

    public string Comment { get; set; } = string.Empty;

    public float AutoTargetReach { get; set; } = 7f;
    public float BasicAutoTargetReach { get; set; }

    public int WieldType { get; set; }

    public int BasicSlotDefId { get; set; }
    public int SpecialSlotDefId { get; set; }

    public int BasicRecastMs { get; set; } = 600;

    public int FallbackBasicAbilityId { get; set; }

    public JobKitTraitDefinition Traits { get; set; } = new();

    public JobKitEnergyDefinition Energy { get; set; } = new();

    public List<JobKitWeaponMapping> Weapons { get; set; } = [];
}
