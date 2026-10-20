#include "UatdDiffAssetsCommandlet.h"

#include "Engine/BlueprintGeneratedClass.h"
#include "Exporters/Exporter.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "UObject/UObjectIterator.h"
#include "UnrealExporter.h"

DEFINE_LOG_CATEGORY_STATIC(LogUatdDiffAssets, Log, All);

// ---------------------------------------------------------------------------
// Pipeline — mirrors UDiffAssetsCommandlet (see its cpp for reference behavior).
// ---------------------------------------------------------------------------

int32 UUatdAssetDiffCommandlet::Main(const FString& InParams)
{
	FString Params = InParams.Replace(TEXT("\\\""), TEXT("\""));
	UE_LOG(LogUatdDiffAssets, Log, TEXT("Params: %s"), *Params);

	TArray<FString> Tokens, Switches;
	ParseCommandLine(*Params, Tokens, Switches);

	TArray<FString> Positional;
	FString DiffCmd;
	for (FString& Token : Tokens)
	{
		FString Key, Value;
		if (Token.Split(TEXT("="), &Key, &Value))
		{
			if (DiffCmd.IsEmpty() && Key.Equals(TEXT("DiffCmd"), ESearchCase::IgnoreCase))
			{
				DiffCmd = Value;
			}
		}
		else
		{
			Positional.Add(MoveTemp(Token));
		}
	}

	if (DiffCmd.IsEmpty() || Positional.Num() < 2 ||
		!DiffCmd.Contains(TEXT("{1}")) || !DiffCmd.Contains(TEXT("{2}")))
	{
		UE_LOG(LogUatdDiffAssets, Warning,
			TEXT("Usage: UatdAssetDiff File1.uasset File2.uasset DiffCmd=\"tool.exe {1} {2}\""));
		return 1;
	}

	FString Filename1 = Positional[0];
	FString Filename2 = Positional[1];
	if (!CopyFileToDiffDir(Filename1) || !CopyFileToDiffDir(Filename2))
	{
		return 1;
	}

	const FString TextFilename1 = Filename1 + TEXT(".t3d");
	const FString TextFilename2 = Filename2 + TEXT(".t3d");
	bool bOk = true;

	for (int32 Side = 0; Side < 2; ++Side)
	{
		const FString& InFile = Side == 0 ? Filename1 : Filename2;
		const FString& OutFile = Side == 0 ? TextFilename1 : TextFilename2;

		TArray<UObject*> Objects;
		UPackage* Package = nullptr;
		if (!LoadFile(InFile, Objects, Package))
		{
			bOk = false;
			continue;
		}
		if (!ExportFile(OutFile, Objects))
		{
			bOk = false;
			continue;
		}
		// The class-default dump is supplemental: it logs its own outcome and
		// writes a marker file, but never flips an otherwise-working export to
		// failure. Absence of the file means the dump could not be produced.
		DumpClassDefaults(Package);
	}

	if (!LaunchDiffTool(DiffCmd, TextFilename1, TextFilename2))
	{
		return 1;
	}
	return bOk ? 0 : 1;
}

bool UUatdAssetDiffCommandlet::CopyFileToDiffDir(FString& InOutFilename)
{
	const FString InFilename = InOutFilename;
	FString OutBaseFilename = FPaths::GetBaseFilename(InFilename);
	const TCHAR* InvalidCharacters = INVALID_LONGPACKAGE_CHARACTERS;
	for (; *InvalidCharacters; ++InvalidCharacters)
	{
		const TCHAR InvalidStr[] = { *InvalidCharacters, '\0' };
		OutBaseFilename.ReplaceInline(InvalidStr, TEXT("_"));
	}
	const FString OutFilename = FPaths::DiffDir() / OutBaseFilename +
		FPaths::GetExtension(InFilename, true);
	if (IFileManager::Get().Copy(*OutFilename, *InFilename, true, true))
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("Failed to copy %s to %s."), *InFilename, *OutFilename);
		return false;
	}
	InOutFilename = OutFilename;
	return true;
}

