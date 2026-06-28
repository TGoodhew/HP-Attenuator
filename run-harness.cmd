@echo off
title HP-Attenuator test harness
echo Starting HP-Attenuator harness (hardware, 100 MHz attenuation check)...
echo.
"C:\Users\Tony\source\HPAttenuator\src\HP-Attenuator.TestHarness\bin\Release\HP-Attenuator.TestHarness.exe" --hardware --fstart 100 --fstop 100 --fstep 1 --out "C:\Users\Tony\source\HPAttenuator\harness-results.csv"
echo.
echo ===== Harness finished (exit %ERRORLEVEL%). Press any key to close. =====
pause >nul
