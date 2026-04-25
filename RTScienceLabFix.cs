// RTScienceLabFix v7
//
// ══════════════════════════════════════════════════════════════════════════════
// Root cause chain (confirmed via IL analysis + log evidence):
//
//  1. ModuleScienceLab sets ScienceData.triggered = true on lab data.
//     RemoteTech sees triggered=true and skips RnDCommsStream creation entirely.
//     Fix: Transpiler on <Transmit>d__18.MoveNext replaces brtrue with pop.
//
//  2. Lab data has subjectID = "" (no experiment subject).
//     With commstream now created, submitStreamData → SubmitScienceData →
//     CollectDeployedScience.OnScience → subject.id.Substring(start, len < 0)
//     → ArgumentOutOfRangeException.  Science never awarded.
//     Fix: Prefix on submitStreamData intercepts empty-subject data,
//          awards via R&D.AddScience, and deducts lab buffer directly.
//
//  3. OnTransmissionComplete is invoked through EventData.Fire() (delegate),
//     causing Harmony's __instance injection to fail — NullReferenceException
//     fires in the Harmony wrapper before any Prefix body runs.
//     Patching this method as a Prefix is therefore not viable.
//     Fix: buffer deduction moved into submitStreamData Prefix (Patch 2).
//
//  4. updateModuleUI throws NullReferenceException on transmitScienceEvent.guiActive.
//     Fix: Finalizer suppresses it.
//
// ══════════════════════════════════════════════════════════════════════════════
// Patches (3 total):
//
//  1. [v3] Transpiler on <Transmit>d__18.MoveNext
//     Removes "if triggered → skip commstream" branch.
//
//  2. [v7] Prefix on RnDCommsStream.submitStreamData
//     Detects lab data by empty subject.id.
//     - Awards science via R&D.AddScience (bypasses crashing SubmitScienceData).
//     - Deducts awarded amount from the lab's storedScience buffer.
//     - Posts screen message for player feedback.
//
//  3. [v3] Finalizer on ModuleScienceLab.updateModuleUI
//     Silences residual NullReferenceException from transmitScienceEvent.guiActive.
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RemoteTech.Modules;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

// ─── Entry point ─────────────────────────────────────────────────────────────

[KSPAddon(KSPAddon.Startup.MainMenu, once: true)]
public class RTScienceLabFixLoader : MonoBehaviour
{
    void Start()
    {
        try
        {
            var harmony = new Harmony("com.dyllskie.rtsciencefixpatch");
            harmony.PatchAll();
            Debug.Log("[RTScienceLabFix] Patches applied.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[RTScienceLabFix] Failed to apply patches: " + ex);
        }
    }
}

// ─── Patch 1: force commstream creation for lab data (v3 — unchanged) ────────

