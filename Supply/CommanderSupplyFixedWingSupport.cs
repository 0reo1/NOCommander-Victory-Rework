using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace NuclearOptionCommander;

/// <summary>
/// Soft-dependency support for fixed-wing "supply plane" mods (e.g. Aryx's MC-260 Chimera)
/// that ship their own complete AI flight controller (takeoff, transit, drop run, egress,
/// landing) instead of relying on the vanilla helicopter transport AI.
///
/// NOCommander's normal Air Supply delivery pipeline is built entirely on top of the vanilla
/// helicopter/tiltwing AI states (AIHeloTransportState, AIHeloLandingState, AutopilotHelo,
/// AutopilotTiltwing, SwivelDuctSystem). A fixed-wing aircraft's Pilot never enters any of
/// those states, so none of that steering code applies to it - which is exactly what we want
/// for an aircraft that already flies and delivers cargo entirely on its own. We only need to
/// recognize such aircraft so they can be listed and spawned from the Air Supply menu; once
/// spawned, their own AI controller takes over immediately without any help from NOCommander.
///
/// Detection is done purely by component type name, resolved at runtime against whatever
/// assemblies happen to be loaded. This intentionally avoids adding a compile-time reference
/// to any specific aircraft mod's assembly, so NOCommander keeps working normally whether or
/// not that mod is installed.
/// </summary>
internal static class CommanderSupplyFixedWingSupport
{
    // Full type names of known "self-flying" fixed-wing supply/transport AI controllers.
    // Add additional entries here to recognize other mods with the same self-contained
    // transport AI pattern.
    private static readonly string[] SelfFlyingControllerTypeNames =
    {
        "Aryx_MC260_Chimera.AryxChimeraAIController",
    };

    private static readonly Type?[] cachedControllerTypes = new Type?[SelfFlyingControllerTypeNames.Length];

    /// <summary>
    /// True if the aircraft carries its own recognized self-flying transport AI controller
    /// (e.g. the Chimera's AryxChimeraAIController), meaning it manages its own takeoff,
    /// navigation, cargo delivery, and landing without needing NOCommander's helicopter-style
    /// steering hooks.
    /// </summary>
    internal static bool HasSelfFlyingSupplyController(Aircraft aircraft)
    {
        if (aircraft == null)
        {
            return false;
        }

        Type?[] controllerTypes = GetControllerTypes();
        for (int i = 0; i < controllerTypes.Length; i++)
        {
            Type? controllerType = controllerTypes[i];
            if (controllerType != null && aircraft.GetComponent(controllerType) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same check against a prefab/definition's unitPrefab (used during catalog discovery,
    /// before any instance of the aircraft has been spawned).
    /// </summary>
    internal static bool HasSelfFlyingSupplyController(GameObject? unitPrefab)
    {
        if (unitPrefab == null)
        {
            return false;
        }

        Type?[] controllerTypes = GetControllerTypes();
        for (int i = 0; i < controllerTypes.Length; i++)
        {
            Type? controllerType = controllerTypes[i];
            if (controllerType != null && unitPrefab.GetComponent(controllerType) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static Type?[] GetControllerTypes()
    {
        // Resolve lazily and keep retrying any entry that hasn't been found yet, in case the
        // mod that declares it loads after the first lookup. Once a type is found it never
        // changes, so a resolved entry is cached permanently and never looked up again.
        for (int i = 0; i < cachedControllerTypes.Length; i++)
        {
            if (cachedControllerTypes[i] == null)
            {
                cachedControllerTypes[i] = ResolveType(SelfFlyingControllerTypeNames[i]);
            }
        }

        return cachedControllerTypes;
    }

    private static Type? ResolveType(string fullName)
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
            catch (Exception ex) when (ex is ReflectionTypeLoadException || ex is FileNotFoundException)
            {
                // The mod that declares this type isn't fully loaded/available; treat it as
                // absent rather than letting discovery fail for everything else.
            }
        }

        return null;
    }
}
