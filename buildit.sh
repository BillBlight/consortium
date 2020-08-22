#!/bin/bash
echo -e "\e[41m Getting Avaialble CPU Cores \e[0m"
NUMCORES=$(grep -c ^processor /proc/cpuinfo)
echo -e "\e[41m CPU CORES: $NUMCORES \e[0m"
NUMCORES=$(expr $NUMCORES / 4)
if [ "$NUMCORES" -lt 1 ] ; then
NUMCORES=1

echo -e "\e[41m 
Selecting $NUMCORES Core for msbuild. \e[0m"
else
echo -e "\e[41m 
Selecting 25% of Cores for msbuild: $NUMCORES \e[0m"
fi
sleep 3
echo "Scrubbing Directories"
find . -name "*.csproj" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.csproj.user" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.build" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*Temporary*" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.cache" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.rej" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.orig" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.pdb" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.mdb" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.bak" -type f -print0 | xargs -0 /bin/rm -f
find . -name "*.obj" -type f -print0 | xargs -0 /bin/rm -rf
find . -name "*obj" -type d -print0 | xargs -0 /bin/rm -rf

echo "Running Prebuild"
./runprebuild.sh

echo "Building Release, MS Debug Info sucks on linux"
msbuild -m:$NUMCORES /p:Configuration=Release /verbosity:minimal
BUILDCODE=$?


if [ $BUILDCODE -gt 0 ]; then
echo -e "\e[41m
The Build encoutered and error.\e[0m
"
exit 1
else
echo -e "
\e[30;48;5;82m
The Build appears to have suceeded\e[0m
"
fi
echo "done"
