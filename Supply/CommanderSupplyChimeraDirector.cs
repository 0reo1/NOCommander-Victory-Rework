using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

/// <summary>
/// Keeps a self-flying fixed-wing supply aircraft (currently Aryx's MC-260 Chimera) pointed at
/// the landing zone the commander picked, instead of letting it wander off to a target its own
/// mission search chose.
///
/// The Chimera ships a complete autonomous transport AI (AIFixedWingTransportState) that picks
/// its own delivery mission on a timer - LandSupply, NavalSupply, BaseRepair, CombatVehicle or
/// Waiting - finds its own target, flies its own run-in, drops, and egresses. That is great when
/// nobody is commanding it, but it means a Chimera spawned by NOCommander will happily take off
/// and service some other target entirely, ignoring the LZ the player designated.
///
/// This class does the smallest useful intervention: it lets the Chimera's own AI do all of the
/// real work (mission plumbing, cargo station selection, run-in geometry, release timing), and
/// only overrides *where* the drop happens. It writes the commander's LZ into the state's
/// dropPoint each time it notices the Chimera's own search has moved it, then asks the state to
/// recompute its run-in from the current position.
///
/// Everything here is reflection against a soft dependency, so NOCommander still builds and runs
/// with the Chimera mod absent, and every step fails closed (logs once, then does nothing) rather
/// than throwing into the game's update loop.
/// </summary>
internal static class CommanderSupplyChimeraDirector
{
    private const string TransportStateTypeName = "Aryx_MC260_Chimera.AIFixedWingTransportState";
    private const string ControllerTypeName = "Aryx_MC260_Chimera.AryxChimeraAIController";
    private const string PreferredTransportModeName = "LandSupply";
    private const string PreferredNavalModeName = "NavalSupply";
    private const string PreferredBaseRepairModeName = "BaseRepair";

    // How close the commander LZ has to be to a friendly airbase before we treat the drop as
    // a base delivery, which is the mode the Chimera will actually validate for a fixed point.
    private const float AirbaseBindRadius = 2000f;

    // Generous, because a commander LZ is often placed on open ground rather than right on top
    // of a unit. The Chimera derives its own drop point from whatever unit we bind, so a unit a
    // few km from the marker still produces a delivery in roughly the right place.
    private const float GroundUnitBindRadius = 6000f;
    private const string PreferredMissionPriorityName = "TransportThenGunshipThenCombat";

    // Only ever intervene once the aircraft is unambiguously airborne and flying, so we can
    // never interfere with its own startup, taxi or takeoff handling.
    private const float MinimumAirborneRadarAlt = 40f;
    private const float MinimumAirborneSpeed = 25f;

    private static bool resolveAttempted;
    private static bool resolveFailed;

    private static Type? transportStateType;
    private static FieldInfo? transportModeField;
    private static FieldInfo? targetUnitField;
    private static FieldInfo? dropPointField;
    private static FieldInfo? missionValidField;
    private static FieldInfo? repairTargetAirbaseField;
    private static MethodInfo? setRunInMethod;
    private static MethodInfo? searchForMissionMethod;
    private static FieldInfo? cargoStationField;
    private static FieldInfo? cargoReleasedField;
    private static FieldInfo? dropZoneRegisteredField;
    private static FieldInfo? targetOffsetField;
    private static MethodInfo? updateMovingTargetMethod;
    private static object? landSupplyMode;
    private static object? navalSupplyMode;
    private static object? baseRepairMode;

    private static Type? controllerType;
    private static FieldInfo? missionPriorityField;
    private static object? transportFirstPriority;

    // Last dropPoint value this director wrote, per aircraft. Used to notice when the Chimera's
    // own mission search has replaced our LZ so we can put it back (and only then).
    private static readonly Dictionary<Aircraft, object> lastWrittenDropPoint = new();
    private static readonly HashSet<Aircraft> seenInTransport = new();
    private static readonly HashSet<Aircraft> deliveredAircraft = new();
    private static readonly Dictionary<Aircraft, float> lastPinLogAt = new();
    private static readonly Dictionary<Aircraft, MissionBinding> bindings = new();
    private static readonly Dictionary<Aircraft, float> lastRunInRebuildAt = new();
    private static readonly HashSet<Aircraft> pinnedOnce = new();