bool UUatdAssetDiffCommandlet::LoadFile(const FString& Filename, TArray<UObject*>& LoadedObjects,
	UPackage*& OutPackage)
{
	UPackage* Package = LoadPackage(nullptr, *Filename, LOAD_ForDiff);
	OutPackage = Package;
	if (Package == nullptr)
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("Could not load %s"), *Filename);
		return false;
	}
	for (TObjectIterator<UObject> It; It; ++It)
	{
		if (It->GetOuter() == Package)
		{
			LoadedObjects.Add(*It);
		}
	}
	if (!LoadedObjects.Num())
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("Loaded %s, but it didn't contain any objects."), *Filename);
		return false;
	}
	LoadedObjects.Sort();
	return true;
}

bool UUatdAssetDiffCommandlet::ExportFile(const FString& Filename, const TArray<UObject*>& LoadedObjects)
{
	const FString Extension(TEXT("t3d"));
	FStringOutputDevice Buffer;
	const FExportObjectInnerContext Context;
	for (UObject* Obj : LoadedObjects)
	{
		UExporter* Exporter = UExporter::FindExporter(Obj, *Extension);
		if (Exporter == nullptr)
		{
			UE_LOG(LogUatdDiffAssets, Warning, TEXT("Could not find exporter."));
			return false;
		}
		UExporter::ExportToOutputDevice(&Context, Obj, Exporter, Buffer, *Extension, 0,
			PPF_ExportsNotFullyQualified, false);
		TMap<FString, FString> NativePropertyValues;
		if (Obj->GetNativePropertyValues(NativePropertyValues) && NativePropertyValues.Num())
		{
			int32 LargestKey = 0;
			for (const auto& Pair : NativePropertyValues)
			{
				LargestKey = FMath::Max(LargestKey, Pair.Key.Len());
			}
			for (const auto& Pair : NativePropertyValues)
			{
				Buffer.Logf(TEXT("  %s=%s"), *Pair.Key.RightPad(LargestKey), *Pair.Value);
			}
		}
	}
	if (!Buffer.Len())
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("No text was exported!"));
		return false;
	}
	if (!FFileHelper::SaveStringToFile(Buffer, *Filename))
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("Could not write %s"), *Filename);
		return false;
	}
	return true;
}

bool UUatdAssetDiffCommandlet::LaunchDiffTool(const FString& DiffCommand,
	const FString& TextFilename1, const FString& TextFilename2)
{
	FString Replaced = DiffCommand
		.Replace(TEXT("{1}"), *TextFilename1, ESearchCase::CaseSensitive)
		.Replace(TEXT("{2}"), *TextFilename2, ESearchCase::CaseSensitive);

	int32 ArgsAt = DiffCommand.Find(TEXT("{1}"), ESearchCase::CaseSensitive) - 1;
	FString Args;
	if (ArgsAt > 0)
	{
		Args = *Replaced + ArgsAt + 1;
		Replaced.LeftInline(ArgsAt, EAllowShrinking::No);
	}
	if (!FPlatformProcess::CreateProc(*Replaced, *Args, true, false, false, nullptr, 0, nullptr, nullptr).IsValid())
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("Could not launch %s."), *Replaced);
		return false;
	}
	return true;
}

// ---------------------------------------------------------------------------
// Supplemental class-default export.
//
// For the loaded package's BlueprintGeneratedClass CDO, every serializable
// property that is either declared by that class or whose value differs
// from the parent class CDO is written as:
//     [var|override|inherited] Name : CPPType = <ExportText value>
// Unavailable states are written as markers so the host tool can tell
// "no defaults data for this asset" from "dump failed".
// ---------------------------------------------------------------------------

