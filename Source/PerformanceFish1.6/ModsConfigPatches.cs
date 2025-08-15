// Copyright (c) 2023 bradson
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.


using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PerformanceFish.Prepatching;

namespace PerformanceFish;

public sealed class ModsConfigPatches : ClassWithFishPrepatches
{
#if V1_5
	public sealed class AreAllActivePatch : FishPrepatch
	{
		public override string? Description { get; }
			= "Fixes a MayRequire bug causing it to normally not recognize steam versions of mods if another local "
			+ "copy exists.";

		public override MethodBase TargetMethodBase { get; } = methodof(ModsConfig.AreAllActive);

		public override void Transpiler(ILProcessor ilProcessor, ModuleDefinition module)
			=> IsActiveFix(ilProcessor, module);
	}
#endif
	public sealed class IsAnyActiveOrEmptyPatch : FishPrepatch
	{
		public override string? Description { get; }
			= "Fixes a MayRequireAnyOf bug causing it to normally not recognize steam versions of mods if another "
			+ "local copy exists, as well as its inability to strip whitespace.";

		public override MethodBase TargetMethodBase { get; } = methodof(ModsConfig.IsAnyActiveOrEmpty);

		public override void Transpiler(ILProcessor ilProcessor, ModuleDefinition module)
			=> IsActiveFix(ilProcessor, module);

		public static void Prefix(out bool trimNames) => trimNames = true;
	}
	
	private static void IsActiveFix(ILProcessor ilProcessor, ModuleDefinition module)
	{
		var instructions = ilProcessor.instructions;
		var success = false;

		for (var i = 0; i + 1 < instructions.Count; i++)
		{
			if (instructions[i].Operand is MethodReference { Name: nameof(ModsConfig.IsActive) })
			{
				instructions[i].Operand = module.ImportReference(IsActiveReplacement);
				success = true;
			}
		}
			
		if (!success)
		{
			Log.Error($"Performance Fish failed to apply its patch on '{
				ilProcessor.GetMethod().FullName}'. This should be harmless as it's meant to be just a bugfix.");
		}
	}

	public static bool IsActiveReplacement(string id) => ModLister.GetActiveModWithIdentifier(id, true) != null;
#if V1_6
	public sealed class AreAllActivePatchNew : FishPrepatch
	{
		public override string? Description { get; }
		= "Fixes a MayRequireAnyOf bug causing it to normally not recognize steam versions of mods if another "
		+ "local copy exists, as well as its inability to strip whitespace. Because of ModsConfig.AreAllActive's"
		+ "new overload, this new method was written targeting the `relevant` one likely causing the issues.";
		public override MethodBase TargetMethodBase { get; } =
			AccessTools.Method(typeof(ModsConfig), nameof(ModsConfig.AreAllActive),
				new[] { typeof(IEnumerable<string>) }) ??
			AccessTools.Method(typeof(ModsConfig), nameof(ModsConfig.AreAllActive),
				new[] { typeof(string) });

		public override void Transpiler(ILProcessor il, ModuleDefinition module)
		{
			var body = il.Body;
			var instructions = body.Instructions;

			// determine original target: bool ModConfig.AreAllActive(string id)
			var isActive = AccessTools.Method(typeof(ModsConfig), nameof(ModsConfig.IsActive),
				new[] { typeof(string) });

			// patch active -> return
			if (isActive == null) return;

			var replacement = AccessTools.Method(typeof(ModsConfigPatches), nameof(ModsConfigPatches.IsActiveReplacement),
				new[] { typeof(string) });

			if (replacement == null) return;
			var isActiveRef = module.ImportReference(isActive);
			var replacementRef = module.ImportReference(replacement);

			foreach (var instruction in instructions)
			{
				if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
				{
					if (instruction.Operand is MethodReference mr && mr.FullName == isActiveRef.FullName) // robust match  
					{
						instruction.Operand = replacementRef;
					} 
				}
			}
		}
	}

}
#endif
