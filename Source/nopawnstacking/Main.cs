using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace nopawnstacking;
[StaticConstructorOnStartup]
public static class Main
{
    static Main()
    {
        new Harmony("mute.nopawnstacking").PatchAll(Assembly.GetExecutingAssembly());
    }
}

public static class HarmonyPatches
{
    [HarmonyPatch(typeof(Pawn_MindState), "MindStateTick")]
    public static class MindStateTickPatch
    {
        private static bool IsRealCombatant(Pawn_MindState mindState)
        {
            return mindState.duty?.def.alwaysShowWeapon is true ||
                   mindState.mentalStateHandler.CurStateDef?.IsAggro is true;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            return new CodeMatcher(instructions, generator).Start().MatchEndForward(
                new CodeMatch(i => i.opcode == OpCodes.Brfalse_S),
                new CodeMatch(i => i.opcode == OpCodes.Ldarg_0),
                new CodeMatch(i => i.opcode == OpCodes.Ldfld),
                new CodeMatch(i => i.opcode == OpCodes.Callvirt),
                new CodeMatch(i => i.opcode == OpCodes.Brfalse_S),
                new CodeMatch(i => i.opcode == OpCodes.Ldarg_0)
            )
            .Advance(1) // Skip past the ldarg0
            .RemoveInstructions(14) // Remove the original body
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MindStateTickPatch), "IsRealCombatant"))
            )
            .InstructionEnumeration();
        }
    }
}