[HarmonyPatch]
static class Patch_RT_Transmit_CommStream
{
    static MethodBase TargetMethod()
    {
        var stateType = typeof(ModuleRTDataTransmitter)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(t => t.Name.StartsWith("<Transmit>"));
        if (stateType == null)
        {
            Debug.LogError("[RTScienceLabFix] Cannot find <Transmit> state machine type.");
            return null;
        }
        var method = stateType.GetMethod("MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null)
            Debug.LogError("[RTScienceLabFix] Cannot find MoveNext on <Transmit> state machine.");
        return method;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var stateType = typeof(ModuleRTDataTransmitter)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .First(t => t.Name.StartsWith("<Transmit>"));

        var scienceDataField = AccessTools.Field(stateType, "<scienceData>5__4");
        var triggeredField   = AccessTools.Field(typeof(ScienceData), "triggered");

        if (scienceDataField == null || triggeredField == null)
        {
            Debug.LogError("[RTScienceLabFix] Transpiler: required fields not found — aborting.");
            foreach (var ci in instructions) yield return ci;
            yield break;
        }

        bool patched = false;
        var list = instructions.ToList();
        for (int i = 0; i < list.Count - 2; i++)
        {
            if (list[i].opcode   == OpCodes.Ldfld && list[i].operand   is FieldInfo f0 && f0 == scienceDataField &&
                list[i+1].opcode == OpCodes.Ldfld && list[i+1].operand is FieldInfo f1 && f1 == triggeredField  &&
                (list[i+2].opcode == OpCodes.Brtrue_S || list[i+2].opcode == OpCodes.Brtrue))
            {
                list[i+2] = new CodeInstruction(OpCodes.Pop);
                patched = true;
                Debug.Log("[RTScienceLabFix] Transpiler: removed triggered-data commstream skip.");
                break;
            }
        }
        if (!patched)
            Debug.LogWarning("[RTScienceLabFix] Transpiler: triggered-data check not found — lab science may still fail.");
        foreach (var ci in list) yield return ci;
    }
}

// ─── Patch 2: award lab science + deduct buffer (v7) ─────────────────────────
//
// submitStreamData → SubmitScienceData → CollectDeployedScience.OnScience
// crashes when subject.id is "" (lab data has no real experiment subject).
//
// For lab data: award science directly via R&D.AddScience, then find the
// transmitting vessel's ModuleScienceLab and deduct storedScience directly.
// OnTransmissionComplete cannot be patched (Harmony __instance injection fails
// when the method is invoked through EventData.Fire()).

[HarmonyPatch(typeof(RnDCommsStream), "submitStreamData")]
static class Patch_RnDCommsStream_submitStreamData
{
    static bool Prefix(RnDCommsStream __instance, ProtoVessel source)
    {
        var subject = Traverse.Create(__instance).Field("subject").GetValue<ScienceSubject>();

        // Normal experiment data — let the original handle it.
        if (subject != null && !string.IsNullOrEmpty(subject.id))
            return true;

        // Lab data: subject.id is "" (fallback subject created by RemoteTech).
        float dataIn  = Traverse.Create(__instance).Field<float>("dataIn").Value;
        float dataOut = Traverse.Create(__instance).Field<float>("dataOut").Value;
        float science = dataIn - dataOut;

        if (science > 0f)
        {
            // Award science.
            if (ResearchAndDevelopment.Instance != null)
            {
                ResearchAndDevelopment.Instance.AddScience(science, TransactionReasons.ScienceTransmission);
                Debug.Log("[RTScienceLabFix] Lab science awarded: " + science.ToString("F2") +
                    " via R&D.AddScience.");
            }
            else
            {
                Debug.LogWarning("[RTScienceLabFix] R&D instance is null — cannot award lab science.");
            }

            // Mark data as consumed in the stream.
            Traverse.Create(__instance).Field<float>("dataOut").Value = dataOut + science;

            // Deduct from the lab's stored science buffer.
            // Use source.vesselRef (the transmitting vessel) for accuracy.
            Vessel vessel = (source != null) ? source.vesselRef : null;
            if (vessel == null)
                vessel = FlightGlobals.ActiveVessel;

            bool deducted = false;
            if (vessel != null)
            {
                foreach (Part p in vessel.Parts)
                {
                    ModuleScienceLab lab = p.FindModuleImplementing<ModuleScienceLab>();
                    if (lab != null && lab.storedScience > 0f)
                    {
                        float before = lab.storedScience;
                        lab.storedScience = Mathf.Max(0f, before - science);
                        Debug.Log("[RTScienceLabFix] Lab buffer: " + before.ToString("F2") +
                            " -> " + lab.storedScience.ToString("F2") +
                            " (deducted " + science.ToString("F2") + ").");
                        deducted = true;
                        break;
                    }
                }
                if (!deducted)
                    Debug.LogWarning("[RTScienceLabFix] No lab with stored science found on vessel '" +
                        vessel.vesselName + "' — buffer not deducted.");
            }
            else
            {
                Debug.LogWarning("[RTScienceLabFix] Could not resolve transmitting vessel — buffer not deducted.");
            }

            // Post screen message.
            ScreenMessages.PostScreenMessage(
                "Science Transmitted: " + science.ToString("F1") + " data sent to KSC.",
                4f, ScreenMessageStyle.UPPER_CENTER);
        }

        return false; // Skip original — crashes on empty subject.id.
    }
}

// ─── Patch 3: silence updateModuleUI crash (v3 — unchanged) ──────────────────

[HarmonyPatch(typeof(ModuleScienceLab), "updateModuleUI")]
static class Patch_ModuleScienceLab_updateModuleUI
{
    static Exception Finalizer(Exception __exception)
    {
        if (__exception is NullReferenceException)
        {
            Debug.LogWarning("[RTScienceLabFix] Suppressed NullReferenceException in updateModuleUI " +
                "(transmitScienceEvent is null in RT coroutine context).");
            return null;
        }
        return __exception;
    }
}
