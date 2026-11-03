#!/bin/bash
# Extra evidence for a game core DLL that ships its PDB (Snowve): the unit / civ info tables the reader uses for names. Append it to the verify.sh output:
#   verify.sh <BinaryVerify.exe> <dll> > evidence.txt
#   verify-extra.sh <BinaryVerify.exe> <LayoutDump.exe> <dll> <pdb> >> evidence.txt
# The PDB is only used to find the info-table functions; their machine code is printed (that code is the evidence).
B="$1"; L="$2"; D="$3"; P="$4"
pub() { "$L" "$D" "$P" "publics=$1" 2>/dev/null | grep -F "public $1" | head -1 | sed 's/.*RVA=0x//'; }
echo "## info tables: CvGlobals::getUnitInfo() / getCivilizationInfo() / CvBaseInfo::CacheResults"
U=$(pub '?getUnitInfo@CvGlobals@@QAEAAV?$vector@PAVCvUnitEntry@@')
C=$(pub '?getCivilizationInfo@CvGlobals@@QAEPAVCvCivilizationInfo@@W4CivilizationTypes@@@Z')
R=$(pub '?CacheResults@CvBaseInfo@@')
if [ -z "$U" ] || [ -z "$C" ] || [ -z "$R" ]; then
  echo "  not located: this PDB's publics stream cannot be read by SharpPdb (info-table offsets are PDB-only for this DLL)"; exit 0
fi
echo "  getUnitInfo (vector) at 0x$U:"; "$B" "$D" dis "$U" 2
echo "  getCivilizationInfo(CivilizationTypes) at 0x$C:"; "$B" "$D" dis "$C" 11
echo "  CvBaseInfo::CacheResults at 0x$R (the string after 'push' is the column name; 'lea ecx,[esi+..]' is where it goes):"
"$B" "$D" dis "$R" 22 | sed -n '13,22p'
for a in $("$B" "$D" dis "$R" 22 | sed -n '13,22p' | grep -o 'push 10[0-9A-F]*h' | sed 's/push 10//; s/h//'); do
  python -I -c "
import struct,sys
d=open(sys.argv[1],'rb').read(); r=int(sys.argv[2],16)
pe=struct.unpack_from('<I',d,0x3c)[0]; n=struct.unpack_from('<H',d,pe+6)[0]; s0=pe+24+struct.unpack_from('<H',d,pe+20)[0]
for i in range(n):
    vs,va,rs,rp=struct.unpack_from('<IIII',d,s0+i*40+8)
    if va<=r<va+max(vs,rs): o=r-va+rp; print('  string at 0x%X = \"%s\"' % (r, d[o:d.index(b'\0',o)].decode('latin1')))
" "$D" "$a"
done
