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
    [HarmonyDebug]
    public static class MindStateTickPatch
    {
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
            .Advance(1)
            .RemoveInstructions(14) // Remove the original body
            .InsertAndAdvance( // AnyCloseHostilesRecently = Pawn_MindState.duty.def.alwaysShowWeapon
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_MindState), "duty")),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Brtrue), // -> 52
                new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Br), // -> 54
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PawnDuty), "def"))
            )
            .Advance(-1) // I don't know why, but the label drops one instruction forward here.
            .CreateLabel(out var label52)
            .Advance(1)
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DutyDef), "alwaysShowWeapon"))
            )
            .CreateLabel(out var label54)
            .MatchStartBackwards(new CodeMatch(OpCodes.Br)) // Add our labels now that they exist
            .SetOperandAndAdvance(label54)
            .MatchStartBackwards(new CodeMatch(OpCodes.Brtrue)) // Same thing again
            .SetOperandAndAdvance(label52)
            .InstructionEnumeration();
        }
    }
}