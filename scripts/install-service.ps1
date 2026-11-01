#Requires -RunAsAdministrator
# Owner step: registers the published service. Agents never run this (no elevated launches).
param([string]$Bin = (Join-Path $PSScriptRoot '..\artifacts\publish'))
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path (Join-Path $Bin 'Corewright.Service.exe')).Path

sc.exe create Corewright binPath= "`"$exe`"" start= auto DisplayName= "Corewright"
sc.exe description Corewright "Applies Corewright process rules: CPU, I/O, GPU and memory priority, affinity, memory caps."
sc.exe failure Corewright reset= 86400 actions= restart/5000/restart/5000//
sc.exe start Corewright
