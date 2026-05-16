# Fix VirtualBox Guest Control Authentication
# Run this in Windows VM as Administrator
# Purpose: Enable remote command execution via VBoxManage guestcontrol

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "VirtualBox Guest Control Auth Fix" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Check VBoxService status
Write-Host "[1/6] Checking VBoxService status..." -ForegroundColor Yellow
$vboxService = Get-Service VBoxService -ErrorAction SilentlyContinue
if ($vboxService) {
    Write-Host "VBoxService Status: $($vboxService.Status)" -ForegroundColor Green
    if ($vboxService.Status -ne "Running") {
        Write-Host "Starting VBoxService..." -ForegroundColor Yellow
        Start-Service VBoxService
    }
} else {
    Write-Host "ERROR: VBoxService not found! Guest Additions may not be installed." -ForegroundColor Red
    exit 1
}

# 2. Enable LocalAccountTokenFilterPolicy (allows remote admin access)
Write-Host ""
Write-Host "[2/6] Enabling LocalAccountTokenFilterPolicy..." -ForegroundColor Yellow
try {
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" `
                     -Name "LocalAccountTokenFilterPolicy" `
                     -Value 1 `
                     -Type DWord `
                     -Force
    Write-Host "LocalAccountTokenFilterPolicy enabled (Value: 1)" -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to set LocalAccountTokenFilterPolicy: $_" -ForegroundColor Red
}

# 3. Disable blank password restriction for network logon
Write-Host ""
Write-Host "[3/6] Configuring blank password policy..." -ForegroundColor Yellow
try {
    $tempSecPol = "$env:TEMP\secpol_temp.cfg"
    secedit /export /cfg $tempSecPol /quiet

    $content = Get-Content $tempSecPol
    $newContent = $content -replace "MACHINE\\System\\CurrentControlSet\\Control\\Lsa\\LimitBlankPasswordUse=4,1", `
                                     "MACHINE\\System\\CurrentControlSet\\Control\\Lsa\\LimitBlankPasswordUse=4,0"
    $newContent | Set-Content $tempSecPol

    secedit /configure /db C:\Windows\security\local.sdb /cfg $tempSecPol /areas SECURITYPOLICY /quiet
    Remove-Item $tempSecPol -Force

    Write-Host "Blank password policy configured" -ForegroundColor Green
} catch {
    Write-Host "WARNING: Could not modify blank password policy: $_" -ForegroundColor Yellow
}

# 4. Verify Administrator account is enabled
Write-Host ""
Write-Host "[4/6] Checking Administrator account..." -ForegroundColor Yellow
$adminAccount = Get-LocalUser -Name "Administrator" -ErrorAction SilentlyContinue
if ($adminAccount) {
    if ($adminAccount.Enabled) {
        Write-Host "Administrator account is enabled" -ForegroundColor Green
    } else {
        Write-Host "Enabling Administrator account..." -ForegroundColor Yellow
        Enable-LocalUser -Name "Administrator"
        Write-Host "Administrator account enabled" -ForegroundColor Green
    }
} else {
    Write-Host "ERROR: Administrator account not found!" -ForegroundColor Red
}

# 5. Check Guest Additions version
Write-Host ""
Write-Host "[5/6] Checking Guest Additions version..." -ForegroundColor Yellow
$gaPath = "HKLM:\SOFTWARE\Oracle\VirtualBox Guest Additions"
if (Test-Path $gaPath) {
    $gaVersion = Get-ItemProperty $gaPath -Name "Version" -ErrorAction SilentlyContinue
    $gaRevision = Get-ItemProperty $gaPath -Name "Revision" -ErrorAction SilentlyContinue
    Write-Host "Guest Additions Version: $($gaVersion.Version)" -ForegroundColor Green
    Write-Host "Guest Additions Revision: $($gaRevision.Revision)" -ForegroundColor Green
} else {
    Write-Host "WARNING: Guest Additions registry key not found" -ForegroundColor Yellow
}

# 6. Restart VBoxService to apply changes
Write-Host ""
Write-Host "[6/6] Restarting VBoxService..." -ForegroundColor Yellow
Restart-Service VBoxService
Start-Sleep -Seconds 2
$vboxServiceAfter = Get-Service VBoxService
Write-Host "VBoxService restarted. Status: $($vboxServiceAfter.Status)" -ForegroundColor Green

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Configuration Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Keep this Windows session logged in (don't log out)" -ForegroundColor White
Write-Host "2. From Linux, test guest control with:" -ForegroundColor White
Write-Host ""
Write-Host "   VBoxManage guestcontrol `"win`" run --exe `"C:\\Windows\\System32\\cmd.exe`" \" -ForegroundColor Gray
Write-Host "              --username `"Administrator`" --password `"unlucky-strange`" \" -ForegroundColor Gray
Write-Host "              -- /c `"echo Guest control test successful`"" -ForegroundColor Gray
Write-Host ""
Write-Host "If the test fails, you may need to:" -ForegroundColor Yellow
Write-Host "- Reboot Windows VM" -ForegroundColor White
Write-Host "- Check Windows Firewall settings" -ForegroundColor White
Write-Host "- Verify password is correct (unlucky-strange)" -ForegroundColor White
Write-Host ""
