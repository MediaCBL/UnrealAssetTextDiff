using UnrealBuildTool;

public class UatdTextDiff : ModuleRules
{
	public UatdTextDiff(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		PrivateDependencyModuleNames.AddRange(new[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"UnrealEd",     // UDiffAssetsCommandlet
		});
	}
}