    // "aircraft" lives on PilotBaseState, so this resolves for the Chimera's own state too.
    private static readonly FieldInfo? StateAircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");

    private static Harmony? patchHarmony;
    private static bool patchesInstalled;

    /// <summary>
    /// Set true to log every pin/re-pin. Useful when checking whether the handoff is actually
    /// taking effect in a live mission; noisy otherwise.
    /// </summary>
    /// <summary>
    /// Master switch. Set false to leave Chimeras entirely alone (they will fly their own
    /// missions again, ignoring the commander LZ) without reverting any code.
    /// </summary>
    internal static bool Enabled { get; set; } = true;

    internal static bool VerboseLogging { get; set; } = true;

    internal static void Reset()
    {
        lastWrittenDropPoint.Clear();
        seenInTransport.Clear();
        deliveredAircraft.Clear();
        lastPinLogAt.Clear();
        bindings.Clear();
        lastRunInRebuildAt.Clear();
        pinnedOnce.Clear();
    }

    private static void ForgetTracking(Aircraft aircraft)
    {
        lastWrittenDropPoint.Remove(aircraft);
        seenInTransport.Remove(aircraft);
        deliveredAircraft.Remove(aircraft);
        lastPinLogAt.Remove(aircraft);
        bindings.Remove(aircraft);
        lastRunInRebuildAt.Remove(aircraft);
        pinnedOnce.Remove(aircraft);
    }

    internal static void Forget(Aircraft aircraft)
    {
        if (aircraft != null)
        {
            lastWrittenDropPoint.Remove(aircraft);
            seenInTransport.Remove(aircraft);
            deliveredAircraft.Remove(aircraft);
            lastPinLogAt.Remove(aircraft);
            bindings.Remove(aircraft);
            lastRunInRebuildAt.Remove(aircraft);
            pinnedOnce.Remove(aircraft);
        }
    }

    /// <summary>
    /// Called once when a self-flying supply aircraft is assigned a commander mission. Biases the
    /// aircraft's role selection toward transport so its role re-check doesn't convert our supply
    /// run into a gunship or combat sortie mid-flight.
    /// </summary>
    internal static void OnMissionAssigned(Aircraft aircraft)
    {
        if (!Enabled || aircraft == null || !TryResolve())
        {
            return;
        }

        ForgetTracking(aircraft);

        if (controllerType == null || missionPriorityField == null || transportFirstPriority == null)
        {
            return;
        }

        try
        {
            Component? controller = aircraft.GetComponent(controllerType) as Component;
            if (controller == null)
            {
                return;
            }

            missionPriorityField.SetValue(controller, transportFirstPriority);
            if (VerboseLogging)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Chimera director: pinned mission priority to {PreferredMissionPriorityName} on {aircraft.definition?.name}.");
            }
        }
        catch (Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"Chimera director: could not set mission priority: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-asserts the commander's LZ as the aircraft's drop point if the Chimera's own mission
    /// search has moved it. Safe to call on a timer for any aircraft; does nothing unless the
    /// aircraft is actually sitting in the Chimera transport state right now.
    /// </summary>
    internal static void EnforceDropPoint(Aircraft aircraft, GlobalPosition target, Unit? navalTarget, FactionHQ? hq)
    {
        if (!Enabled || aircraft == null || !TryResolve())
        {
            return;
        }

        object? state = FindTransportState(aircraft);
        if (state == null)
        {
            return;
        }

        if (VerboseLogging && seenInTransport.Add(aircraft))
        {
            CommanderPlugin.Log.LogInfo(
                $"Chimera director: {aircraft.definition?.name} entered its transport state "
                + $"(radarAlt={aircraft.radarAlt:F0}m, speed={aircraft.speed:F0}m/s).");
        }

        // Never touch the aircraft until it is clearly airborne and flying; writing mission
        // fields while it is parked stops it from ever spooling up.
        if (aircraft.radarAlt < MinimumAirborneRadarAlt || aircraft.speed < MinimumAirborneSpeed)
        {
            return;
        }

        try
        {
            // Backstop only. The Harmony postfixes do the real work, in-band with the Chimera's
            // own tick; this catches anything that drifts between its own decisions.
            InstallMission(state, aircraft, target, navalTarget, hq);
        }
        catch (Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"Chimera director: could not pin drop point: {ex.Message}");
        }
    }

