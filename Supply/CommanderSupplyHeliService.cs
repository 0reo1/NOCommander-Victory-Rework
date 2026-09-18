using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed partial class CommanderSupplyHeliService
{
    private const string SamSiteCargoSupportSummary = "Automatic SAM-site ammunition run";
    private const string SamSiteCargoSupportPrefix = "SAM-site ammunition:";
    private const string SamSiteFoundationSupportPrefix = "SAM-site foundation:";
    private const string SamSiteJacknifeSupportPrefix = "SAM-site Jacknife:";
    private const float StatusDurationSeconds = 5f;
    private const float PendingSpawnTimeoutSeconds = 90f;

    private static readonly FieldInfo? AircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");
    private static readonly FieldInfo? PilotField = AccessTools.Field(typeof(PilotBaseState), "pilot");
    private static readonly FieldInfo? LastLandingSpotCheckField = AccessTools.Field(typeof(AIHeloTransportState), "lastLandingSpotCheck");
    private static readonly FieldInfo? TouchedDownTimeField = AccessTools.Field(typeof(AIHeloTransportState), "touchedDownTime");
    private static readonly FieldInfo? TimeWithoutMissionField = AccessTools.Field(typeof(AIHeloTransportState), "timeWithoutMission");
    private static readonly FieldInfo? AirdropField = AccessTools.Field(typeof(AIHeloTransportState), "airdrop");
    private static readonly FieldInfo? TransportModeField = AccessTools.Field(typeof(AIHeloTransportState), "transportMode");
    private static readonly FieldInfo? TransportDestinationField = AccessTools.Field(typeof(AIHeloTransportState), "transportDestination");
    private static readonly Type? TransportDestinationType = typeof(AIHeloTransportState).GetNestedType("TransportDestination", BindingFlags.NonPublic);
    private static readonly FieldInfo? DestinationValidMissionField = AccessTools.Field(TransportDestinationType, "validMission");
    private static readonly FieldInfo? DestinationDropConditionsField = AccessTools.Field(TransportDestinationType, "dropConditionsMet");
    private static readonly FieldInfo? DestinationTouchdownField = AccessTools.Field(TransportDestinationType, "touchdownPoint");
    private static readonly FieldInfo? DestinationEnemyPositionField = AccessTools.Field(TransportDestinationType, "enemyPosition");
    private static readonly FieldInfo? DestinationLzField = AccessTools.Field(TransportDestinationType, "LZ");
    private static readonly FieldInfo? DestinationSlopeField = AccessTools.Field(TransportDestinationType, "slope");
    private static readonly FieldInfo? DestinationAttemptsField = AccessTools.Field(TransportDestinationType, "touchdownPointAttempts");
    private static readonly MethodInfo? UpdateTouchdownPointMethod = AccessTools.Method(TransportDestinationType, "UpdateTouchdownPoint");
    private static readonly MethodInfo? UpdateLzForUnitMethod = AccessTools.Method(
        TransportDestinationType,
        "UpdateLZ",
        new[] { typeof(Aircraft), typeof(Unit) });
    private static readonly FieldInfo? GroundVehicleParachuteField = AccessTools.Field(typeof(GroundVehicle), "parachuteSystem");
    private static readonly FieldInfo? ContainerParachuteField = AccessTools.Field(typeof(Container), "parachuteSystem");
    private static readonly FieldInfo? LandingStatePointField =
        AccessTools.Field(typeof(AIHeloLandingState), "landingPoint");
    private static readonly FieldInfo? LandingStateReachedApproachField =
        AccessTools.Field(typeof(AIHeloLandingState), "reachedApproachPoint");
    private static readonly FieldInfo? StateNearestAirbaseField =
        AccessTools.Field(typeof(PilotBaseState), "nearestAirbase");
    private static readonly FieldInfo? StateDestinationField =
        AccessTools.Field(typeof(PilotBaseState), "destination");
    private static readonly FieldInfo? WeaponHardpointField = AccessTools.Field(typeof(Weapon), "hardpoint");
    private static readonly MethodInfo? MountedCargoRemoveMethod = AccessTools.Method(typeof(MountedCargo), "RemoveFromHardpoint");
    private static readonly FieldInfo? BayDoorOpenTimerField = AccessTools.Field(typeof(BayDoor), "openTimer");
    private static readonly FieldInfo? BayDoorOpenAmountField = AccessTools.Field(typeof(BayDoor), "openAmount");
    private static readonly FieldInfo? SwivelAircraftField = AccessTools.Field(typeof(SwivelDuctSystem), "aircraft");

    private readonly List<CargoAircraftOption> aircraftOptions = new();
    private readonly List<AirbaseOption> airbaseOptions = new();
    private readonly Queue<QueuedCargoSpawn> queuedCargoSpawns = new();
    private readonly Dictionary<Aircraft, CargoMission> assignedMissions = new();
    private readonly Dictionary<Autopilot, float> terrainClearanceAutopilots = new();
    private readonly Dictionary<Autopilot, Aircraft> assignedAutopilotAircraft = new();
    private readonly HashSet<Aircraft> pendingTerrainAutopilotBindings = new();
    private readonly HashSet<Unit> pendingSamCargoDeposits = new();
    private PendingTargetSelection? pendingTargetSelection;
    private PendingAircraftSpawn? pendingAircraftSpawn;
    private Airbase? selectedAirbase;
    private int selectedAircraftIndex;
    private bool highTerrainClearance;
    private float terrainClearanceMeters = 250f;
    private bool airdropDelivery;
    private bool includeEcm = true;
    private bool includeCountermeasures = true;
    private bool fillRemainingHardpoints;
    private bool useOtherAirfields = true;
    private bool uiVisible;
    private float nextAirbaseRefreshAt;
    private float nextMissionPruneAt;
    private float nextSelfFlyingDirectAt;
    private float statusUntil;
    private string statusText = string.Empty;

    internal CommanderSupplyHeliService()
    {
        Instance = this;
    }

    internal static CommanderSupplyHeliService? Instance { get; private set; }
    internal IReadOnlyList<CargoAircraftOption> AircraftOptions => aircraftOptions;
    internal IReadOnlyList<AirbaseOption> AirbaseOptions => airbaseOptions;
    internal bool AwaitingTargetSelection => pendingTargetSelection != null;
    internal int QueuedSpawnCount => queuedCargoSpawns.Count;

    internal void CopyActiveDeliveryTargets(List<GlobalPosition> targets)
    {
        targets.Clear();
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Value.TargetOverrideActive)
            {
                targets.Add(entry.Value.Target);
            }
        }
    }
    internal string StatusText => AwaitingTargetSelection
        ? pendingTargetSelection!.GetTargetPrompt()
        : Time.unscaledTime <= statusUntil
            ? statusText
            : queuedCargoSpawns.Count > 0
                ? $"Waiting for an available supply hangar ({queuedCargoSpawns.Count} queued)."
                : string.Empty;
    internal int SelectedAircraftIndex => selectedAircraftIndex;
    internal bool HighTerrainClearance
    {
        get => highTerrainClearance;
        set => highTerrainClearance = value;
    }

    internal float TerrainClearanceMeters
    {
        get => terrainClearanceMeters;
        set => terrainClearanceMeters = Mathf.Max(value, 0f);
    }

    internal bool AirdropDelivery
    {
        get => airdropDelivery;
        set => airdropDelivery = value && SelectedCargoSupportsAirdrop;
    }

    internal bool IncludeEcm
    {
        get => includeEcm;
        set => includeEcm = value;
    }

    internal bool IncludeCountermeasures
    {
        get => includeCountermeasures;
        set => includeCountermeasures = value;
    }

    internal bool FillRemainingHardpoints
    {
        get => fillRemainingHardpoints;
        set => fillRemainingHardpoints = value;
    }

    internal bool UseOtherAirfields
    {
        get => useOtherAirfields;
        set => useOtherAirfields = value;
    }

    internal bool SelectedCargoSupportsAirdrop => SelectedAircraft != null
        && SelectedAircraft.CargoSlots.Exists(slot => slot.SelectedMount != null)
        && SelectedAircraft.CargoSlots.TrueForAll(slot => slot.SelectedMount == null || CargoMountSupportsAirdrop(slot.SelectedMount));
    internal bool HasSelectedCargo => SelectedAircraft != null
        && SelectedAircraft.CargoSlots.Exists(slot => slot.SelectedMount != null);

    internal Airbase? SelectedAirbase => selectedAirbase;

    internal CargoAircraftOption? SelectedAircraft => aircraftOptions.Count == 0
        ? null
        : aircraftOptions[Mathf.Clamp(selectedAircraftIndex, 0, aircraftOptions.Count - 1)];

    internal void Activate()
    {
        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }
    }

    internal void Deactivate()
    {
        uiVisible = false;
        CancelTargetSelection(showStatus: false);
    }

    internal void SetUiVisible(bool visible)
    {
        uiVisible = visible;
    }

    internal void CancelDeploymentSelection()
    {
        CancelTargetSelection(showStatus: false);
        SetStatus("Deployment selection cleared.");
    }

    internal void TickActive()
    {
        if (AwaitingTargetSelection && CommanderGameInput.CancelDown)
        {
            CancelTargetSelection(showStatus: true);
        }
    }

    internal void TickPersistent()
    {
        BindPendingTerrainAutopilots();

        if (pendingAircraftSpawn != null && Time.unscaledTime > pendingAircraftSpawn.ExpiresAt)
        {
            CommanderPlugin.Log.LogWarning($"Supply cargo run assignment timed out: aircraft={pendingAircraftSpawn.Definition.name}");
            NotifySamMissionFailed(pendingAircraftSpawn.SupportSummary);
            pendingAircraftSpawn = null;
        }

        if (CommanderScheduler.IsDue(ref nextAirbaseRefreshAt, 1f))
        {
            if (uiVisible)
            {
                RefreshAirbaseOptions();
            }

            if (queuedCargoSpawns.Count > 0)
            {
                TryProcessQueuedCargoSpawns();
            }
        }

        if (CommanderScheduler.IsDue(ref nextSelfFlyingDirectAt, 1f))
        {
            DirectSelfFlyingSupplyMissions();
        }

        if (CommanderScheduler.IsDue(ref nextMissionPruneAt, 2f))
        {
            PruneFinishedMissions();
        }
    }

    internal void ResetSession()
    {
        aircraftOptions.Clear();
        airbaseOptions.Clear();
        assignedMissions.Clear();
        terrainClearanceAutopilots.Clear();
        assignedAutopilotAircraft.Clear();
        pendingTerrainAutopilotBindings.Clear();
        pendingSamCargoDeposits.Clear();
        pendingTargetSelection = null;
        pendingAircraftSpawn = null;
        uiVisible = false;
        queuedCargoSpawns.Clear();
        selectedAirbase = null;
        selectedAircraftIndex = 0;
        highTerrainClearance = false;
        airdropDelivery = false;
        includeEcm = true;
        includeCountermeasures = true;
        fillRemainingHardpoints = false;
        useOtherAirfields = true;
        nextAirbaseRefreshAt = CommanderScheduler.Stagger("supply.airbases", 1f, 0.5f);
        nextMissionPruneAt = CommanderScheduler.Stagger("supply.prune", 2f, 0.8f);
        nextSelfFlyingDirectAt = CommanderScheduler.Stagger("supply.selfflying", 1f, 0.3f);
        CommanderSupplyChimeraDirector.Reset();
        statusText = string.Empty;
    }

    internal void CancelSamSiteMissions(int siteId)
    {
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            CargoMission mission = entry.Value;
            if (mission.FoundationSiteId != siteId
                && mission.DepositSiteId != siteId
                && mission.JacknifeSiteId != siteId)
            {
                continue;
            }

            mission.TargetOverrideActive = false;
            mission.Cancelled = true;
            mission.CargoClearancePending = false;
            IssueSupplyReturnToBase(entry.Key, mission);
            if (entry.Key?.autopilot != null)
            {
                terrainClearanceAutopilots.Remove(entry.Key.autopilot);
                assignedAutopilotAircraft.Remove(entry.Key.autopilot);
            }
            if (entry.Key != null)
            {
                pendingTerrainAutopilotBindings.Remove(entry.Key);
            }
        }

        if (pendingAircraftSpawn != null
            && SupportSummaryMatchesSite(pendingAircraftSpawn.SupportSummary, siteId))
        {
            pendingAircraftSpawn = null;
        }

        int queuedCount = queuedCargoSpawns.Count;
        for (int i = 0; i < queuedCount; i++)
        {
            QueuedCargoSpawn request = queuedCargoSpawns.Dequeue();
            if (!SupportSummaryMatchesSite(request.SupportSummary, siteId))
            {
                queuedCargoSpawns.Enqueue(request);
            }
        }
    }

    internal void RefreshOptions()
    {
        aircraftOptions.Clear();
        AircraftDefinition[] definitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        HashSet<AircraftDefinition> seenDefinitions = new();
        for (int i = 0; i < definitions.Length; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (!seenDefinitions.Add(definition))
            {
                continue;
            }

            CargoAircraftOption? option = CreateAircraftOption(definition);
            if (option != null)
            {
                aircraftOptions.Add(option);
            }
        }

        aircraftOptions.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        selectedAircraftIndex = Mathf.Clamp(selectedAircraftIndex, 0, Mathf.Max(aircraftOptions.Count - 1, 0));
        RefreshAirbaseOptions();
    }

    internal void SelectAircraft(int index)
    {
        if (index < 0 || index >= aircraftOptions.Count)
        {
            return;
        }

        selectedAircraftIndex = index;
        RefreshAirbaseOptions();
    }

    internal void SelectAirbase(int index)
    {
        if (index < 0 || index >= airbaseOptions.Count)
        {
            return;
        }

        selectedAirbase = airbaseOptions[index].Airbase;
    }

    internal void CycleCargoSlot(int slotIndex)
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null || slotIndex < 0 || slotIndex >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CargoSlotOption selectedSlot = aircraft.CargoSlots[slotIndex];
        selectedSlot.CycleSelection();
        if (selectedSlot.SelectedMount == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CargoSlotOption other = aircraft.CargoSlots[i];
            if (!ReferenceEquals(other, selectedSlot)
                && other.SelectedMount != null
                && SetsConflict(aircraft.HardpointSets, selectedSlot.HardpointIndex, other.HardpointIndex))
            {
                other.Clear();
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal void SelectCargoMount(int slotIndex, int mountIndex)
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null || slotIndex < 0 || slotIndex >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CargoSlotOption selectedSlot = aircraft.CargoSlots[slotIndex];
        selectedSlot.Select(mountIndex);
        if (selectedSlot.SelectedMount != null)
        {
            for (int i = 0; i < aircraft.CargoSlots.Count; i++)
            {
                CargoSlotOption other = aircraft.CargoSlots[i];
                if (!ReferenceEquals(other, selectedSlot)
                    && other.SelectedMount != null
                    && SetsConflict(aircraft.HardpointSets, selectedSlot.HardpointIndex, other.HardpointIndex))
                {
                    other.Clear();
                }
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal void ClearSelectedCargo()
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            aircraft.CargoSlots[i].Clear();
        }

        airdropDelivery = false;
    }

    internal void RandomizeSelectedCargo()
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null)
        {
            return;
        }

        ClearSelectedCargo();
        List<int> slotOrder = new();
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            slotOrder.Add(i);
        }

        Shuffle(slotOrder);
        for (int i = 0; i < slotOrder.Count; i++)
        {
            CargoSlotOption slot = aircraft.CargoSlots[slotOrder[i]];
            bool blocked = false;
            for (int otherIndex = 0; otherIndex < aircraft.CargoSlots.Count; otherIndex++)
            {
                CargoSlotOption other = aircraft.CargoSlots[otherIndex];
                if (other.SelectedMount != null
                    && SetsConflict(aircraft.HardpointSets, slot.HardpointIndex, other.HardpointIndex))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked && slot.Mounts.Count > 0)
            {
                slot.Select(UnityEngine.Random.Range(0, slot.Mounts.Count));
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal string GetCargoSlotButtonLabel(CargoSlotOption slot)
    {
        string cargo = slot.SelectedMount != null
            ? GetCargoLabel(slot.SelectedMount, string.Empty)
            : "None";
        return $"{slot.Label}\n{cargo}";
    }

    internal string GetCargoMountLabel(WeaponMount mount)
    {
        string suffix = CargoMountSupportsAirdrop(mount) ? " [Airdrop]" : string.Empty;
        return GetCargoLabel(mount, string.Empty) + suffix;
    }

    internal string GetAircraftButtonLabel(CargoAircraftOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = UnitConverter.ValueReading(option.Definition.value) ?? option.Definition.value.ToString("F1");
        return $"{option.Label} | Supply {supply} | {cost}";
    }

    internal string GetAirbaseButtonLabel(AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{(option.Ready ? "READY" : "WAIT")} | {option.Label} | {distance}";
    }

    internal void BeginSelectedCargoRun()
    {
        CargoAircraftOption? aircraftOption = SelectedAircraft;
        if (aircraftOption == null)
        {
            SetStatus("No cargo-capable aircraft is selected.");
            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return;
        }

        Airbase? airbase = selectedAirbase;
        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("Select a friendly airbase that supports this aircraft.");
            return;
        }

        Loadout cargoLoadout = BuildSelectedCargoLoadout(
            aircraftOption,
            airbase!,
            hq!,
            includeEcm,
            includeCountermeasures,
            fillRemainingHardpoints,
            out int cargoCount,
            out string cargoLabel,
            out string supportSummary);
        if (cargoCount <= 0)
        {
            SetStatus("Select cargo for at least one cargo bay.");
            return;
        }

        if (airdropDelivery && !SelectedCargoSupportsAirdrop)
        {
            airdropDelivery = false;
            SetStatus("Airdrop is unavailable because selected cargo has no parachute system.");
            return;
        }

        if (hq!.GetUnitSupply(aircraftOption.Definition) <= 0 && hq.factionFunds < aircraftOption.Definition.value)
        {
            SetStatus("The faction cannot afford this supply aircraft.");
            return;
        }

        pendingTargetSelection = new PendingTargetSelection(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase!,
            highTerrainClearance,
            terrainClearanceMeters,
            airdropDelivery,
            supportSummary,
            useOtherAirfields);
        SetStatus(pendingTargetSelection.GetTargetPrompt());
    }

    internal bool RequestAutomaticCargoRun(GlobalPosition target)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        List<(CargoAircraftOption aircraft, Airbase airbase, CargoSlotOption slot, WeaponMount mount, float capacity)> choices = new();
        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition)
                    || !IsSamSupplyAirbaseSafe(airbase, target))
                {
                    continue;
                }

                for (int slotIndex = 0; slotIndex < aircraft.CargoSlots.Count; slotIndex++)
                {
                    CargoSlotOption slot = aircraft.CargoSlots[slotIndex];
                    for (int mountIndex = 0; mountIndex < slot.Mounts.Count; mountIndex++)
                    {
                        WeaponMount mount = slot.Mounts[mountIndex];
                        if (WeaponChecker.MountAllowedHQ(mount, hq)
                            && WeaponChecker.MountAllowedAirbase(mount, airbase)
                            && TryGetAmmunitionCargoCapacity(mount, out float capacity))
                        {
                            choices.Add((aircraft, airbase, slot, mount, capacity));
                        }
                    }
                }
            }
        }

        if (choices.Count == 0)
        {
            SetStatus("No compatible ammunition cargo helicopter and airbase combination is available.");
            return false;
        }

        float largestCapacity = 0f;
        for (int i = 0; i < choices.Count; i++)
        {
            largestCapacity = Mathf.Max(largestCapacity, choices[i].capacity);
        }
        choices.RemoveAll(choice => choice.capacity < largestCapacity - 0.01f);
        var choice = choices[UnityEngine.Random.Range(0, choices.Count)];
        Loadout loadout = CreateEmptyLoadout(choice.aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.aircraft.HardpointSets,
            choice.slot.HardpointIndex,
            choice.mount);
        string cargoLabel = GetCargoLabel(choice.mount, string.Empty);
        SpawnCargoRun(
            choice.aircraft,
            loadout,
            cargoLabel,
            choice.airbase,
            useHighTerrainClearance: true,
            terrainClearanceMeters: 100f,
            useAirdrop: false,
            supportSummary: SamSiteCargoSupportSummary,
            useOtherAirfields: true,
            target);
        return true;
    }

    internal bool RequestSamSiteFoundationDrop(int siteId, GlobalPosition target)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        (CargoAircraftOption aircraft, Airbase airbase, CargoSlotOption jackSlot, WeaponMount jackMount,
            CargoSlotOption ammoSlot, WeaponMount ammoMount, float ammoCapacity)? best = null;
        (CargoAircraftOption aircraft, Airbase airbase, CargoSlotOption slot, WeaponMount mount)? bestJack = null;
        (CargoAircraftOption aircraft, Airbase airbase, CargoSlotOption slot, WeaponMount mount, float capacity)? bestAmmo = null;
        float bestDistance = float.MaxValue;
        float bestJackDistance = float.MaxValue;
        float bestAmmoDistance = float.MaxValue;
        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (!IsAvailableAirbase(airbase, hq, aircraft.Definition)
                    || !IsSamSupplyAirbaseSafe(airbase, target))
                {
                    continue;
                }

                Transform airbaseTransform = airbase.center != null ? airbase.center : airbase.transform;
                float airbaseDistance = HorizontalSquareDistance(
                    airbaseTransform.GlobalPosition(),
                    target);

                for (int jackSlotIndex = 0; jackSlotIndex < aircraft.CargoSlots.Count; jackSlotIndex++)
                {
                    CargoSlotOption jackSlot = aircraft.CargoSlots[jackSlotIndex];
                    for (int jackMountIndex = 0; jackMountIndex < jackSlot.Mounts.Count; jackMountIndex++)
                    {
                        WeaponMount jackMount = jackSlot.Mounts[jackMountIndex];
                        if (IsJacknifeCargo(jackMount)
                            && WeaponChecker.MountAllowedHQ(jackMount, hq)
                            && WeaponChecker.MountAllowedAirbase(jackMount, airbase))
                        {
                            if (bestJack == null || airbaseDistance < bestJackDistance)
                            {
                                bestJack = (aircraft, airbase, jackSlot, jackMount);
                                bestJackDistance = airbaseDistance;
                            }
                        }
                        if (TryGetAmmunitionCargoCapacity(jackMount, out float standaloneCapacity)
                            && WeaponChecker.MountAllowedHQ(jackMount, hq)
                            && WeaponChecker.MountAllowedAirbase(jackMount, airbase)
                            && (bestAmmo == null
                                || airbaseDistance < bestAmmoDistance - 1f
                                || (Mathf.Abs(airbaseDistance - bestAmmoDistance) <= 1f
                                    && standaloneCapacity > bestAmmo.Value.capacity)))
                        {
                            bestAmmo = (aircraft, airbase, jackSlot, jackMount, standaloneCapacity);
                            bestAmmoDistance = airbaseDistance;
                        }

                        if (!IsJacknifeCargo(jackMount)
                            || !WeaponChecker.MountAllowedHQ(jackMount, hq)
                            || !WeaponChecker.MountAllowedAirbase(jackMount, airbase))
                        {
                            continue;
                        }

                        for (int ammoSlotIndex = 0; ammoSlotIndex < aircraft.CargoSlots.Count; ammoSlotIndex++)
                        {
                            CargoSlotOption ammoSlot = aircraft.CargoSlots[ammoSlotIndex];
                            if (ammoSlot.HardpointIndex == jackSlot.HardpointIndex
                                || SetsConflict(
                                    aircraft.HardpointSets,
                                    jackSlot.HardpointIndex,
                                    ammoSlot.HardpointIndex))
                            {
                                continue;
                            }

                            for (int ammoMountIndex = 0; ammoMountIndex < ammoSlot.Mounts.Count; ammoMountIndex++)
                            {
                                WeaponMount ammoMount = ammoSlot.Mounts[ammoMountIndex];
                                if (!TryGetAmmunitionCargoCapacity(ammoMount, out float capacity)
                                    || !WeaponChecker.MountAllowedHQ(ammoMount, hq)
                                    || !WeaponChecker.MountAllowedAirbase(ammoMount, airbase))
                                {
                                    continue;
                                }

                                if (best == null
                                    || airbaseDistance < bestDistance - 1f
                                    || (Mathf.Abs(airbaseDistance - bestDistance) <= 1f
                                        && capacity > best.Value.ammoCapacity))
                                {
                                    best = (aircraft, airbase, jackSlot, jackMount, ammoSlot, ammoMount, capacity);
                                    bestDistance = airbaseDistance;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (best == null)
        {
            if (bestJack == null || bestAmmo == null)
            {
                SetStatus("No compatible Jacknife and ammunition cargo combination is available.");
                return false;
            }

            string supportSummary = SamSiteFoundationSupportPrefix + siteId;
            var jackChoice = bestJack.Value;
            Loadout jackLoadout = CreateEmptyLoadout(jackChoice.aircraft.HardpointSets.Length);
            PlaceCargoAndClearNonCargo(
                jackLoadout,
                jackChoice.aircraft.HardpointSets,
                jackChoice.slot.HardpointIndex,
                jackChoice.mount);
            SpawnCargoRun(
                jackChoice.aircraft,
                jackLoadout,
                GetCargoLabel(jackChoice.mount, string.Empty),
                jackChoice.airbase,
                useHighTerrainClearance: true,
                terrainClearanceMeters: 100f,
                useAirdrop: false,
                supportSummary,
                useOtherAirfields: false,
                target);

            var ammoChoice = bestAmmo.Value;
            Loadout ammoLoadout = CreateEmptyLoadout(ammoChoice.aircraft.HardpointSets.Length);
            PlaceCargoAndClearNonCargo(
                ammoLoadout,
                ammoChoice.aircraft.HardpointSets,
                ammoChoice.slot.HardpointIndex,
                ammoChoice.mount);
            SpawnCargoRun(
                ammoChoice.aircraft,
                ammoLoadout,
                GetCargoLabel(ammoChoice.mount, string.Empty),
                ammoChoice.airbase,
                useHighTerrainClearance: true,
                terrainClearanceMeters: 100f,
                useAirdrop: false,
                supportSummary,
                useOtherAirfields: false,
                target);
            CommanderPlugin.Log.LogInfo(
                $"SAM foundation cargo split across two landing sorties: "
                + $"Jacknife={GetCargoLabel(jackChoice.mount, string.Empty)}, "
                + $"ammo={GetCargoLabel(ammoChoice.mount, string.Empty)} ({ammoChoice.capacity:0}).");
            return true;
        }

        var choice = best.Value;
        CommanderPlugin.Log.LogInfo(
            $"SAM foundation cargo selected: aircraft={choice.aircraft.Label}, "
            + $"Jacknife={GetCargoLabel(choice.jackMount, string.Empty)}, "
            + $"ammo={GetCargoLabel(choice.ammoMount, string.Empty)} ({choice.ammoCapacity:0}).");
        Loadout loadout = CreateEmptyLoadout(choice.aircraft.HardpointSets.Length);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.aircraft.HardpointSets,
            choice.jackSlot.HardpointIndex,
            choice.jackMount);
        PlaceCargoAndClearNonCargo(
            loadout,
            choice.aircraft.HardpointSets,
            choice.ammoSlot.HardpointIndex,
            choice.ammoMount);
        SpawnCargoRun(
            choice.aircraft,
            loadout,
            "Jacknife + ammunition",
            choice.airbase,
            useHighTerrainClearance: true,
            terrainClearanceMeters: 100f,
            useAirdrop: false,
            supportSummary: SamSiteFoundationSupportPrefix + siteId,
            useOtherAirfields: false,
            target);
        return true;
    }

    private static bool TryGetAmmunitionCargoCapacity(WeaponMount mount, out float capacity)
    {
        capacity = 0f;
        if (!IsRuntimeCargoMount(mount) || mount.prefab == null)
        {
            return false;
        }

        MountedCargo[] mountedCargo = mount.prefab.GetComponentsInChildren<MountedCargo>(true);
        for (int i = 0; i < mountedCargo.Length; i++)
        {
            UnitDefinition? cargoDefinition = mountedCargo[i]?.cargo;
            Rearmer? rearmer = cargoDefinition?.unitPrefab != null
                ? cargoDefinition.unitPrefab.GetComponentInChildren<Rearmer>(true)
                : null;
            if (rearmer != null)
            {
                capacity += Mathf.Max(0f, rearmer.Capacity);
            }
        }

        return capacity > 0.01f;
    }

    private static bool IsJacknifeCargo(WeaponMount mount)
    {
        if (!TryGetMountedCargoDefinition(mount, out UnitDefinition? definition))
        {
            return false;
        }

        string identity = $"{definition!.unitName} {definition.code} {definition.jsonKey} {definition.name}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool TryGetMountedCargoDefinition(
        WeaponMount mount,
        out UnitDefinition? definition)
    {
        definition = null;
        if (!IsRuntimeCargoMount(mount) || mount.prefab == null)
        {
            return false;
        }

        MountedCargo? mountedCargo = mount.prefab.GetComponentInChildren<MountedCargo>(true);
        definition = mountedCargo?.cargo;
        return definition?.unitPrefab != null;
    }

    private static int ParseFoundationSiteId(string supportSummary)
    {
        if (string.IsNullOrWhiteSpace(supportSummary)
            || !supportSummary.StartsWith(
                SamSiteFoundationSupportPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(
            supportSummary.Substring(SamSiteFoundationSupportPrefix.Length),
            out int siteId)
            ? siteId
            : -1;
    }

    private static int ParseJacknifeSiteId(string supportSummary)
    {
        if (string.IsNullOrWhiteSpace(supportSummary)
            || !supportSummary.StartsWith(
                SamSiteJacknifeSupportPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(
            supportSummary.Substring(SamSiteJacknifeSupportPrefix.Length),
            out int siteId)
            ? siteId
            : -1;
    }

    private static int ParseCargoSiteId(string supportSummary)
    {
        if (string.IsNullOrWhiteSpace(supportSummary)
            || !supportSummary.StartsWith(
                SamSiteCargoSupportPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(
            supportSummary.Substring(SamSiteCargoSupportPrefix.Length),
            out int siteId)
            ? siteId
            : -1;
    }

    private static bool SupportSummaryMatchesSite(string supportSummary, int siteId)
    {
        return ParseFoundationSiteId(supportSummary) == siteId
            || ParseCargoSiteId(supportSummary) == siteId
            || ParseJacknifeSiteId(supportSummary) == siteId;
    }

    private static void NotifySamMissionFailed(string supportSummary)
    {
        CommanderSamSiteService.NotifySupplyMissionFailed(
            ParseFoundationSiteId(supportSummary),
            ParseCargoSiteId(supportSummary),
            ParseJacknifeSiteId(supportSummary));
    }

    private static bool RequiresProtectedSamAirbase(string supportSummary)
    {
        return string.Equals(
                supportSummary,
                SamSiteCargoSupportSummary,
                StringComparison.OrdinalIgnoreCase)
            || ParseCargoSiteId(supportSummary) >= 0
            || ParseFoundationSiteId(supportSummary) >= 0
            || ParseJacknifeSiteId(supportSummary) >= 0;
    }

    private static bool IsJacknifeUnit(Unit unit)
    {
        string identity =
            $"{unit.unitName} {unit.definition?.unitName} {unit.definition?.code} {unit.definition?.jsonKey}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || unit.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool IsSamSupplyAirbaseSafe(Airbase airbase, GlobalPosition sitePosition)
    {
        Transform sourceTransform = airbase.center != null ? airbase.center : airbase.transform;
        GlobalPosition sourcePosition = sourceTransform.GlobalPosition();
        if (CommanderSamSiteAnalyzerService.TryEvaluateLogisticsRisk(
            sourcePosition,
            sitePosition,
            null,
            out float influenceRisk,
            out _))
        {
            return influenceRisk <= 0.62f;
        }

        GlobalPosition nearestEnemyPosition = default;
        float nearestEnemyDistance = float.MaxValue;

        foreach (FactionHQ candidateHq in FactionRegistry.GetAllHQs())
        {
            if (candidateHq == null || DynamicMap.GetFactionMode(candidateHq) != FactionMode.Enemy)
            {
                continue;
            }

            foreach (Airbase enemyAirbase in candidateHq.GetAirbases())
            {
                if (enemyAirbase == null || enemyAirbase.disabled)
                {
                    continue;
                }

                Transform enemyTransform = enemyAirbase.center != null
                    ? enemyAirbase.center
                    : enemyAirbase.transform;
                GlobalPosition enemyPosition = enemyTransform.GlobalPosition();
                float sourceDistance = HorizontalSquareDistance(sourcePosition, enemyPosition);
                if (sourceDistance < nearestEnemyDistance)
                {
                    nearestEnemyDistance = sourceDistance;
                    nearestEnemyPosition = enemyPosition;
                }
            }
        }

        return nearestEnemyDistance == float.MaxValue
            || nearestEnemyDistance >= HorizontalSquareDistance(sitePosition, nearestEnemyPosition);
    }

    private static float HorizontalSquareDistance(GlobalPosition left, GlobalPosition right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return x * x + z * z;
    }

    internal bool TrySpawnAtWorldPoint(Vector2 screenPosition)
    {
        if (pendingTargetSelection == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid terrain point was found. Click visible terrain or a surface.");
            return true;
        }

        PendingTargetSelection selection = pendingTargetSelection;
        selection.Targets.Add(target);

        bool repeatSelection = CommanderSettings.RepeatDeployment.IsPressed();
        if (!repeatSelection)
        {
            pendingTargetSelection = null;
        }
        SpawnCargoRun(
            selection.Aircraft,
            selection.Loadout,
            selection.CargoLabel,
            selection.Airbase,
            selection.HighTerrainClearance,
            selection.TerrainClearanceMeters,
            selection.Airdrop,
            selection.SupportSummary,
            selection.UseOtherAirfields,
            selection.Targets);
        if (repeatSelection)
        {
            selection.Targets.Clear();
            SetStatus("Supply run queued. Hold Shift and click another destination, or release Shift for the final run.");
        }
        return true;
    }

    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    internal static bool TryOverrideTransportTarget(AIHeloTransportState state)
    {
        try
        {
            return Instance != null && Instance.OverrideTransportTarget(state);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Supply cargo target hook failed; returning this aircraft to Basegame cargo logic: {exception}");
            Instance?.EndTargetOverrideForState(state);
            return false;
        }
    }

    internal static void NotifyTransportStateLeft(AIHeloTransportState state)
    {
        Instance?.EndTargetOverrideForState(state);
    }

    internal static bool ShouldDelayCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        return Instance != null && Instance.ShouldDelayAssignedCargoTakeoff(pilot, requestedState);
    }

    internal static bool TryDeployAssignedCargo(AIHeloTransportState state)
    {
        return Instance != null && Instance.DeployNextAssignedCargo(state);
    }

    internal static bool ShouldSuppressAssignedEjection(AIHeloTransportState state)
    {
        return Instance != null && Instance.SuppressEjectionAtAssignedSamSite(state);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.HandleAircraftReturned(aircraft);
    }

    internal static bool TryOverrideAssignedReturnAirbase(AIHeloLandingState state)
    {
        return Instance != null && Instance.OverrideAssignedReturnAirbase(state);
    }

    internal static bool TryOverrideAssignedNearestAirbase(PilotBaseState state)
    {
        return Instance != null && Instance.OverrideAssignedNearestAirbase(state);
    }

    internal static void NotifyCargoActivated(Aircraft aircraft, Unit cargoUnit)
    {
        Instance?.HoldDeployedCargo(aircraft, cargoUnit);
    }

    private void BindPendingTerrainAutopilots()
    {
        if (pendingTerrainAutopilotBindings.Count == 0)
        {
            return;
        }

        List<Aircraft>? completed = null;
        foreach (Aircraft aircraft in pendingTerrainAutopilotBindings)
        {
            if (aircraft == null
                || aircraft.disabled
                || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
                || !mission.HighTerrainClearance)
            {
                completed ??= new List<Aircraft>();
                completed.Add(aircraft!);
                continue;
            }
            if (aircraft.autopilot == null)
            {
                continue;
            }

            TryBindTerrainAutopilot(aircraft, mission);
            completed ??= new List<Aircraft>();
            completed.Add(aircraft);
        }

        if (completed == null)
        {
            return;
        }
        for (int i = 0; i < completed.Count; i++)
        {
            pendingTerrainAutopilotBindings.Remove(completed[i]);
        }
    }

    private bool TryBindTerrainAutopilot(Aircraft aircraft, CargoMission mission)
    {
        Autopilot? autopilot = aircraft.autopilot;
        if (autopilot == null)
        {
            return false;
        }

        bool newlyBound = !assignedAutopilotAircraft.ContainsKey(autopilot);
        terrainClearanceAutopilots[autopilot] = mission.TerrainClearanceMeters;
        assignedAutopilotAircraft[autopilot] = aircraft;
        if (newlyBound)
        {
            CommanderPlugin.Log.LogInfo(
                $"Supply terrain profile bound: aircraft={CommanderGameAccess.GetUnitLabel(aircraft)}, "
                + $"clearance={mission.TerrainClearanceMeters:0}m, foundation={mission.FoundationSiteId >= 0}, "
                + $"steepLanding={mission.SteepLanding}, routeWaypoints={mission.ApproachRoute.Count}.");
        }
        return true;
    }

    internal static void PrepareAssignedTerrainFlight(
        Autopilot autopilot,
        ref float altitudeHold,
        bool followTerrain)
    {
        if (Instance == null
            || !Instance.terrainClearanceAutopilots.TryGetValue(autopilot, out float clearance))
        {
            return;
        }
        if (followTerrain)
        {
            altitudeHold = Mathf.Max(altitudeHold, clearance);
        }

        if (!Instance.assignedAutopilotAircraft.TryGetValue(autopilot, out Aircraft aircraft)
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive
            || mission.FoundationSiteId < 0)
        {
            return;
        }
        altitudeHold = Mathf.Max(altitudeHold, mission.SteepLanding ? 100f : 60f);
    }

    internal static void ForceAssignedVerticalTakeoff(SwivelDuctSystem swivelDuct)
    {
        if (Instance == null
            || SwivelAircraftField?.GetValue(swivelDuct) is not Aircraft aircraft
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.VerticalDepartureActive)
        {
            return;
        }

        if (aircraft.radarAlt >= 35f)
        {
            mission.VerticalDepartureActive = false;
            return;
        }

        aircraft.GetInputs().customAxis1 = 0f;
    }

    private static bool CanHostSpawn(out FactionHQ? hq, out string error)
    {
        hq = null;
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            error = "Supply aircraft can only be spawned by the host.";
            return false;
        }

        hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            error = "No local faction HQ is available.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void RefreshAirbaseOptions()
    {
        Airbase? previousSelection = selectedAirbase;
        airbaseOptions.Clear();

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (hq == null || aircraft == null)
        {
            selectedAirbase = null;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition))
            {
                continue;
            }

            Transform positionTransform = airbase.center != null ? airbase.center : airbase.transform;
            float distance = Vector3.Distance(cameraPosition, positionTransform.position);
            bool ready = IsAvailableAirbase(airbase, hq, aircraft.Definition);
            airbaseOptions.Add(new AirbaseOption(airbase, GetAirbaseLabel(airbase), distance, ready));
        }

        airbaseOptions.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        selectedAirbase = null;
        for (int i = 0; i < airbaseOptions.Count; i++)
        {
            if (ReferenceEquals(airbaseOptions[i].Airbase, previousSelection))
            {
                selectedAirbase = previousSelection;
                break;
            }
        }

        if (selectedAirbase == null && airbaseOptions.Count > 0)
        {
            selectedAirbase = airbaseOptions[0].Airbase;
        }
    }

    private static bool IsAvailableAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        return airbase != null
            && !airbase.disabled
            && airbase.CurrentHQ == hq
            && airbase.CanSpawnAircraft(definition);
    }

    private static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || airbase.CurrentHQ != hq)
        {
            return false;
        }

        List<AircraftDefinition> availableAircraft = airbase.GetAvailableAircraft();
        return availableAircraft != null && availableAircraft.Contains(definition);
    }

    private static string GetAirbaseLabel(Airbase airbase)
    {
        if (airbase.SavedAirbase != null)
        {
            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.DisplayName))
            {
                return airbase.SavedAirbase.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.UniqueName))
            {
                return airbase.SavedAirbase.UniqueName;
            }
        }

        return airbase.name;
    }

    private static Loadout CloneLoadout(Loadout source)
    {
        return new Loadout
        {
            weapons = source.weapons != null ? new List<WeaponMount>(source.weapons) : new List<WeaponMount>()
        };
    }

    private static void EnsureLoadoutLength(Loadout loadout, int count)
    {
        while (loadout.weapons.Count < count)
        {
            loadout.weapons.Add(null!);
        }

        if (loadout.weapons.Count > count)
        {
            loadout.weapons.RemoveRange(count, loadout.weapons.Count - count);
        }
    }

    private int CountCargoSlots()
    {
        int count = 0;
        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            count += aircraftOptions[i].CargoSlots.Count;
        }

        return count;
    }

    private void CancelTargetSelection(bool showStatus)
    {
        if (pendingTargetSelection == null)
        {
            return;
        }

        pendingTargetSelection = null;
        if (showStatus)
        {
            SetStatus("Cargo run target selection cancelled.");
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }


}
