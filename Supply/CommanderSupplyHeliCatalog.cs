using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed partial class CommanderSupplyHeliService
{
    private static CargoAircraftOption? CreateAircraftOption(AircraftDefinition? definition)
    {
        if (definition?.unitPrefab == null || definition.aircraftParameters == null)
        {
            return null;
        }

        Aircraft? aircraft = definition.unitPrefab.GetComponent<Aircraft>();
        if (aircraft == null || aircraft.weaponManager == null || !IsEligibleSupplyAircraft(aircraft))
        {
            return null;
        }

        HardpointSet[] hardpointSets = aircraft.weaponManager.hardpointSets;
        if (hardpointSets == null || hardpointSets.Length == 0)
        {
            return null;
        }

        Loadout baseline = FindBaselineLoadout(definition, hardpointSets.Length);
        List<CargoSlotOption> cargoSlots = new();

        for (int setIndex = 0; setIndex < hardpointSets.Length; setIndex++)
        {
            HardpointSet? hardpointSet = hardpointSets[setIndex];
            List<WeaponMount> mounts = GetCargoMounts(hardpointSet);
            if (mounts.Count == 0)
            {
                continue;
            }

            string slotLabel = !string.IsNullOrWhiteSpace(hardpointSet!.name)
                ? hardpointSet.name
                : $"Cargo {setIndex + 1}";
            bool combinedBay = IsCombinedCargoBay(hardpointSets, setIndex, slotLabel);
            cargoSlots.Add(new CargoSlotOption(setIndex, slotLabel, mounts, combinedBay));
        }

        if (cargoSlots.Count == 0)
        {
            return null;
        }

        return new CargoAircraftOption(
            definition,
            GetAircraftLabel(definition),
            cargoSlots,
            baseline,
            hardpointSets);
    }

    private static bool IsCombinedCargoBay(HardpointSet[] sets, int setIndex, string label)
    {
        if (label.IndexOf("combined", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        int cargoConflicts = 0;
        for (int i = 0; i < sets.Length; i++)
        {
            if (i != setIndex && GetCargoMounts(sets[i]).Count > 0 && SetsConflict(sets, setIndex, i))
            {
                cargoConflicts++;
            }
        }

        return cargoConflicts >= 2;
    }

    private static Loadout FindBaselineLoadout(AircraftDefinition definition, int hardpointCount)
    {
        List<Loadout>? loadouts = definition.aircraftParameters.loadouts;
        if (loadouts != null)
        {
            for (int i = 0; i < loadouts.Count; i++)
            {
                if (loadouts[i]?.weapons?.Count == hardpointCount)
                {
                    return CloneLoadout(loadouts[i]);
                }
            }
        }

        StandardLoadout[]? standardLoadouts = definition.aircraftParameters.StandardLoadouts;
        if (standardLoadouts != null)
        {
            for (int i = 0; i < standardLoadouts.Length; i++)
            {
                Loadout? loadout = standardLoadouts[i]?.loadout;
                if (loadout?.weapons?.Count == hardpointCount)
                {
                    return CloneLoadout(loadout);
                }
            }
        }

        Loadout empty = new();
        for (int i = 0; i < hardpointCount; i++)
        {
            empty.weapons.Add(null!);
        }

        return empty;
    }

    private static Loadout BuildSelectedCargoLoadout(
        CargoAircraftOption aircraft,
        Airbase airbase,
        FactionHQ hq,
        bool includeEcm,
        bool includeCountermeasures,
        bool fillRemaining,
        out int cargoCount,
        out string cargoLabel,
        out string supportSummary)
    {
        Loadout loadout = CreateEmptyLoadout(aircraft.HardpointSets.Length);
        List<string> labels = new();
        cargoCount = 0;
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CargoSlotOption slot = aircraft.CargoSlots[i];
            WeaponMount? mount = slot.SelectedMount;
            if (mount == null)
            {
                continue;
            }

            PlaceCargoAndClearNonCargo(loadout, aircraft.HardpointSets, slot.HardpointIndex, mount);
            cargoCount += Mathf.Max(aircraft.HardpointSets[slot.HardpointIndex].hardpoints?.Count ?? 0, 1);
            labels.Add(GetCargoLabel(mount, string.Empty));
        }

        cargoLabel = cargoCount == 1 && labels.Count > 0
            ? labels[0]
            : $"{cargoCount} cargo loads";

        bool ecmAdded = !includeEcm || LoadoutContains(loadout, mount => mount.info?.jammer == true);
        bool countermeasureAdded = !includeCountermeasures || LoadoutContains(loadout, mount => mount.countermeasure);
        if (!ecmAdded)
        {
            ecmAdded = TryAddSupportMount(loadout, aircraft.HardpointSets, airbase, hq, mount => mount.info?.jammer == true);
        }
        if (!countermeasureAdded)
        {
            countermeasureAdded = TryAddSupportMount(loadout, aircraft.HardpointSets, airbase, hq, mount => mount.countermeasure);
        }
        if (fillRemaining)
        {
            FillRemainingMounts(loadout, aircraft.HardpointSets, airbase, hq);
        }

        supportSummary = $"ECM={(includeEcm ? (ecmAdded ? "added" : "unavailable") : "off")}, CM={(includeCountermeasures ? (countermeasureAdded ? "added" : "unavailable") : "off")}, fillRest={fillRemaining}";
        return loadout;
    }

    private static Loadout CreateEmptyLoadout(int hardpointCount)
    {
        Loadout loadout = new();
        for (int i = 0; i < hardpointCount; i++)
        {
            loadout.weapons.Add(null!);
        }
        return loadout;
    }

    private static bool LoadoutContains(Loadout loadout, Predicate<WeaponMount> predicate)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount != null && predicate(mount))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryAddSupportMount(
        Loadout loadout,
        HardpointSet[] sets,
        Airbase airbase,
        FactionHQ hq,
        Predicate<WeaponMount> predicate)
    {
        List<(int index, WeaponMount mount)> candidates = new();
        List<WeaponMount> available = new();
        for (int i = 0; i < sets.Length; i++)
        {
            if (loadout.weapons[i] != null || SlotBlocked(loadout, sets, i))
            {
                continue;
            }

            WeaponChecker.GetAvailableWeaponsNonAlloc(null, sets[i], airbase, hq, allowEmpty: false, available);
            for (int mountIndex = 0; mountIndex < available.Count; mountIndex++)
            {
                WeaponMount mount = available[mountIndex];
                if (!IsRuntimeCargoMount(mount) && predicate(mount))
                {
                    candidates.Add((i, mount));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        (int index, WeaponMount mount) selected = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        loadout.weapons[selected.index] = selected.mount;
        return true;
    }

    private static void FillRemainingMounts(Loadout loadout, HardpointSet[] sets, Airbase airbase, FactionHQ hq)
    {
        List<WeaponMount> available = new();
        for (int i = 0; i < sets.Length; i++)
        {
            if (loadout.weapons[i] != null || SlotBlocked(loadout, sets, i))
            {
                continue;
            }

            WeaponChecker.GetAvailableWeaponsNonAlloc(null, sets[i], airbase, hq, allowEmpty: false, available);
            for (int candidateIndex = available.Count - 1; candidateIndex >= 0; candidateIndex--)
            {
                WeaponMount candidate = available[candidateIndex];
                if (IsRuntimeCargoMount(candidate) || candidate.Troops || candidate.info?.troops == true)
                {
                    available.RemoveAt(candidateIndex);
                }
            }
            if (available.Count > 0)
            {
                loadout.weapons[i] = available[UnityEngine.Random.Range(0, available.Count)];
            }
        }
    }

    private static bool SlotBlocked(Loadout loadout, HardpointSet[] sets, int slotIndex)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            if (loadout.weapons[i] != null && i != slotIndex && SetsConflict(sets, slotIndex, i))
            {
                return true;
            }
        }
        return false;
    }

    private static void PlaceCargoAndClearNonCargo(
        Loadout loadout,
        HardpointSet[] sets,
        int selectedIndex,
        WeaponMount cargoMount)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            if (i != selectedIndex
                && loadout.weapons[i] != null
                && !IsRuntimeCargoMount(loadout.weapons[i])
                && SetsConflict(sets, selectedIndex, i))
            {
                loadout.weapons[i] = null!;
            }
        }

        loadout.weapons[selectedIndex] = cargoMount;
    }

    private static bool SetsConflict(HardpointSet[] sets, int first, int second)
    {
        return ContainsIndex(sets[first].precludingHardpointSets, second)
            || ContainsIndex(sets[second].precludingHardpointSets, first);
    }

    private static bool ContainsIndex(List<byte>? indices, int index)
    {
        return indices != null && index <= byte.MaxValue && indices.Contains((byte)index);
    }

    private static List<WeaponMount> GetCargoMounts(HardpointSet? set)
    {
        List<WeaponMount> result = new();
        if (set?.weaponOptions == null)
        {
            return result;
        }

        for (int i = 0; i < set.weaponOptions.Count; i++)
        {
            WeaponMount? mount = set.weaponOptions[i];
            if (IsRuntimeCargoMount(mount) && !result.Contains(mount!))
            {
                result.Add(mount!);
            }
        }

        return result;
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int swapIndex = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[swapIndex]) = (list[swapIndex], list[i]);
        }
    }

    private static bool IsRuntimeCargoMount(WeaponMount? mount)
    {
        return mount != null && mount.Cargo && mount.info != null && mount.info.cargo;
    }

    private static bool CargoMountSupportsAirdrop(WeaponMount mount)
    {
        if (mount.prefab == null)
        {
            return false;
        }

        MountedCargo[] cargoItems = mount.prefab.GetComponentsInChildren<MountedCargo>(true);
        if (cargoItems.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < cargoItems.Length; i++)
        {
            UnitDefinition? definition = cargoItems[i]?.cargo;
            Unit? unit = definition?.unitPrefab?.GetComponent<Unit>();
            if (unit is GroundVehicle groundVehicle)
            {
                if (GroundVehicleParachuteField?.GetValue(groundVehicle) == null)
                {
                    return false;
                }
            }
            else if (unit is Container container)
            {
                if (ContainerParachuteField?.GetValue(container) == null)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for aircraft NOCommander's Air Supply system knows how to task: either a vanilla
    /// helicopter/tiltwing (steered via the AIHeloTransportState hooks in
    /// CommanderSupplyHeliPatches) or an aircraft carrying a recognized self-flying transport
    /// AI controller such as the Chimera's, which manages its own delivery flight entirely and
    /// only needs to be spawned.
    /// </summary>
    private static bool IsEligibleSupplyAircraft(Aircraft aircraft)
    {
        return HasHeloPilot(aircraft) || CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(aircraft.gameObject);
    }

    private static bool HasHeloPilot(Aircraft aircraft)
    {
        if (aircraft.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot? pilot = aircraft.pilots[i];
            if (pilot != null && (pilot.pilotType == Pilot.PilotType.Helo || pilot.pilotType == Pilot.PilotType.Tiltwing))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetAircraftLabel(AircraftDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.unitName))
        {
            return definition.unitName;
        }

        return !string.IsNullOrWhiteSpace(definition.code) ? definition.code : definition.name;
    }

    private static string GetCargoLabel(WeaponMount mount, string loadoutName)
    {
        if (mount.info.rearmGround)
        {
            return "Ground supplies";
        }

        if (mount.info.rearmShip)
        {
            return "Naval supplies";
        }

        if (mount.prefab != null)
        {
            Unit[] cargoUnits = mount.prefab.GetComponentsInChildren<Unit>(true);
            for (int i = 0; i < cargoUnits.Length; i++)
            {
                Unit cargoUnit = cargoUnits[i];
                if (cargoUnit?.definition != null && !string.IsNullOrWhiteSpace(cargoUnit.definition.unitName))
                {
                    return cargoUnit.definition.unitName;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(mount.mountName))
        {
            return mount.mountName;
        }

        if (!string.IsNullOrWhiteSpace(mount.info.weaponName))
        {
            return mount.info.weaponName;
        }

        return string.IsNullOrWhiteSpace(loadoutName) ? mount.name : loadoutName;
    }

    internal sealed class CargoAircraftOption
    {
        internal CargoAircraftOption(
            AircraftDefinition definition,
            string label,
            List<CargoSlotOption> cargoSlots,
            Loadout baselineLoadout,
            HardpointSet[] hardpointSets)
        {
            Definition = definition;
            Label = label;
            CargoSlots = cargoSlots;
            BaselineLoadout = baselineLoadout;
            HardpointSets = hardpointSets;
        }

        internal AircraftDefinition Definition { get; }
        internal string Label { get; }
        internal List<CargoSlotOption> CargoSlots { get; }
        internal Loadout BaselineLoadout { get; }
        internal HardpointSet[] HardpointSets { get; }
    }

    internal sealed class CargoSlotOption
    {
        private int selectedIndex = -1;

        internal CargoSlotOption(int hardpointIndex, string label, List<WeaponMount> mounts, bool combinedBay)
        {
            HardpointIndex = hardpointIndex;
            Label = label;
            Mounts = mounts;
            IsCombinedBay = combinedBay;
        }

        internal int HardpointIndex { get; }
        internal string Label { get; }
        internal List<WeaponMount> Mounts { get; }
        internal bool IsCombinedBay { get; }
        internal WeaponMount? SelectedMount => selectedIndex >= 0 && selectedIndex < Mounts.Count
            ? Mounts[selectedIndex]
            : null;

        internal void CycleSelection()
        {
            selectedIndex++;
            if (selectedIndex >= Mounts.Count)
            {
                selectedIndex = -1;
            }
        }

        internal void Select(int index)
        {
            selectedIndex = index >= 0 && index < Mounts.Count ? index : -1;
        }

        internal void Clear()
        {
            selectedIndex = -1;
        }
    }

    internal sealed class AirbaseOption
    {
        internal AirbaseOption(Airbase airbase, string label, float distance, bool ready)
        {
            Airbase = airbase;
            Label = label;
            Distance = distance;
            Ready = ready;
        }

        internal Airbase Airbase { get; }
        internal string Label { get; }
        internal float Distance { get; }
        internal bool Ready { get; }
    }

}