void UUatdAssetDiffCommandlet::DumpClassDefaults(UPackage* Package)
{
	const FString Stem = FPackageName::GetShortName(Package->GetName());
	const FString OutFilename = FPaths::DiffDir() / (Stem + TEXT(".defaults.t3d"));

	FStringOutputDevice Out;
	Out.Logf(TEXT("; UatdClassDefaults: CDO property values for the class in %s\n"),
		*Package->GetName());

	UBlueprintGeneratedClass* BPGC = nullptr;
	ForEachObjectWithPackage(Package, [&BPGC](UObject* Obj)
	{
		if (UBlueprintGeneratedClass* C = Cast<UBlueprintGeneratedClass>(Obj))
		{
			BPGC = C;
		}
		return BPGC == nullptr;
	}, /*bIncludeNestedObjects*/ false);

	if (BPGC == nullptr)
	{
		Out.Logf(TEXT("NoBlueprintGeneratedClass\n"));
		FFileHelper::SaveStringToFile(Out, *OutFilename);
		UE_LOG(LogUatdDiffAssets, Display,
			TEXT("UatdClassDefaults: %s is not a Blueprint package; wrote marker."), *Stem);
		return;
	}

	UObject* CDO = BPGC->GetDefaultObject();
	UClass* Super = BPGC->GetSuperClass();
	UObject* SuperCDO = Super ? Super->GetDefaultObject() : nullptr;
	if (CDO == nullptr)
	{
		Out.Logf(TEXT("NoDefaultObject\n"));
		FFileHelper::SaveStringToFile(Out, *OutFilename);
		UE_LOG(LogUatdDiffAssets, Warning,
			TEXT("UatdClassDefaults: %s produced no class default object."), *Stem);
		return;
	}

	Out.Logf(TEXT("Class=%s\n"), *BPGC->GetPathName());
	Out.Logf(TEXT("SuperClass=%s\n"), Super ? *Super->GetPathName() : TEXT("None"));

	int32 Count = 0;
	int32 SkippedTransient = 0;
	int32 SkippedDelegate = 0;
	for (TFieldIterator<FProperty> It(BPGC); It; ++It)
	{
		FProperty* Prop = *It;
		if (Prop->HasAnyPropertyFlags(CPF_Transient | CPF_DuplicateTransient | CPF_NonPIEDuplicateTransient))
		{
			++SkippedTransient; // runtime-only values; not part of saved defaults
			continue;
		}
		if (Prop->IsA<FDelegateProperty>() || Prop->IsA<FMulticastDelegateProperty>())
		{
			++SkippedDelegate; // delegates have no comparable default value
			continue;
		}

		const bool bDeclaredHere = Prop->GetOwnerClass() == BPGC;
		const TCHAR* Origin;
		if (bDeclaredHere)
		{
			Origin = TEXT("var");
		}
		else if (SuperCDO == nullptr)
		{
			Origin = TEXT("inherited");
		}
		else
		{
			const void* Mine = Prop->ContainerPtrToValuePtr<void>(CDO);
			const void* Base = Prop->ContainerPtrToValuePtr<void>(SuperCDO);
			if (Prop->Identical(Mine, Base, PPF_None))
			{
				continue; // inherited and unchanged — not worth a line
			}
			Origin = TEXT("override");
		}

		FString Value;
		Prop->ExportText_Direct(Value, Prop->ContainerPtrToValuePtr<void>(CDO),
			nullptr, CDO, PPF_Delimited);
		Out.Logf(TEXT("[%s] %s : %s = %s\n"), Origin, *Prop->GetName(), *Prop->GetCPPType(), *Value);
		++Count;
	}

	Out.Logf(TEXT("PropertyCount=%d\n"), Count);
	Out.Logf(TEXT("; skipped: %d transient, %d delegate\n"), SkippedTransient, SkippedDelegate);
	if (FFileHelper::SaveStringToFile(Out, *OutFilename))
	{
		UE_LOG(LogUatdDiffAssets, Display, TEXT("UatdClassDefaults: %s -> %d propertie(s)"), *Stem, Count);
	}
	else
	{
		UE_LOG(LogUatdDiffAssets, Warning, TEXT("UatdClassDefaults: failed to write %s"), *OutFilename);
	}
}
