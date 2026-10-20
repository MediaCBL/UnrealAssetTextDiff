#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "UatdDiffAssetsCommandlet.generated.h"

/**
 * UnrealAssetTextDiff commandlet. Reimplements the built-in DiffAssets
 * pipeline (copy inputs into Saved/Diff, load with LOAD_ForDiff, T3D text
 * export, detached DiffCmd launch) using only public APIs — the stock
 * UDiffAssetsCommandlet class is not exported from UnrealEd — and adds a
 * supplemental dump of each loaded package's Blueprint generated-class
 * default object to <DiffDir>/<stem>.defaults.t3d.
 *
 * Class-default coverage the stock commandlet lacks: UBlueprint assets
 * never appear in the object export as property dumps, so variable
 * defaults are invisible without this file.
 */
UCLASS()
class UUatdAssetDiffCommandlet : public UCommandlet
{
	GENERATED_BODY()

public:
	virtual int32 Main(const FString& Params) override;

private:
	static bool CopyFileToDiffDir(FString& InOutFilename);
	static bool LoadFile(const FString& Filename, TArray<UObject*>& LoadedObjects, UPackage*& OutPackage);
	static bool ExportFile(const FString& Filename, const TArray<UObject*>& LoadedObjects);
	static bool LaunchDiffTool(const FString& DiffCommand, const FString& TextFilename1, const FString& TextFilename2);
	static void DumpClassDefaults(class UPackage* Package);
};