    /// <summary>
    /// Which of the Chimera's own delivery modes we ask it to fly, plus whatever that mode needs
    /// bound to consider the mission valid.
    /// </summary>
    private readonly struct MissionBinding
    {
        internal MissionBinding(string kind, object? mode, Unit? targetUnit, Airbase? repairAirbase, bool forceDropPoint)
        {
            Kind = kind;
            Mode = mode;
            TargetUnit = targetUnit;
            RepairAirbase = repairAirbase;
            ForceDropPoint = forceDropPoint;
        }

        internal string Kind { get; }
        internal object? Mode { get; }
        internal Unit? TargetUnit { get; }
        internal Airbase? RepairAirbase { get; }

        /// <summary>
        /// Whether we write the drop point ourselves. Only for the LandSupply fallback: the
        /// NavalSupply and BaseRepair modes derive their own drop point from the ship or airbase
        /// we bound, and overwriting it every tick would reset their run-in continuously.
        /// </summary>
        internal bool ForceDropPoint { get; }
    }

    /// <summary>
    /// Picks the delivery mode that best matches what the commander designated, because the
    /// Chimera validates each mode against different state.
    /// </summary>
    private static MissionBinding ResolveBinding(GlobalPosition target, Unit? navalTarget, FactionHQ? hq)
    {
        if (navalTarget != null && navalSupplyMode != null)
        {
            return new MissionBinding("NavalSupply", navalSupplyMode, navalTarget, null, forceDropPoint: false);
        }

        // A real friendly ground unit is the binding LandSupply is actually built around, so try
        // that before anything else: if it accepts one, the Chimera flies its own properly tuned
        // delivery instead of a mission we forced on it.
        if (landSupplyMode != null && hq != null)
        {
            Unit? groundUnit = FindGroundUnitNear(target, hq);
            if (groundUnit != null)
            {
                return new MissionBinding("LandSupply+Unit", landSupplyMode, groundUnit, null, forceDropPoint: false);
            }
        }

        if (baseRepairMode != null && hq != null)
        {
            Airbase? nearest = FindAirbaseNear(target, hq);
            if (nearest != null)
            {
                return new MissionBinding("BaseRepair", baseRepairMode, null, nearest, forceDropPoint: false);
            }
        }

        return new MissionBinding("LandSupply", landSupplyMode, null, null, forceDropPoint: true);
    }

    /// <summary>
    /// Nearest friendly ground vehicle to the commander LZ. Scanned once per mission and cached
    /// in the binding, since FindObjectsOfType is not cheap.
    /// </summary>
    private static Unit? FindGroundUnitNear(GlobalPosition target, FactionHQ hq)
    {
        GroundVehicle[] vehicles = UnityEngine.Object.FindObjectsOfType<GroundVehicle>();
        Unit? best = null;
        float bestDistanceSq = GroundUnitBindRadius * GroundUnitBindRadius;

        for (int i = 0; i < vehicles.Length; i++)
        {
            GroundVehicle vehicle = vehicles[i];
            if (vehicle == null || !CommanderGameAccess.IsFriendlyUnit(vehicle, hq))
            {
                continue;
            }

            GlobalPosition position = vehicle.GlobalPosition();
            float dx = (float)(position.x - target.x);
            float dz = (float)(position.z - target.z);
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                best = vehicle;
            }
        }

        if (VerboseLogging)
        {
            CommanderPlugin.Log.LogInfo(
                best != null
                    ? $"Chimera director: bound friendly ground unit {best.unitName} "
                        + $"{Mathf.Sqrt(bestDistanceSq):F0}m from the LZ."
                    : $"Chimera director: no friendly ground unit within {GroundUnitBindRadius:F0}m of the LZ "
                        + $"(scanned {vehicles.Length}); falling back.");
        }

