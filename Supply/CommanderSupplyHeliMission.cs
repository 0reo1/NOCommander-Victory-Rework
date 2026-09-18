using System;
using System.Collections;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed partial class CommanderSupplyHeliService
{
    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        IReadOnlyList<GlobalPosition> targets)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return;
        }

        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("The selected airbase no longer supports this aircraft.");
            return;
        }

        QueuedCargoSpawn request = new(
            aircraftOption,
            CloneLoadout(cargoLoadout),
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            targets);
        Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq!);
        if (spawnAirbase == null
            || pendingAircraftSpawn != null
            || IsSamHelipadBusy(request))
        {
            queuedCargoSpawns.Enqueue(request);
            SetStatus(useOtherAirfields
                ? $"Supply run queued. Waiting for a compatible friendly airfield ({queuedCargoSpawns.Count} queued)."
                : $"Supply run queued for {GetAirbaseLabel(airbase)} ({queuedCargoSpawns.Count} queued).");
            return;
        }

        TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq!);
    }

    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        GlobalPosition target)
    {
        SpawnCargoRun(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            new[] { target });
    }

    private void TryProcessQueuedCargoSpawns()
    {
        if (pendingAircraftSpawn != null || queuedCargoSpawns.Count == 0)
        {
            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out _))
        {
            return;
        }

        int attempts = queuedCargoSpawns.Count;
        while (attempts-- > 0)
        {
            QueuedCargoSpawn request = queuedCargoSpawns.Dequeue();
            if (!IsCompatibleAirbase(request.RequestedAirbase, hq!, request.Aircraft.Definition)
                && !request.UseOtherAirfields)
            {
                SetStatus("A queued supply run was cancelled because its airbase is no longer friendly or compatible.");
                continue;
            }

            if (IsSamHelipadBusy(request))
            {
                queuedCargoSpawns.Enqueue(request);
                continue;
            }

            Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq!);
            if (spawnAirbase == null)
            {
                queuedCargoSpawns.Enqueue(request);
                continue;
            }

            TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq!);
            return;
        }
    }

    private bool IsSamHelipadBusy(QueuedCargoSpawn request)
    {
        int siteId = GetSamSiteId(request.SupportSummary);
        if (siteId < 0)
        {
            return false;
        }

        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && entry.Value.TargetOverrideActive
                && GetSamSiteId(entry.Value) == siteId)
            {
                return true;
            }
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return false;
        }

        Vector3 target = request.Target.ToLocalPosition();
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not Aircraft aircraft
                || aircraft.disabled)
            {
                continue;
            }

            Vector3 position = aircraft.transform.position;
            if (CommanderGameAccess.HorizontalDistance(position, target) <= 60f
                && Mathf.Abs(position.y - target.y) <= 80f)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetSamSiteId(string supportSummary)
    {
        int siteId = ParseFoundationSiteId(supportSummary);
        if (siteId < 0)
        {
            siteId = ParseCargoSiteId(supportSummary);
        }
        if (siteId < 0)
        {
            siteId = ParseJacknifeSiteId(supportSummary);
        }
        return siteId;
    }

    private static int GetSamSiteId(CargoMission mission)
    {
        if (mission.FoundationSiteId >= 0)
        {
            return mission.FoundationSiteId;
        }
        if (mission.DepositSiteId >= 0)
        {
            return mission.DepositSiteId;
        }
        return mission.JacknifeSiteId;
    }

    private static Airbase? ResolveSpawnAirbase(QueuedCargoSpawn request, FactionHQ hq)
    {
        bool protectedSamRun = RequiresProtectedSamAirbase(request.SupportSummary);
        if (IsAvailableAirbase(request.RequestedAirbase, hq, request.Aircraft.Definition)
            && (!protectedSamRun
                || !request.UseOtherAirfields
                || IsSamSupplyAirbaseSafe(request.RequestedAirbase, request.Target)))
        {
            return request.RequestedAirbase;
        }

        if (!request.UseOtherAirfields)
        {
            return null;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        Airbase? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (!IsAvailableAirbase(candidate, hq, request.Aircraft.Definition))
            {
                continue;
            }
            if (protectedSamRun && !IsSamSupplyAirbaseSafe(candidate, request.Target))
            {
                continue;
            }

            Transform positionTransform = candidate.center != null ? candidate.center : candidate.transform;
            float distance = Vector3.SqrMagnitude(positionTransform.position - cameraPosition);
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static void IssueSupplyReturnToBase(Aircraft? aircraft, CargoMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return;
        }

        CloseCargoDoors(mission);
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            pilot.AILandingState ??= new AIPilotLandingState();
            pilot.SwitchState(pilot.AILandingState);
            return;
        }
    }

    private void TrySpawnCargoRunAtAirbase(QueuedCargoSpawn request, Airbase airbase, FactionHQ hq)
    {
        CargoAircraftOption aircraftOption = request.Aircraft;

        bool purchased = false;
        if (hq.GetUnitSupply(aircraftOption.Definition) <= 0)
        {
            float cost = aircraftOption.Definition.value;
            if (hq.factionFunds < cost)
            {
                SetStatus("The faction cannot afford this supply aircraft.");
                return;
            }

            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(aircraftOption.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            aircraftOption.Definition,
            airbase,
            request.CargoLabel,
            request.Target,
            request.HighTerrainClearance,
            request.TerrainClearanceMeters,
            request.Airdrop,
            request.SupportSummary,
            purchased,
            purchased ? aircraftOption.Definition.value : 0f,
            request.Targets,
            Time.unscaledTime + PendingSpawnTimeoutSeconds);

        AircraftDefinition definition = aircraftOption.Definition;
        int liveryIndex = definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = CloneLoadout(request.Loadout);
        Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(
            null,
            definition,
            new LiveryKey(liveryIndex),
            loadout,
            definition.aircraftParameters.DefaultFuelLevel);

        if (!result.Allowed)
        {
            NotifySamMissionFailed(request.SupportSummary);
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(definition, -1);
                hq.AddFunds(definition.value);
            }

            SetStatus("The airbase rejected the supply aircraft spawn.");
            return;
        }

        string purchaseLabel = purchased ? " Purchased from faction funds." : string.Empty;
        SetStatus($"Spawned {aircraftOption.Label} with {request.CargoLabel}.{purchaseLabel}");
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Definition))
        {
            return;
        }

        // Aircraft like the Chimera carry their own complete transport AI and fly, drop and land
        // without any of NOCommander's helicopter-only steering hooks. We still record a mission
        // for them so the commander's chosen LZ is known and can be enforced (see
        // CommanderSupplyChimeraDirector), but we skip every helo-specific steering path below.
        bool selfFlying = CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(aircraft);

        CargoMission mission = new(
            pending.Hq,
            pending.Target,
            pending.CargoLabel,
            pending.HighTerrainClearance,
            pending.TerrainClearanceMeters,
            pending.Airdrop,
            pending.PurchasedWithFunds,
            pending.PurchaseCost,
            CountDeployableCargo(aircraft),
            pending.Targets,
            pending.SupportSummary == SamSiteCargoSupportSummary
                || ParseCargoSiteId(pending.SupportSummary) >= 0,
            ParseCargoSiteId(pending.SupportSummary),
            ParseFoundationSiteId(pending.SupportSummary),
            ParseJacknifeSiteId(pending.SupportSummary),
            pending.OriginAirbase,
            pending.NavalTarget);
        assignedMissions[aircraft] = mission;
        int cachedRouteSiteId = mission.DepositSiteId >= 0
            ? mission.DepositSiteId
            : mission.JacknifeSiteId;
        if (cachedRouteSiteId >= 0
            && CommanderSamSiteService.TryCopyCachedSupplyRoute(
                cachedRouteSiteId,
                pending.OriginAirbase,
                mission.ApproachRoute,
                out bool cachedSteepLanding))
        {
            mission.RoutePlanned = true;
            mission.SteepLanding = cachedSteepLanding;
        }
        else if (mission.FoundationSiteId >= 0)
        {
            mission.RoutePlanned = CommanderTerrainFlightPlanner.TryBuildRoute(
                aircraft.GlobalPosition(),
                mission.Target,
                Mathf.Max(60f, mission.TerrainClearanceMeters),
                mission.ApproachRoute,
                out bool steepLanding);
            mission.SteepLanding = steepLanding;
        }
        string missionLabel = pending.NavalTarget != null
            ? "Naval Supply"
            : pending.Airdrop
                ? $"Airdrop: {pending.CargoLabel}"
                : $"Cargo Delivery: {pending.CargoLabel}";
        CommanderSelectionService.PinMissionUnit(aircraft, "SUPPLY", missionLabel);
        if (selfFlying)
        {
            // Terrain-clearance autopilot binding only drives AutopilotHelo/AutopilotTiltwing,
            // and this aircraft handles its own terrain following. Bias its role toward transport
            // so a mid-flight role re-check doesn't turn the supply run into a gunship sortie.
            CommanderSupplyChimeraDirector.OnMissionAssigned(aircraft);
        }
        else if (pending.HighTerrainClearance && !TryBindTerrainAutopilot(aircraft, mission))
        {
            pendingTerrainAutopilotBindings.Add(aircraft);
        }
        pendingAircraftSpawn = null;
    }

    private bool OverrideTransportTarget(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        if (Mathf.Approximately(mission.LastTransportOverrideFixedTime, Time.fixedTime))
        {
            return true;
        }
        mission.LastTransportOverrideFixedTime = Time.fixedTime;

        float lastCheck = LastLandingSpotCheckField?.GetValue(state) is float value ? value : 0f;
        bool routeNeedsUpdate = mission.RouteTransitActive
            || mission.ApproachRouteIndex < mission.ApproachRoute.Count;
        if (Time.timeSinceLevelLoad - lastCheck < 3f && !routeNeedsUpdate)
        {
            return true;
        }

        if (mission.NavalTarget != null)
        {
            return OverrideNavalSupplyTarget(state, aircraft, mission);
        }

        if (!TrySelectCargoStation(aircraft))
        {
            if (PilotField?.GetValue(state) is Pilot unloadingPilot
                && unloadingPilot.flightInfo.LastCargoDelivery > 0f)
            {
                LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
                TimeWithoutMissionField?.SetValue(state, 0f);
                state.stateDisplayName = "Unloading cargo";
                return true;
            }

            CommanderPlugin.Log.LogWarning($"Assigned supply aircraft has no active cargo station: {CommanderGameAccess.GetUnitLabel(aircraft)}");
            return false;
        }

        LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
        TimeWithoutMissionField?.SetValue(state, 0f);
        TransportModeField?.SetValue(state, AIHeloTransportState.TransportMode.LandSuppy);
        state.stateDisplayName = $"Delivering {mission.CargoLabel}";

        if (PilotField?.GetValue(state) is Pilot pilot)
        {
            pilot.flightInfo.EnemyContact = true;
        }

        object? destination = TransportDestinationField?.GetValue(state);
        if (destination == null)
        {
            return false;
        }

        bool intermediateRouteTarget = TryGetApproachRouteTarget(aircraft, mission, out GlobalPosition assignedTarget);
        mission.RouteTransitActive = intermediateRouteTarget;
        AirdropField?.SetValue(state, mission.Airdrop);
        if (intermediateRouteTarget)
        {
            if (aircraft.autopilot is not AutopilotTiltwing)
            {
                assignedTarget = GetTurnAnticipationTarget(aircraft, mission, assignedTarget);
                GlobalPosition aircraftPosition = aircraft.GlobalPosition();
                Vector3 routeDirection = assignedTarget - aircraftPosition;
                routeDirection.y = 0f;
                if (routeDirection.sqrMagnitude > 1f)
                {
                    routeDirection.Normalize();
                    assignedTarget = new GlobalPosition(
                        aircraftPosition.x + routeDirection.x * 10000f,
                        assignedTarget.y,
                        aircraftPosition.z + routeDirection.z * 10000f);
                }
            }
        }
        else
        {
            assignedTarget = mission.Target;
        }
        bool foundationLandingSearch = !intermediateRouteTarget && mission.FoundationSiteId >= 0;
        if (foundationLandingSearch)
        {
            if (!mission.FoundationLandingSearchInitialized)
            {
                Vector3 approach = aircraft.GlobalPosition() - mission.Target;
                approach.y = 0f;
                if (approach.sqrMagnitude < 1f)
                {
                    approach = -aircraft.transform.forward;
                    approach.y = 0f;
                }
                approach.Normalize();
                assignedTarget = mission.Target + approach * 120f;
                DestinationLzField?.SetValue(destination, assignedTarget);
                DestinationTouchdownField?.SetValue(destination, assignedTarget);
                DestinationSlopeField?.SetValue(destination, 90f);
                DestinationAttemptsField?.SetValue(destination, 0);
                mission.FoundationLandingSearchInitialized = true;
            }
            UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 300f, aircraft });
            if (DestinationTouchdownField?.GetValue(destination) is GlobalPosition foundationTouchdown)
            {
                assignedTarget = foundationTouchdown;
            }
        }
        bool elevatedPlatformApproach = !intermediateRouteTarget
            && mission.FoundationSiteId < 0
            && (mission.DepositSiteId >= 0 || mission.JacknifeSiteId >= 0);
        if (elevatedPlatformApproach && !mission.PlatformArrivalInitialized)
        {
            Vector3 awayFromPlatform = aircraft.GlobalPosition() - mission.Target;
            awayFromPlatform.y = 0f;
            if (awayFromPlatform.sqrMagnitude < 1f)
            {
                awayFromPlatform = -aircraft.transform.forward;
                awayFromPlatform.y = 0f;
            }
            awayFromPlatform.Normalize();
            float arrivalDistance = Mathf.Max(150f, aircraft.maxRadius * 5f);
            const float arrivalClearance = 20f;
            mission.PlatformArrivalPoint = new GlobalPosition(
                mission.Target.x + awayFromPlatform.x * arrivalDistance,
                mission.Target.y + arrivalClearance,
                mission.Target.z + awayFromPlatform.z * arrivalDistance);
            mission.PlatformArrivalInitialized = true;
        }
        if (elevatedPlatformApproach && !mission.PlatformArrivalReached)
        {
            float arrivalDistance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                mission.PlatformArrivalPoint.ToLocalPosition());
            float heightAboveDeck = aircraft.GlobalPosition().y - mission.Target.y;
            if (arrivalDistance <= 50f && heightAboveDeck >= 10f && aircraft.speed <= 25f)
            {
                mission.PlatformArrivalReached = true;
            }
            else
            {
                assignedTarget = mission.PlatformArrivalPoint;
            }
        }
        if (elevatedPlatformApproach
            && mission.PlatformArrivalReached
            && !mission.PlatformApproachComplete)
        {
            const float deckClearance = 20f;
            float horizontalDistance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                mission.Target.ToLocalPosition());
            float heightAboveDeck = aircraft.GlobalPosition().y - mission.Target.y;
            if (horizontalDistance <= 30f
                && heightAboveDeck >= deckClearance * 0.6f
                && aircraft.speed <= 20f)
            {
                mission.PlatformApproachComplete = true;
            }
            else
            {
                assignedTarget = new GlobalPosition(
                    mission.Target.x,
                    mission.Target.y + deckClearance,
                    mission.Target.z);
            }
        }

        DestinationValidMissionField?.SetValue(destination, true);
        DestinationDropConditionsField?.SetValue(destination, false);
        DestinationEnemyPositionField?.SetValue(destination, mission.Target);
        if (!foundationLandingSearch)
        {
            DestinationLzField?.SetValue(destination, assignedTarget);
        }

        if (!mission.Initialized)
        {
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 90f);
            DestinationAttemptsField?.SetValue(destination, 0);
            mission.Initialized = true;
        }

        if (intermediateRouteTarget || elevatedPlatformApproach)
        {
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 0f);
        }
        else if (!foundationLandingSearch)
        {
            UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 150f, aircraft });
        }
        TransportDestinationField?.SetValue(state, destination);
        StateDestinationField?.SetValue(state, assignedTarget);
        return true;
    }

    private void EndTargetOverrideForState(AIHeloTransportState state)
    {
        if (AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && mission.TargetOverrideActive)
        {
            mission.TargetOverrideActive = false;
        }
    }

    private bool ShouldDelayAssignedCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        // Every guard below exists to shepherd a vanilla cargo helicopter through its
        // AIHeloTakeoffState/AIHeloTransportState sequence. A fixed-wing aircraft's pilot has no
        // helo states at all, so those properties are null - and a freshly spawned, parked
        // aircraft's currentState is null too. Without this exit, "currentState == AIHeloTakeoffState"
        // is null == null, the switch gets cancelled, and a self-flying aircraft can never enter
        // any state: it just sits on the ramp. Never block state changes for those aircraft.
        if (pilot.aircraft != null
            && CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(pilot.aircraft))
        {
            return false;
        }

        if (pilot.aircraft != null
            && assignedMissions.TryGetValue(pilot.aircraft, out CargoMission assignedMission)
            && assignedMission.Cancelled)
        {
            return false;
        }

        if (pilot.aircraft != null
            && assignedMissions.ContainsKey(pilot.aircraft)
            && pilot.AIHeloTakeoffState != null
            && pilot.currentState == pilot.AIHeloTakeoffState
            && requestedState != pilot.currentState
            && pilot.aircraft.radarAlt < 30f)
        {
            return true;
        }

        if (requestedState == pilot.currentState
            || pilot.AIHeloTransportState == null
            || pilot.currentState != pilot.AIHeloTransportState
            || pilot.aircraft == null
            || !assignedMissions.TryGetValue(pilot.aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        // Do not block the initial departure from the airfield. After the first
        // release, keep an airdrop in transport state until every cargo station
        // has fired; otherwise Basegame leaves the state after only one load.
        if (mission.Airdrop)
        {
            return mission.ReleasedCargoCount > 0 && HasDeployableCargo(pilot.aircraft);
        }
        return mission.CargoClearancePending
            || (mission.LastCargoReleasedAt > 0f
                && Time.timeSinceLevelLoad - mission.LastCargoReleasedAt < 8f);
    }

    private void HoldDeployedCargo(Aircraft aircraft, Unit cargoUnit)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return;
        }

        if (mission.Cancelled)
        {
            DestroyCargoUnit(cargoUnit);
            return;
        }

        mission.ActivatedCargoCount++;
        if (IsSamLogisticsMission(mission)
            && !IsJacknifeUnit(cargoUnit))
        {
            float supply = GetCargoSupply(cargoUnit);
            if (mission.FoundationSiteId >= 0)
            {
                CommanderSamSiteService.NotifyFoundationAmmunitionDelivered(
                    mission.FoundationSiteId,
                    supply);
            }
            else if (mission.DepositSiteId >= 0)
            {
                CommanderSamSiteService.TryDepositAmmunitionAmount(
                    mission.DepositSiteId,
                    supply,
                    out _);
            }

            DestroyCargoUnit(cargoUnit);
            UpdateDeliveryCompleted(aircraft, mission);
            return;
        }

        if (mission.DepositAtSamCore && pendingSamCargoDeposits.Add(cargoUnit))
        {
            CommanderPlugin.Instance?.StartCoroutine(
                DepositSamCargoWhenStationary(cargoUnit, mission.DepositSiteId));
        }
        bool cargoStillOnAircraft = HasDeployableCargo(aircraft);
        if (cargoUnit is GroundVehicle groundVehicle)
        {
            if ((mission.FoundationSiteId >= 0 || mission.JacknifeSiteId >= 0)
                && IsJacknifeUnit(groundVehicle)
                && !mission.Airdrop)
            {
                int siteId = mission.FoundationSiteId >= 0
                    ? mission.FoundationSiteId
                    : mission.JacknifeSiteId;
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(
                    groundVehicle.GlobalPosition(),
                    playerCommand: false);
                CommanderSamSiteService.ReserveDeliveredJacknife(siteId, groundVehicle);
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(
                    ReleaseSamJacknifeAfterUnloadDelay(
                        aircraft,
                        groundVehicle,
                        mission));
                return;
            }

            if (cargoStillOnAircraft && !mission.Airdrop)
            {
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(ClearGroundVehicleFromRamp(aircraft, groundVehicle, mission));
            }
            else
            {
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(groundVehicle.GlobalPosition(), playerCommand: false);
                NotifyFoundationCargoActivated(mission, cargoUnit);
            }
        }
        else if (cargoStillOnAircraft
            && !mission.Airdrop
            && !mission.CargoClearancePending)
        {
            mission.CargoClearancePending = true;
            CommanderPlugin.Instance?.StartCoroutine(ClearStaticCargoFromRamp(aircraft, cargoUnit, mission));
        }
        else
        {
            NotifyFoundationCargoActivated(mission, cargoUnit);
        }
        UpdateDeliveryCompleted(aircraft, mission);
    }

    private static IEnumerator ReleaseSamJacknifeAfterUnloadDelay(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float releaseAt = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < releaseAt)
        {
            vehicle.SetHoldPosition(true);
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle == null || vehicle.disabled)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        NotifyFoundationCargoActivated(mission, vehicle);

        float clearance = aircraft != null
            ? Mathf.Max(12f, aircraft.maxRadius + vehicle.maxRadius + 2f)
            : 12f;
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (aircraft != null
            && vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                vehicle.transform.position) < clearance)
        {
            yield return new WaitForSeconds(0.25f);
        }

        mission.CargoClearancePending = false;
    }

    private static void NotifyFoundationCargoActivated(CargoMission mission, Unit? cargo)
    {
        if (cargo == null || cargo.disabled)
        {
            return;
        }

        if (mission.FoundationSiteId >= 0)
        {
            CommanderSamSiteService.NotifyFoundationCargoActivated(
                mission.FoundationSiteId,
                cargo);
        }
        else if (mission.JacknifeSiteId >= 0)
        {
            CommanderSamSiteService.NotifySiteJacknifeActivated(
                mission.JacknifeSiteId,
                cargo);
        }
    }

    private IEnumerator DepositSamCargoWhenStationary(Unit cargo, int siteId)
    {
        float removeAt = Time.timeSinceLevelLoad + 45f;
        yield return new WaitForSeconds(3f);
        float stableSince = -1f;
        float timeout = Time.timeSinceLevelLoad + 60f;
        while (cargo != null && !cargo.disabled && Time.timeSinceLevelLoad < timeout)
        {
            Rigidbody? body = cargo.rb;
            bool stationary = body == null
                || (body.velocity.sqrMagnitude < 0.25f && body.angularVelocity.sqrMagnitude < 0.25f);
            if (stationary)
            {
                if (stableSince < 0f)
                {
                    stableSince = Time.timeSinceLevelLoad;
                }
                else if (Time.timeSinceLevelLoad - stableSince >= 2f)
                {
                    break;
                }
            }
            else
            {
                stableSince = -1f;
            }

            yield return new WaitForSeconds(0.25f);
        }

        pendingSamCargoDeposits.Remove(cargo!);
        if (cargo == null || cargo.disabled)
        {
            yield break;
        }
        if (!CommanderSamSiteService.TryDepositAmmunition(
                siteId,
                cargo,
                out float transferred))
        {
            DestroyCargoUnit(cargo);
            yield break;
        }

        CommanderPlugin.Log.LogInfo(
            $"SAM cargo deposited: cargo={CommanderGameAccess.GetUnitLabel(cargo)}, ammunition={transferred:0.0}.");
        while (cargo != null && !cargo.disabled && Time.timeSinceLevelLoad < removeAt)
        {
            yield return new WaitForSeconds(0.5f);
        }
        if (cargo != null
            && NetworkManagerNuclearOption.i?.ServerObjectManager != null
            && cargo.Identity != null)
        {
            NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(
                cargo.Identity,
                !cargo.Identity.IsSceneObject);
        }
    }

    private bool DeployNextAssignedCargo(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return false;
        }

        if (mission.Cancelled)
        {
            return true;
        }

        if (mission.RouteTransitActive)
        {
            return true;
        }

        HoldLandingTimerWhileCargoProcesses(state, aircraft, mission);

        if (!mission.Airdrop && IsSamLogisticsMission(mission))
        {
            bool unloading = HasDeployableCargo(aircraft)
                || mission.ReleasedCargoCount < mission.ExpectedCargoLoads
                || mission.ReleasedCargoCount > mission.ActivatedCargoCount
                || mission.CargoClearancePending;
            if (unloading)
            {
                OpenCargoDoors(aircraft, mission);
                if (mission.LandingUnloadAt <= 0f)
                {
                    mission.LandingUnloadAt = Time.timeSinceLevelLoad + 30f;
                }
                if (Time.timeSinceLevelLoad < mission.LandingUnloadAt)
                {
                    TouchedDownTimeField?.SetValue(state, 0f);
                    return true;
                }
            }
            else if (!CloseCargoDoors(mission))
            {
                TouchedDownTimeField?.SetValue(state, 0f);
                return true;
            }
        }

        if (Time.timeSinceLevelLoad < mission.NextCargoReleaseAt)
        {
            return true;
        }

        if (!mission.Airdrop
            && (mission.ReleasedCargoCount > mission.ActivatedCargoCount || mission.CargoClearancePending))
        {
            return true;
        }

        if (!TrySelectNextCargoWeapon(aircraft, out WeaponStation station, out Weapon cargoWeapon))
        {
            if (!mission.Airdrop && IsSamLogisticsMission(mission))
            {
                mission.DeliveryCompleted = mission.ActivatedCargoCount >= mission.ExpectedCargoLoads;
                mission.VerticalDepartureActive = true;
            }
            return true;
        }

        Pilot? pilot = PilotField?.GetValue(state) as Pilot;
        if (pilot == null)
        {
            return false;
        }

        if (!mission.Airdrop
            && IsSamLogisticsMission(mission)
            && cargoWeapon is MountedCargo mountedCargo
            && !IsJacknifeCargoDefinition(mountedCargo.cargo)
            && TryConsumeVirtualSamCargo(aircraft, station, mountedCargo, mission, pilot))
        {
            return true;
        }

        aircraft.weaponManager.currentWeaponStation = station;
        cargoWeapon.Fire(aircraft, null!, aircraft.rb.velocity, station, default);
        station.UpdateLastFired(1);
        mission.ReleasedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + (mission.Airdrop ? 1.5f : 2.5f);
        return true;
    }

    private static void HoldLandingTimerWhileCargoProcesses(
        AIHeloTransportState state,
        Aircraft aircraft,
        CargoMission mission)
    {
        if (mission.Airdrop)
        {
            return;
        }

        bool waitingForReleasedCargo = mission.ActivatedCargoCount < mission.ReleasedCargoCount;
        bool moreCargoAtThisLz = HasDeployableCargo(aircraft)
            || mission.ReleasedCargoCount < mission.ExpectedCargoLoads;
        if (waitingForReleasedCargo || moreCargoAtThisLz || mission.CargoClearancePending)
        {
            TouchedDownTimeField?.SetValue(state, 0f);
        }
    }

    private static IEnumerator ClearGroundVehicleFromRamp(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (vehicle != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }
        if (vehicle == null || aircraft == null
            || !TryFindCargoClearanceDirection(aircraft, vehicle, 10f, out Vector3 direction))
        {
            if (vehicle != null)
            {
                vehicle.SetHoldPosition(true);
                NotifyFoundationCargoActivated(mission, vehicle);
            }
            mission.CargoClearancePending = false;
            yield break;
        }

        GlobalPosition clearPoint = (vehicle.transform.position + direction * 6f).ToGlobalPosition();
        vehicle.SetHoldPosition(false);
        vehicle.UnitCommand?.SetDestination(clearPoint, playerCommand: true);
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(vehicle.transform.position, clearPoint.ToLocalPosition()) > 1.25f)
        {
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle != null && !vehicle.disabled)
        {
            vehicle.SetHoldPosition(true);
            vehicle.UnitCommand?.SetDestination(vehicle.GlobalPosition(), playerCommand: false);
            NotifyFoundationCargoActivated(mission, vehicle);
        }
        mission.CargoClearancePending = false;
    }

    private static IEnumerator ClearStaticCargoFromRamp(Aircraft aircraft, Unit cargo, CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (cargo != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }

        if (cargo == null || aircraft == null)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        if (!TryFindCargoClearanceDirection(aircraft, cargo, 10f, out Vector3 direction))
        {
            CommanderPlugin.Log.LogWarning(
                $"Supply cargo could not find 10m ramp clearance: {CommanderGameAccess.GetUnitLabel(cargo)}");
            NotifyFoundationCargoActivated(mission, cargo);
            mission.CargoClearancePending = false;
            yield break;
        }

        Vector3 start = cargo.transform.position;
        Vector3 target = start + direction.normalized * 6f;
        const float moveDuration = 5f;
        float startedAt = Time.timeSinceLevelLoad;
        while (cargo != null && Time.timeSinceLevelLoad - startedAt < moveDuration)
        {
            float t = Mathf.SmoothStep(0f, 1f, (Time.timeSinceLevelLoad - startedAt) / moveDuration);
            Vector3 position = Vector3.Lerp(start, target, t);
            if (cargo.rb != null)
            {
                cargo.rb.MovePosition(position);
                cargo.rb.velocity = Vector3.zero;
                cargo.rb.angularVelocity = Vector3.zero;
            }
            else
            {
                cargo.transform.position = position;
            }
            yield return new WaitForFixedUpdate();
        }
        NotifyFoundationCargoActivated(mission, cargo);
        mission.CargoClearancePending = false;
    }

    private static bool TryFindCargoClearanceDirection(
        Aircraft aircraft,
        Unit cargo,
        float scanDistance,
        out Vector3 direction)
    {
        Vector3 rear = -aircraft.transform.forward;
        rear.y = 0f;
        rear = rear.sqrMagnitude > 0.01f ? rear.normalized : Vector3.back;
        Vector3[] candidates =
        {
            Quaternion.AngleAxis(-70f, Vector3.up) * rear,
            Quaternion.AngleAxis(70f, Vector3.up) * rear,
            rear,
        };
        Vector3 origin = cargo.transform.position + Vector3.up * Mathf.Max(cargo.maxRadius * 0.25f, 0.5f);
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 candidate = candidates[i].normalized;
            if (!Physics.Raycast(origin, candidate, scanDistance, 2112, QueryTriggerInteraction.Ignore))
            {
                direction = candidate;
                return true;
            }
        }

        direction = Vector3.zero;
        return false;
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.PurchasedWithFunds
            || mission.PurchaseRefunded
            || mission.Hq == null)
        {
            return;
        }

        mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
        mission.Hq.AddFunds(mission.PurchaseCost);
        mission.PurchaseRefunded = true;
    }

    private bool SuppressEjectionAtAssignedSamSite(AIHeloTransportState state)
    {
        return AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && IsSamLogisticsMission(mission)
            && !mission.Airdrop
            && !mission.RouteTransitActive
            && aircraft.radarAlt < 15f
            && FastMath.InRange(aircraft.GlobalPosition(), mission.Target, 500f);
    }

    private bool OverrideAssignedReturnAirbase(AIHeloLandingState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || mission.OriginAirbase == null
            || mission.OriginAirbase.disabled
            || mission.OriginAirbase.CurrentHQ != mission.Hq)
        {
            return false;
        }

        RunwayQuery query = new()
        {
            RunwayType = RunwayQueryType.Vertical,
            MinSize = aircraft.maxRadius
        };
        if (!mission.OriginAirbase.TryRequestVerticalLanding(
            aircraft,
            query,
            out Airbase.VerticalLandingPoint landingPoint))
        {
            return false;
        }

        StateNearestAirbaseField?.SetValue(state, mission.OriginAirbase);
        LandingStatePointField?.SetValue(state, landingPoint);
        LandingStateReachedApproachField?.SetValue(state, false);
        StateDestinationField?.SetValue(state, landingPoint.GetApproachPoint(aircraft));
        return true;
    }

    private static bool TryGetApproachRouteTarget(
        Aircraft aircraft,
        CargoMission mission,
        out GlobalPosition target)
    {
        const float waypointReachDistance = 400f;
        target = default;
        while (mission.ApproachRouteIndex < mission.ApproachRoute.Count)
        {
            GlobalPosition waypoint = mission.ApproachRoute[mission.ApproachRouteIndex];
            Vector3 previous = GetPreviousRoutePoint(mission).ToLocalPosition();
            Vector3 waypointPosition = waypoint.ToLocalPosition();
            if (CommanderGameAccess.HorizontalDistance(aircraft.transform.position, waypointPosition) > waypointReachDistance
                && !HasPassedRoutePoint(aircraft.transform.position, previous, waypointPosition))
            {
                break;
            }
            mission.ApproachRouteIndex++;
        }
        if (mission.ApproachRouteIndex < mission.ApproachRoute.Count)
        {
            target = mission.ApproachRoute[mission.ApproachRouteIndex];
            return true;
        }
        return false;
    }

    private static GlobalPosition GetTurnAnticipationTarget(
        Aircraft aircraft,
        CargoMission mission,
        GlobalPosition currentWaypoint)
    {
        int nextIndex = mission.ApproachRouteIndex + 1;
        if (nextIndex >= mission.ApproachRoute.Count)
        {
            return currentWaypoint;
        }

        float distance = CommanderGameAccess.HorizontalDistance(
            aircraft.transform.position,
            currentWaypoint.ToLocalPosition());
        float turnLead = Mathf.Clamp(aircraft.speed * 8f, 500f, 1600f);
        if (distance >= turnLead)
        {
            return currentWaypoint;
        }

        float blend = Mathf.InverseLerp(turnLead, 0f, distance) * 0.75f;
        GlobalPosition nextWaypoint = mission.ApproachRoute[nextIndex];
        return new GlobalPosition(
            Mathf.Lerp((float)currentWaypoint.x, (float)nextWaypoint.x, blend),
            Mathf.Lerp((float)currentWaypoint.y, (float)nextWaypoint.y, blend),
            Mathf.Lerp((float)currentWaypoint.z, (float)nextWaypoint.z, blend));
    }

    private static GlobalPosition GetPreviousRoutePoint(CargoMission mission)
    {
        if (mission.ApproachRouteIndex > 0)
        {
            return mission.ApproachRoute[mission.ApproachRouteIndex - 1];
        }

        Transform origin = mission.OriginAirbase.center != null
            ? mission.OriginAirbase.center
            : mission.OriginAirbase.transform;
        return origin.GlobalPosition();
    }

    private static bool HasPassedRoutePoint(Vector3 aircraft, Vector3 previous, Vector3 waypoint)
    {
        Vector3 segment = waypoint - previous;
        Vector3 beyondWaypoint = aircraft - waypoint;
        segment.y = 0f;
        beyondWaypoint.y = 0f;
        return segment.sqrMagnitude > 1f && Vector3.Dot(segment, beyondWaypoint) >= 0f;
    }

    private static bool TryConsumeVirtualSamCargo(
        Aircraft aircraft,
        WeaponStation station,
        MountedCargo cargo,
        CargoMission mission,
        Pilot pilot)
    {
        if (MountedCargoRemoveMethod == null)
        {
            return false;
        }

        float supply = GetCargoSupply(cargo.cargo);
        if (supply <= 0f)
        {
            return false;
        }

        if (mission.FoundationSiteId >= 0)
        {
            CommanderSamSiteService.NotifyFoundationAmmunitionDelivered(
                mission.FoundationSiteId,
                supply);
        }
        else if (mission.DepositSiteId >= 0)
        {
            CommanderSamSiteService.TryDepositAmmunitionAmount(
                mission.DepositSiteId,
                supply,
                out _);
        }

        aircraft.weaponManager.currentWeaponStation = station;
        station.LaunchMount(aircraft, null!, default);
        MountedCargoRemoveMethod.Invoke(cargo, null);
        UnityEngine.Object.Destroy(cargo.gameObject);
        mission.ReleasedCargoCount++;
        mission.ActivatedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + 1f;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        UpdateDeliveryCompleted(aircraft, mission);
        return true;
    }

    private static bool IsJacknifeCargoDefinition(UnitDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        string identity = $"{definition.unitName} {definition.code} {definition.jsonKey}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || definition.unitPrefab?.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool IsSamLogisticsMission(CargoMission mission)
    {
        return mission.FoundationSiteId >= 0
            || mission.DepositSiteId >= 0
            || mission.JacknifeSiteId >= 0;
    }

    private static void OpenCargoDoors(Aircraft aircraft, CargoMission mission)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation station = aircraft.weaponStations[stationIndex];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = station.Weapons[weaponIndex];
                if (weapon != null && WeaponHardpointField?.GetValue(weapon) is Hardpoint hardpoint)
                {
                    hardpoint.SpringOpenBayDoors();
                    for (int doorIndex = 0; doorIndex < hardpoint.bayDoors.Length; doorIndex++)
                    {
                        BayDoor door = hardpoint.bayDoors[doorIndex];
                        if (door != null && !mission.CargoDoors.Contains(door))
                        {
                            mission.CargoDoors.Add(door);
                        }
                    }
                }
            }
        }
    }

    private static bool CloseCargoDoors(CargoMission mission)
    {
        bool closed = true;
        for (int i = mission.CargoDoors.Count - 1; i >= 0; i--)
        {
            BayDoor door = mission.CargoDoors[i];
            if (door == null)
            {
                mission.CargoDoors.RemoveAt(i);
                continue;
            }

            BayDoorOpenTimerField?.SetValue(door, 0f);
            door.enabled = true;
            float openAmount = BayDoorOpenAmountField?.GetValue(door) is float value ? value : 0f;
            closed &= openAmount <= 0.01f;
        }
        return closed;
    }

    private static void UpdateDeliveryCompleted(Aircraft aircraft, CargoMission mission)
    {
        if (mission.ActivatedCargoCount >= mission.ExpectedCargoLoads
            && !HasDeployableCargo(aircraft)
            && !mission.CargoClearancePending)
        {
            mission.DeliveryCompleted = true;
        }
    }

    private static float GetCargoSupply(Unit cargo)
    {
        float supply = 0f;
        Rearmer[] rearmers = cargo.GetComponentsInChildren<Rearmer>(true);
        for (int i = 0; i < rearmers.Length; i++)
        {
            supply += Mathf.Max(0f, rearmers[i].Capacity);
        }
        return supply;
    }

    private static float GetCargoSupply(UnitDefinition? definition)
    {
        float supply = 0f;
        Rearmer[] rearmers = definition?.unitPrefab != null
            ? definition.unitPrefab.GetComponentsInChildren<Rearmer>(true)
            : Array.Empty<Rearmer>();
        for (int i = 0; i < rearmers.Length; i++)
        {
            supply += Mathf.Max(0f, rearmers[i].Capacity);
        }
        return supply;
    }

    private static void DestroyCargoUnit(Unit cargo)
    {
        if (cargo.Identity != null && NetworkManagerNuclearOption.i?.ServerObjectManager != null)
        {
            NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(
                cargo.Identity,
                !cargo.Identity.IsSceneObject);
        }
    }

    private bool OverrideAssignedNearestAirbase(PilotBaseState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(aircraft)
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || mission.OriginAirbase == null
            || mission.OriginAirbase.disabled
            || mission.OriginAirbase.CurrentHQ != mission.Hq)
        {
            return false;
        }

        StateNearestAirbaseField?.SetValue(state, mission.OriginAirbase);
        return true;
    }

    /// <summary>
    /// Looks up the commander mission for a self-flying supply aircraft. Used by the Chimera
    /// director's Harmony patches, which run inside the aircraft's own AI tick.
    /// </summary>
    internal static bool TryGetSelfFlyingMission(
        Aircraft aircraft,
        out GlobalPosition target,
        out Unit? navalTarget,
        out FactionHQ? hq)
    {
        target = default;
        navalTarget = null;
        hq = null;

        if (Instance == null
            || aircraft == null
            || !Instance.assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return false;
        }

        target = mission.Target;
        navalTarget = mission.NavalTarget;
        hq = mission.Hq;
        return true;
    }

    /// <summary>
    /// Keeps self-flying supply aircraft (Chimera and friends) aimed at the LZ the commander
    /// picked. Their own AI re-runs its mission search on a timer and will otherwise re-target
    /// itself mid-flight, which looks like the aircraft taking off and then wandering off to
    /// service something else.
    /// </summary>
    private void DirectSelfFlyingSupplyMissions()
    {
        if (assignedMissions.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            Aircraft aircraft = entry.Key;
            if (aircraft == null || aircraft.disabled)
            {
                continue;
            }

            if (!CommanderSupplyFixedWingSupport.HasSelfFlyingSupplyController(aircraft))
            {
                continue;
            }

            CargoMission mission = entry.Value;
            CommanderSupplyChimeraDirector.EnforceDropPoint(
                aircraft,
                mission.Target,
                mission.NavalTarget,
                mission.Hq);
        }
    }

    private void PruneFinishedMissions()
    {
        if (assignedMissions.Count == 0)
        {
            return;
        }

        List<Aircraft>? stale = null;
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled)
            {
                continue;
            }

            stale ??= new List<Aircraft>();
            stale.Add(entry.Key!);
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            Aircraft aircraft = stale[i];
            if (ReferenceEquals(aircraft, null))
            {
                continue;
            }

            if (aircraft.autopilot != null)
            {
                terrainClearanceAutopilots.Remove(aircraft.autopilot);
                assignedAutopilotAircraft.Remove(aircraft.autopilot);
            }
            pendingTerrainAutopilotBindings.Remove(aircraft);
            CommanderSupplyChimeraDirector.Forget(aircraft);

            if (assignedMissions.TryGetValue(aircraft, out CargoMission failedMission)
                && !failedMission.Cancelled
                && !failedMission.DeliveryCompleted)
            {
                CommanderSamSiteService.NotifySupplyMissionFailed(
                    failedMission.FoundationSiteId,
                    failedMission.DepositSiteId,
                    failedMission.JacknifeSiteId);
            }
            assignedMissions.Remove(aircraft);
        }
    }

    private static bool TrySelectCargoStation(Aircraft aircraft)
    {
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station != null && station.Cargo && station.WeaponInfo != null && station.WeaponInfo.cargo && HasDeployableCargo(station))
            {
                aircraft.weaponManager.currentWeaponStation = station;
                return true;
            }
        }

        return false;
    }

    private static bool TrySelectNextCargoWeapon(
        Aircraft aircraft,
        out WeaponStation station,
        out Weapon cargoWeapon)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation candidate = aircraft.weaponStations[stationIndex];
            if (candidate == null || !candidate.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < candidate.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = candidate.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    station = candidate;
                    cargoWeapon = weapon;
                    return true;
                }
            }
        }

        station = null!;
        cargoWeapon = null!;
        return false;
    }

    private static bool HasDeployableCargo(Aircraft aircraft)
    {
        return CountDeployableCargo(aircraft) > 0;
    }

    private static int CountDeployableCargo(Aircraft aircraft)
    {
        int count = 0;
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon? weapon = station.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    count += weapon.GetAmmoLoaded();
                }
            }
        }

        return count;
    }

    private static bool HasDeployableCargo(WeaponStation station)
    {
        for (int i = 0; i < station.Weapons.Count; i++)
        {
            if (station.Weapons[i] != null && station.Weapons[i].GetAmmoLoaded() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class PendingTargetSelection
    {
        internal PendingTargetSelection(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase airbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            Airbase = airbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
        }

        internal CargoAircraftOption Aircraft { get; }
        internal Loadout Loadout { get; }
        internal string CargoLabel { get; }
        internal Airbase Airbase { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool UseOtherAirfields { get; }
        internal List<GlobalPosition> Targets { get; } = new();

        internal string GetTargetPrompt()
        {
            string targetType = Airdrop ? "airdrop point" : "landing point";
            return $"Click a {targetType} in the 3D world. The game's Cancel binding cancels.";
        }
    }

    private sealed class QueuedCargoSpawn
    {
        internal QueuedCargoSpawn(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase requestedAirbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields,
            IReadOnlyList<GlobalPosition> targets)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            RequestedAirbase = requestedAirbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
            Targets = new List<GlobalPosition>(targets);
        }

        internal CargoAircraftOption Aircraft { get; }
        internal Loadout Loadout { get; }
        internal string CargoLabel { get; }
        internal Airbase RequestedAirbase { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool UseOtherAirfields { get; }
        internal List<GlobalPosition> Targets { get; }
        internal GlobalPosition Target => Targets[0];
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(
            FactionHQ hq,
            AircraftDefinition definition,
            Airbase originAirbase,
            string cargoLabel,
            GlobalPosition target,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool purchasedWithFunds,
            float purchaseCost,
            IReadOnlyList<GlobalPosition> targets,
            float expiresAt,
            Ship? navalTarget = null)
        {
            Hq = hq;
            Definition = definition;
            OriginAirbase = originAirbase;
            CargoLabel = cargoLabel;
            Target = target;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            Targets = new List<GlobalPosition>(targets);
            ExpiresAt = expiresAt;
            NavalTarget = navalTarget;
        }

        internal FactionHQ Hq { get; }
        internal AircraftDefinition Definition { get; }
        internal Airbase OriginAirbase { get; }
        internal string CargoLabel { get; }
        internal GlobalPosition Target { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal List<GlobalPosition> Targets { get; }
        internal float ExpiresAt { get; }
        internal Ship? NavalTarget { get; }
    }

    private sealed class CargoMission
    {
        internal CargoMission(
            FactionHQ hq,
            GlobalPosition target,
            string cargoLabel,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            bool purchasedWithFunds,
            float purchaseCost,
            int expectedCargoLoads,
            IReadOnlyList<GlobalPosition> deliveryTargets,
            bool depositAtSamCore,
            int depositSiteId,
            int foundationSiteId,
            int jacknifeSiteId,
            Airbase originAirbase,
            Ship? navalTarget = null)
        {
            Hq = hq;
            DeliveryTargets = new List<GlobalPosition>(deliveryTargets);
            CargoLabel = cargoLabel;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpectedCargoLoads = expectedCargoLoads;
            DepositAtSamCore = depositAtSamCore;
            DepositSiteId = depositSiteId;
            FoundationSiteId = foundationSiteId;
            JacknifeSiteId = jacknifeSiteId;
            OriginAirbase = originAirbase;
            NavalTarget = navalTarget;
        }

        internal FactionHQ Hq { get; }
        internal GlobalPosition Target => DeliveryTargets[0];
        internal List<GlobalPosition> DeliveryTargets { get; }
        internal string CargoLabel { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal int ExpectedCargoLoads { get; }
        internal bool DepositAtSamCore { get; }
        internal int DepositSiteId { get; }
        internal int FoundationSiteId { get; }
        internal int JacknifeSiteId { get; }
        internal Airbase OriginAirbase { get; }
        internal Ship? NavalTarget { get; }
        internal bool PurchaseRefunded { get; set; }
        internal bool Initialized { get; set; }
        internal bool TargetOverrideActive { get; set; } = true;
        internal int ActivatedCargoCount { get; set; }
        internal int ReleasedCargoCount { get; set; }
        internal float NextCargoReleaseAt { get; set; }
        internal float LastCargoReleasedAt { get; set; }
        internal bool CargoClearancePending { get; set; }
        internal bool Cancelled { get; set; }
        internal float LandingUnloadAt { get; set; }
        internal bool VerticalDepartureActive { get; set; }
        internal bool FoundationLandingSearchInitialized { get; set; }
        internal bool PlatformArrivalInitialized { get; set; }
        internal bool PlatformArrivalReached { get; set; }
        internal GlobalPosition PlatformArrivalPoint { get; set; }
        internal bool PlatformApproachComplete { get; set; }
        internal bool RoutePlanned { get; set; }
        internal bool SteepLanding { get; set; }
        internal bool RouteTransitActive { get; set; }
        internal float LastTransportOverrideFixedTime { get; set; } = float.NegativeInfinity;
        internal int ApproachRouteIndex { get; set; }
        internal readonly List<GlobalPosition> ApproachRoute = new();
        internal bool DeliveryCompleted { get; set; }
        internal readonly List<BayDoor> CargoDoors = new();
    }}
