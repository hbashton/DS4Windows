<#
.SYNOPSIS
    DSX UDP Mod Protocol Test Script
    Simulates a PC game mod sending trigger, RGB, and status instructions to DS4Windows on port 6969.
#>

param(
    [string]$ServerIp = "127.0.0.1",
    [int]$Port = 6969
)

$udpClient = New-Object System.Net.Sockets.UdpClient
$udpClient.Client.ReceiveTimeout = 2000
$targetEndPoint = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse($ServerIp), $Port)

function Send-DSXInstruction {
    param([string]$JsonPayload)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($JsonPayload)
    [void]$udpClient.Send($bytes, $bytes.Length, $targetEndPoint)
}

function Query-DSXStatus {
    Write-Host "`n--- 1. Querying Controller Status (GetDSXStatus) ---" -ForegroundColor Cyan
    $json = '{"instructions":[{"type":"GetDSXStatus","parameters":[0]}]}'
    Send-DSXInstruction $json

    try {
        $senderEP = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $receivedBytes = $udpClient.Receive([ref]$senderEP)
        $response = [System.Text.Encoding]::UTF8.GetString($receivedBytes)
        Write-Host "Received Status Response:" -ForegroundColor Green
        Write-Host $response
    }
    catch {
        Write-Host "No response received within timeout. Make sure DS4Windows is running and the DSX UDP server is started." -ForegroundColor Yellow
    }
}

function Test-RGB {
    Write-Host "`n--- 2. Testing RGB Lightbar (Cycling Cyan -> Magenta -> Yellow) ---" -ForegroundColor Cyan
    
    # Cyan
    Write-Host "Setting Lightbar to Cyan..." -ForegroundColor Cyan
    Send-DSXInstruction '{"instructions":[{"type":"RGBUpdate","parameters":[0,0,255,255,255]}]}'
    Start-Sleep -Seconds 1

    # Magenta
    Write-Host "Setting Lightbar to Magenta..." -ForegroundColor Magenta
    Send-DSXInstruction '{"instructions":[{"type":"RGBUpdate","parameters":[0,255,0,255,255]}]}'
    Start-Sleep -Seconds 1

    # Yellow
    Write-Host "Setting Lightbar to Yellow..." -ForegroundColor Yellow
    Send-DSXInstruction '{"instructions":[{"type":"RGBUpdate","parameters":[0,255,255,0,255]}]}'
    Start-Sleep -Seconds 1
}

function Test-Triggers {
    Write-Host "`n--- 3. Testing Adaptive Triggers (Right Trigger) ---" -ForegroundColor Cyan
    
    # Resistance / Feedback mode (Heavy tension)
    Write-Host "-> Applying Heavy Feedback Resistance (Mode 21, Start 0, Force 8)..." -ForegroundColor White
    Write-Host "   (Pull Right Trigger now to feel the stiff resistance!)" -ForegroundColor Gray
    Send-DSXInstruction '{"instructions":[{"type":"TriggerUpdate","parameters":[0,1,21,"0,8"]}]}'
    Start-Sleep -Seconds 4

    # Machine Gun / Weapon mode
    Write-Host "-> Applying Weapon Click (Mode 22, Start 2, End 7, Force 6)..." -ForegroundColor White
    Write-Host "   (Pull Right Trigger to feel the weapon sear breakpoint!)" -ForegroundColor Gray
    Send-DSXInstruction '{"instructions":[{"type":"TriggerUpdate","parameters":[0,1,22,"2,7,6"]}]}'
    Start-Sleep -Seconds 4

    # Bow mode
    Write-Host "-> Applying Bow String Tension (Mode 14, Start 1, End 8, Force 6, Snap 4)..." -ForegroundColor White
    Write-Host "   (Pull Right Trigger to feel the bow draw and snap!)" -ForegroundColor Gray
    Send-DSXInstruction '{"instructions":[{"type":"TriggerUpdate","parameters":[0,1,14,"1,8,6,4"]}]}'
    Start-Sleep -Seconds 4

    # Reset
    Write-Host "`n-> Resetting Controller to User Profile Settings (ResetToUserSettings)..." -ForegroundColor Green
    Send-DSXInstruction '{"instructions":[{"type":"ResetToUserSettings","parameters":[0]}]}'
}

Write-Host "==========================================" -ForegroundColor Green
Write-Host "   DS4Windows DSX UDP Protocol Tester     " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Target: ${ServerIp}:${Port}"

Query-DSXStatus
Test-RGB
Test-Triggers

$udpClient.Close()
Write-Host "`nTest finished!" -ForegroundColor Green