        return best;
    }

    private static Airbase? FindAirbaseNear(GlobalPosition target, FactionHQ hq)
    {
        Airbase? best = null;
        float bestDistanceSq = AirbaseBindRadius * AirbaseBindRadius;

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            Transform positionTransform = airbase.center != null ? airbase.center : airbase.transform;
            if (positionTransform == null)
            {
                continue;
            }

            GlobalPosition airbasePosition = positionTransform.GlobalPosition();
            float dx = (float)(airbasePosition.x - target.x);
            float dz = (float)(airbasePosition.z - target.z);
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                best = airbase;
            }
        }

        return best;
    }

    private static string DescribeMode(object? mode)
    {
        return mode != null ? mode.ToString() : "null";
    }

    /// <summary>
    /// Finds a loaded cargo station on the aircraft, using the same checks NOCommander's own
    /// cargo handling uses.
    /// </summary>
    private static WeaponStation? FindCargoStation(Aircraft aircraft)
    {
        if (aircraft.weaponStations == null)
        {
            return null;
        }

        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station == null || !station.Cargo || station.WeaponInfo == null || !station.WeaponInfo.cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = station.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    return station;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the commander's delivery into the Chimera's transport state. Safe to call often;
    /// it only touches what has drifted, and only rebuilds the run-in when the destination we
    /// own actually moved.
    /// </summary>
    private static void InstallMission(
        object state,
        Aircraft aircraft,
        GlobalPosition target,
        Unit? navalTarget,
        FactionHQ? hq)
    {
        // Once the cargo is away the sortie belongs to the Chimera again.
        if (cargoReleasedField?.GetValue(state) is bool released && released)
        {
            if (VerboseLogging && deliveredAircraft.Add(aircraft))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Chimera director: {aircraft.definition?.name} released its cargo; handing control back.");
            }

            return;
        }

        if (!bindings.TryGetValue(aircraft, out MissionBinding binding))
        {
            binding = ResolveBinding(target, navalTarget, hq);
            bindings[aircraft] = binding;
            if (VerboseLogging)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Chimera director: delivering via {binding.Kind} for {aircraft.definition?.name} "
                    + $"(targetUnit={(binding.TargetUnit != null ? binding.TargetUnit.unitName : "none")}, "
                    + $"repairBase={(binding.RepairAirbase != null ? binding.RepairAirbase.name : "none")}).");
            }
        }

        object boxedTarget = target;
        object? mode = binding.Mode;
        object? theirModeBefore = transportModeField?.GetValue(state);
        object? currentDropPoint = dropPointField?.GetValue(state);

        bool dropPointOurs = !binding.ForceDropPoint
            || (lastWrittenDropPoint.TryGetValue(aircraft, out object? lastWritten)
                && currentDropPoint != null
                && lastWritten != null
                && currentDropPoint.Equals(lastWritten));
        bool missionStillValid = missionValidField?.GetValue(state) is bool valid && valid;
        bool modeOurs = mode == null || (theirModeBefore != null && theirModeBefore.Equals(mode));
        bool targetBound = targetUnitField == null
            || Equals(targetUnitField.GetValue(state), binding.TargetUnit);

        if (dropPointOurs && missionStillValid && modeOurs && targetBound)
        {
            return;
        }

        bool firstPin = !pinnedOnce.Contains(aircraft);
        bool hasOwnMission = missionStillValid;

        if (mode != null)
        {
            transportModeField?.SetValue(state, mode);
        }

        targetUnitField?.SetValue(state, binding.TargetUnit);
        repairTargetAirbaseField?.SetValue(state, binding.RepairAirbase);

        if (binding.ForceDropPoint)
        {
            dropPointField?.SetValue(state, boxedTarget);
        }

        if (!hasOwnMission)
        {
            if (cargoStationField != null && cargoStationField.GetValue(state) == null)
            {
                WeaponStation? cargoStation = FindCargoStation(aircraft);
                if (cargoStation != null)
                {
                    cargoStationField.SetValue(state, cargoStation);
                }
            }

            dropZoneRegisteredField?.SetValue(state, false);
            missionValidField?.SetValue(state, true);
        }

        float nowTime = Time.timeSinceLevelLoad;
        if (binding.ForceDropPoint
            && !dropPointOurs
            && (!lastRunInRebuildAt.TryGetValue(aircraft, out float rebuiltAt) || nowTime - rebuiltAt >= 10f))
        {
            lastRunInRebuildAt[aircraft] = nowTime;
            setRunInMethod?.Invoke(state, Array.Empty<object>());
        }

        if (binding.ForceDropPoint)
        {
            lastWrittenDropPoint[aircraft] = boxedTarget;
        }

        pinnedOnce.Add(aircraft);

        if (VerboseLogging
            && (!lastPinLogAt.TryGetValue(aircraft, out float loggedAt) || nowTime - loggedAt >= 5f))
        {
            lastPinLogAt[aircraft] = nowTime;
            CommanderPlugin.Log.LogInfo(
                $"Chimera director: holding commander LZ on {aircraft.definition?.name} "
                + $"(via={binding.Kind}, ownMission={hasOwnMission}, firstPin={firstPin}, "
                + $"wasValid={missionStillValid}, modeOurs={modeOurs}, dropPointOurs={dropPointOurs}, "
                + $"targetBound={targetBound}, theirMode={DescribeMode(theirModeBefore)}, "
                + $"radarAlt={aircraft.radarAlt:F0}m).");
        }
    }

    private static void TryInstallPatches()
    {
        if (patchesInstalled || transportStateType == null)
        {
            return;
        }

        patchesInstalled = true;

        try
        {
            patchHarmony ??= new Harmony("nuclearoptioncommander.chimera");

            MethodInfo? setWaiting = transportStateType.GetMethod(
                "SetWaiting",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            MethodInfo postfix = typeof(CommanderSupplyChimeraDirector)
                .GetMethod(nameof(ReassertPostfix), BindingFlags.Static | BindingFlags.NonPublic)!;

            if (setWaiting != null)
            {
                patchHarmony.Patch(setWaiting, postfix: new HarmonyMethod(postfix));
            }

            if (searchForMissionMethod != null)
            {
                patchHarmony.Patch(searchForMissionMethod, postfix: new HarmonyMethod(postfix));
            }

            if (updateMovingTargetMethod != null)
            {
                MethodInfo retarget = typeof(CommanderSupplyChimeraDirector)
                    .GetMethod(nameof(RetargetDropPointPostfix), BindingFlags.Static | BindingFlags.NonPublic)!;
                patchHarmony.Patch(updateMovingTargetMethod, postfix: new HarmonyMethod(retarget));
            }

            CommanderPlugin.Log.LogInfo(
                $"Chimera director: installed in-band AI patches (setWaiting={setWaiting != null}, "
                + $"searchForMission={searchForMissionMethod != null}, "
                + $"updateMovingTarget={updateMovingTargetMethod != null}).");
        }
        catch (Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"Chimera director: could not install AI patches: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs immediately after the Chimera decides it has nothing to do. If the commander gave
    /// this aircraft a delivery, install it right here so its very next tick flies our mission.
    /// </summary>
    private static void ReassertPostfix(object __instance)
    {
        if (!Enabled || __instance == null || !TryResolve())
        {
            return;
        }

        try
        {
            if (StateAircraftField?.GetValue(__instance) is not Aircraft aircraft
                || aircraft.disabled
                || !CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(aircraft))
            {
                return;
            }

            // Same airborne gate as the timer path: never touch it on the ramp, or it never
            // starts up at all.
            if (aircraft.radarAlt < MinimumAirborneRadarAlt || aircraft.speed < MinimumAirborneSpeed)
            {
                return;
            }

            if (!CommanderSupplyHeliService.TryGetSelfFlyingMission(
                    aircraft,
                    out GlobalPosition target,
                    out Unit? navalTarget,
                    out FactionHQ? hq))
            {
                return;
            }

            InstallMission(__instance, aircraft, target, navalTarget, hq);
        }
        catch (Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"Chimera director: in-band re-assert failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs right after the Chimera recomputes its drop point from whichever unit it is
    /// targeting. LandSupply puts the cargo on the unit, which is why the first successful drop
    /// landed roughly a kilometre from the commander's marker. Offsetting from the unit's current
    /// position puts it on the marker instead, while leaving the delivery logic itself alone.
    /// </summary>
    private static void RetargetDropPointPostfix(object __instance)
    {
        if (!Enabled || __instance == null || !TryResolve())
        {
            return;
        }

        try
        {
            if (StateAircraftField?.GetValue(__instance) is not Aircraft aircraft
                || aircraft.disabled
                || !CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(aircraft))
            {
                return;
            }

            // Once the cargo is gone its return leg is its own business.
            if (cargoReleasedField?.GetValue(__instance) is bool released && released)
            {
                return;
            }

            if (!CommanderSupplyHeliService.TryGetSelfFlyingMission(
                    aircraft,
                    out GlobalPosition target,
                    out Unit? _,
                    out FactionHQ? _))
            {
                return;
            }

            // Express the commander LZ as an offset from whatever unit it settled on, so its own
            // drop-point maths resolves to our marker.
            if (targetOffsetField != null && targetUnitField?.GetValue(__instance) is Unit targetedUnit)
            {
                GlobalPosition unitPosition = targetedUnit.GlobalPosition();
                targetOffsetField.SetValue(
                    __instance,
                    new Vector3(
                        (float)(target.x - unitPosition.x),
                        (float)(target.y - unitPosition.y),
                        (float)(target.z - unitPosition.z)));
            }

            // Belt and braces: the drop point has just been recomputed, so put it on the LZ.
            dropPointField?.SetValue(__instance, target);
        }
        catch (Exception ex)
        {
            CommanderPlugin.Log.LogWarning($"Chimera director: drop point retarget failed: {ex.Message}");
        }
    }

    private static object? FindTransportState(Aircraft aircraft)
    {
        if (transportStateType == null || aircraft.pilots == null)
        {
            return null;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot? pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            object? current = pilot.currentState;
            if (current != null && transportStateType.IsInstanceOfType(current))
            {
                return current;
            }
        }

        return null;
    }

    private static bool TryResolve()
    {
        if (resolveFailed)
        {
            return false;
        }

        if (resolveAttempted && transportStateType != null)
        {
            return true;
        }

        resolveAttempted = true;

        transportStateType = FindType(TransportStateTypeName);
        if (transportStateType == null)
        {
            // Chimera mod not loaded (yet). Don't latch a failure - it may load later.
            resolveAttempted = false;
            return false;
        }

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        transportModeField = transportStateType.GetField("transportMode", Instance);
        targetUnitField = transportStateType.GetField("targetUnit", Instance);
        dropPointField = transportStateType.GetField("dropPoint", Instance);
        missionValidField = transportStateType.GetField("missionValid", Instance);
        repairTargetAirbaseField = transportStateType.GetField("repairTargetAirbase", Instance);
        setRunInMethod = transportStateType.GetMethod("SetRunInFromCurrentPosition", Instance, null, Type.EmptyTypes, null);
        searchForMissionMethod = transportStateType.GetMethod("SearchForMission", Instance, null, new[] { typeof(bool) }, null);
        cargoStationField = transportStateType.GetField("cargoStation", Instance);
        cargoReleasedField = transportStateType.GetField("cargoReleased", Instance);
        dropZoneRegisteredField = transportStateType.GetField("dropZoneRegistered", Instance);
        targetOffsetField = transportStateType.GetField("targetOffset", Instance);
        updateMovingTargetMethod = transportStateType.GetMethod("UpdateMovingTarget", Instance, null, Type.EmptyTypes, null);

        if (dropPointField == null || missionValidField == null)
        {
            resolveFailed = true;
            CommanderPlugin.Log.LogWarning(
                "Chimera director: the Chimera transport AI does not expose the expected drop point fields; "
                + "commander LZs will not be enforced for it. The Chimera mod may have changed version.");
            return false;
        }

        landSupplyMode = ParseEnumField(transportModeField, PreferredTransportModeName);
        navalSupplyMode = ParseEnumField(transportModeField, PreferredNavalModeName);
        baseRepairMode = ParseEnumField(transportModeField, PreferredBaseRepairModeName);

        TryInstallPatches();

        controllerType = FindType(ControllerTypeName);
        if (controllerType != null)
        {
            missionPriorityField = controllerType.GetField("missionPriority", Instance);
            transportFirstPriority = ParseEnumField(missionPriorityField, PreferredMissionPriorityName);
        }

        return true;
    }

    private static object? ParseEnumField(FieldInfo? field, string valueName)
    {
        if (field == null || !field.FieldType.IsEnum)
        {
            return null;
        }

        try
        {
            return Enum.Parse(field.FieldType, valueName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Type? FindType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                Type? found = assemblies[i].GetType(fullName, throwOnError: false);
                if (found != null)
                {
                    return found;
                }
            }
            catch (Exception)
            {
                // Assembly not fully loadable; skip it.
            }
        }

        return null;
    }
}
