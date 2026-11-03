#!/bin/bash
# Writes the binary evidence for one game core DLL (every offset the Outside Recorder reads).
# usage: OutsideRecorder/tools/BinaryVerify/verify.sh <path-to-BinaryVerify.exe> <CvGameCore_Expansion2.dll> > evidence.txt
# Read the output next to Layouts.cs: each layout value must appear here (OutsideRecorder.Tests/EvidenceTests checks it).
B="$1"; D="$2"
echo "# BinaryVerify evidence for $D"
echo "# sha256 $(sha256sum "$D" | cut -d' ' -f1)"
echo "## vtables (RTTI)"
for c in ".?AVCvGlobals@@" ".?AVCvPlayerAI@@" ".?AVCvCityAI@@" ".?AVCvMap@@" ".?AV?\$FAutoArchiveClassContainer@VCvUnit@@@@"; do "$B" "$D" rtti "$c"; done
echo "## gGlobals = static object built by the CvGlobals constructor"
"$B" "$D" objects "$("$B" "$D" rtti ".?AVCvGlobals@@" | head -1 | sed 's/.*RVA=0x//')"
echo "## player array: GET_PLAYER inlined as index*size + [global]"
"$B" "$D" players | head -2
echo "## unit (Lua Unit methods -> getters)"
"$B" "$D" lua IsEmbarked GetX GetY GetID GetOwner GetDamage GetMoves GetUnitType GetFortifyTurns GetExperience GetLevel IsEmbarked GetActivityType GetGameTurnCreated GetLastMoveTurn
echo "## player (Lua Player methods)"
"$B" "$D" lua GetUnitByID GetUnitByID GetCityByID IsAlive IsTurnActive GetID GetCivilizationType IsHuman
echo "## city (Lua City methods)"
"$B" "$D" lua GetPopulation GetX GetY GetID GetOwner GetPopulation GetDamage IsPuppet IsOccupied GetOriginalOwner GetGameTurnFounded GetName
echo "## game + map (Lua Game / Map)"
"$B" "$D" lua GetGameTurn GetGameTurn GetActivePlayer
"$B" "$D" lua GetGridSize GetGridSize
echo "## gameStarted: interface CvDllPreGame slot 31 (the exe calls it)"
"$B" "$D" vfn ".?AVCvDllPreGame@@" 31 | tee /dev/stderr 2>/dev/null
T=$("$B" "$D" vfn ".?AVCvDllPreGame@@" 31 | grep -o 'call 10[0-9A-F]*h' | head -1 | sed 's/call 10//; s/h//')
echo "  CvPreGame::gameStarted body:"; "$B" "$D" dis "$T" 2
echo "## game seeds: interface CvDllPreGame slot 70 (mapRandomSeed) and 192 (syncRandomSeed), the exe calls them"
for k in 70 192; do
  "$B" "$D" vfn ".?AVCvDllPreGame@@" $k
  T=$("$B" "$D" vfn ".?AVCvDllPreGame@@" $k | grep -o 'call 10[0-9A-F]*h' | head -1 | sed 's/call 10//; s/h//')
  echo "  slot $k body:"; "$B" "$D" dis "$T" 2
done
echo "## unit / city lists: FFreeListTrashArray::GetAt (vtable slot 4)"
"$B" "$D" vfn ".?AV?\$FFreeListTrashArray@VCvUnit@@@@" 4
"$B" "$D" vfn ".?AV?\$FFreeListTrashArray@VCvCityAI@@@@" 4
