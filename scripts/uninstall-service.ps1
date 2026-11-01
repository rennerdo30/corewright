#Requires -RunAsAdministrator
# Owner step. Stopping the service reverts every change it applied; rules.json in ProgramData is kept.
$ErrorActionPreference = 'Continue'
sc.exe stop Corewright
Start-Sleep -Seconds 2
sc.exe delete Corewright
