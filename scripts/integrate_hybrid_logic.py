#!/usr/bin/env python3
"""
Integrate hybrid logic into existing v4.2 methods
"""

def integrate_hybrid():
    with open('ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs', 'r') as f:
        lines = f.readlines()

    output = []
    i = 0

    while i < len(lines):
        line = lines[i]

        # 1. Add UpdateRegime() call in OnBarUpdate after risk checks
        if 'if (hardLimitHit || weeklyLimitHit)' in line and i < 400:  # Make sure it's in OnBarUpdate
            output.append(line)
            i += 1
            output.append(lines[i])  # return statement
            i += 1
            output.append('\n')
            output.append('\t\t\t// NEW v4.2: Update market regime\n')
            output.append('\t\t\tUpdateRegime();\n')
            continue

        # 2. Modify ExecuteSignal to use adaptive parameters
        if 'private void ExecuteSignal(Signal signal)' in line:
            # Skip to the part where we set stops/targets
            output.append(line)
            i += 1

            # Copy lines until we find where stops/targets are set
            while i < len(lines):
                if 'Get parameters for signal type' in lines[i]:
                    output.append(lines[i])
                    i += 1
                    break
                output.append(lines[i])
                i += 1

            # Now replace the parameter assignment logic
            output.append('\t\t\tdouble target = 0;\n')
            output.append('\t\t\tdouble stop = 0;\n')
            output.append('\t\t\tint maxHold = 0;\n')
            output.append('\n')
            output.append('\t\t\t// NEW v4.2: Use adaptive parameters based on regime\n')
            output.append('\t\t\tif (UseHybridMode)\n')
            output.append('\t\t\t{\n')
            output.append('\t\t\t\t// Use regime-based parameters\n')
            output.append('\t\t\t\tstop = currentStopDistance;\n')
            output.append('\t\t\t\ttarget = currentTargetDistance;\n')
            output.append('\t\t\t\tmaxHold = currentMaxHold;\n')
            output.append('\t\t\t}\n')
            output.append('\t\t\telse\n')
            output.append('\t\t\t{\n')
            output.append('\t\t\t\t// Use signal-type specific parameters (v4.1 behavior)\n')
            # Skip original if/else logic and find it
            while i < len(lines) and 'SetProfitTarget' not in lines[i]:
                if 'if (signal.Type == "ABSORPTION")' in lines[i]:
                    output.append('\t\t')  # Add extra indent
                if 'target = AbsorptionTarget;' in lines[i]:
                    output.append('\t\t')
                if 'stop = AbsorptionStop;' in lines[i]:
                    output.append('\t\t')
                if 'maxHold = AbsorptionMaxHold;' in lines[i]:
                    output.append('\t\t')
                if 'else if (signal.Type == "BREAKOUT")' in lines[i]:
                    output.append('\t\t')
                if 'target = BreakoutTarget;' in lines[i]:
                    output.append('\t\t')
                if 'stop = BreakoutStop;' in lines[i]:
                    output.append('\t\t')
                if 'maxHold = BreakoutMaxHold;' in lines[i]:
                    output.append('\t\t')
                if any(keyword in lines[i] for keyword in ['target =', 'stop =', 'maxHold =', 'if (signal.Type', 'else if']):
                    output.append(lines[i])
                i += 1
            output.append('\t\t\t}\n')
            output.append('\n')
            continue

        # 3. Update CheckTimeBasedExit to use currentMaxHold
        if 'int maxHold = currentSignalType == "ABSORPTION" ? AbsorptionMaxHold : BreakoutMaxHold;' in line:
            output.append('\t\t\t// NEW v4.2: Use regime-based max hold if hybrid mode enabled\n')
            output.append('\t\t\tint maxHold = UseHybridMode ? currentMaxHold : \n')
            output.append('\t\t\t\t(currentSignalType == "ABSORPTION" ? AbsorptionMaxHold : BreakoutMaxHold);\n')
            i += 1
            continue

        # 4. Update DataLoaded to initialize regime
        if 'Print("CG SCALPING NT8 NATIVE v4.1 - SHORT GATE");' in line:
            output.append('\t\t\t\tPrint("CG SCALPING NT8 NATIVE v4.2 - HYBRID");\n')
            i += 1
            continue

        if 'Print("NEW v4.1 FEATURES:");' in line:
            output.append('\t\t\t\tPrint("NEW v4.2 FEATURES:");\n')
            i += 1
            # Add hybrid mode info
            output.append('\t\t\t\tPrint("  ✓ HYBRID MODE: Auto-detect market regime");\n')
            output.append('\t\t\t\tPrint("    - Hybrid enabled: " + (UseHybridMode ? "YES" : "NO"));\n')
            output.append('\t\t\t\tPrint("    - Trend EMA sep: " + TrendDetectionEMASeparation);\n')
            output.append('\t\t\t\tPrint("    - Trend target: " + TrendModeTarget + " / stop: " + TrendModeStop);\n')
            output.append('\t\t\t\tPrint("");\n')
            output.append('\t\t\t\tPrint("v4.1 FEATURES:");\n')
            continue

        # Default: keep line
        output.append(line)
        i += 1

    with open('ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs', 'w') as f:
        f.writelines(output)

    print("✅ Integrated hybrid logic:")
    print("   - UpdateRegime() called in OnBarUpdate()")
    print("   - ExecuteSignal() uses adaptive parameters")
    print("   - CheckTimeBasedExit() uses currentMaxHold")
    print("   - Updated startup logging")

if __name__ == "__main__":
    integrate_hybrid()
