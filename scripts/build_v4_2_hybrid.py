#!/usr/bin/env python3
"""
Build v4.2 Hybrid Strategy from v4.1 Fixed
Adds regime detection and adaptive parameters
"""

def build_v4_2():
    # Read v4.1 FIXED as base
    with open('ninjascript/CGScalpingStrategyNT8Native_v4_1_ShortGate_FIXED.cs', 'r') as f:
        v4_1_lines = f.readlines()

    output = []
    i = 0

    while i < len(v4_1_lines):
        line = v4_1_lines[i]

        # 1. Update header comment
        if 'v4.1 - SHORT GATE' in line:
            output.append('// CG MNQ Scalping Strategy - NT8 NATIVE VERSION v4.2 - HYBRID\n')
            output.append('// NEW v4.2: Hybrid scalp/trend - auto-detects market regime and adapts\n')
            i += 2
            continue

        # 2. Update class name
        if 'public class CGScalpingStrategyNT8Native_v4_1_ShortGate' in line:
            output.append('\tpublic class CGScalpingStrategyNT8Native_v4_2_Hybrid : Strategy\n')
            i += 1
            continue

        # 3. Add regime enum after OrderFlowBar class (around line 52)
        if line.strip() == '}' and i > 40 and i < 60 and 'OrderFlowBar' in ''.join(v4_1_lines[i-10:i]):
            output.append(line)  # closing brace of OrderFlowBar
            output.append('\n')
            output.append('\t\t// NEW v4.2: Market regime detection\n')
            output.append('\t\tprivate enum MarketRegime\n')
            output.append('\t\t{\n')
            output.append('\t\t\tTRENDING,\n')
            output.append('\t\t\tCHOPPY,\n')
            output.append('\t\t\tTRANSITION\n')
            output.append('\t\t}\n')
            i += 1
            continue

        # 4. Add regime tracking variables after EMA variables (around line 82)
        if 'private EMA emaSlow;' in line:
            output.append(line)
            output.append('\n')
            output.append('\t\t// NEW v4.2: Regime detection state\n')
            output.append('\t\tprivate MarketRegime currentRegime = MarketRegime.CHOPPY;\n')
            output.append('\t\tprivate MarketRegime lastDetectedRegime = MarketRegime.CHOPPY;\n')
            output.append('\t\tprivate int regimeConfirmationBars = 0;\n')
            output.append('\t\tprivate double currentStopDistance = 5.0;\n')
            output.append('\t\tprivate double currentTargetDistance = 8.0;\n')
            output.append('\t\tprivate int currentMaxHold = 120;\n')
            output.append('\t\tprivate double currentTrailDistance = 3.0;\n')
            i += 1
            continue

        # 5. Add hybrid parameters after Short Gate parameters (search for ShortGateMinEMASeparation)
        if 'public double ShortGateMinEMASeparation { get; set; }' in line:
            output.append(line)
            output.append('\n')
            output.append('\t\t// NEW v4.2: HYBRID MODE PARAMETERS\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Display(Name = "Enable Hybrid Mode", Order = 1, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic bool UseHybridMode { get; set; }\n')
            output.append('\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Range(5.0, 20.0)]\n')
            output.append('\t\t[Display(Name = "Trend Detection: Min EMA Separation", Order = 2, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic double TrendDetectionEMASeparation { get; set; }\n')
            output.append('\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Range(3, 10)]\n')
            output.append('\t\t[Display(Name = "Regime Confirmation Bars", Order = 3, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic int RegimeConfirmationBars { get; set; }\n')
            output.append('\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Range(10.0, 40.0)]\n')
            output.append('\t\t[Display(Name = "Trend Mode: Target", Order = 4, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic double TrendModeTarget { get; set; }\n')
            output.append('\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Range(8.0, 20.0)]\n')
            output.append('\t\t[Display(Name = "Trend Mode: Stop", Order = 5, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic double TrendModeStop { get; set; }\n')
            output.append('\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Range(300, 1200)]\n')
            output.append('\t\t[Display(Name = "Trend Mode: Max Hold (seconds)", Order = 6, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic int TrendModeMaxHold { get; set; }\n')
            output.append('\n')
            output.append('\t\t[NinjaScriptProperty]\n')
            output.append('\t\t[Range(5.0, 15.0)]\n')
            output.append('\t\t[Display(Name = "Trend Mode: Trail Distance", Order = 7, GroupName = "2d. Hybrid Mode")]\n')
            output.append('\t\tpublic double TrendModeTrailDistance { get; set; }\n')
            i += 1
            continue

        # 6. Add hybrid defaults after ShortGate defaults (search for ShortGateMinEMASeparation = 5.0)
        if 'ShortGateMinEMASeparation = 5.0;' in line:
            output.append(line)
            output.append('\n')
            output.append('\t\t\t\t// NEW v4.2: Hybrid mode defaults\n')
            output.append('\t\t\t\tUseHybridMode = true;\n')
            output.append('\t\t\t\tTrendDetectionEMASeparation = 10.0;\n')
            output.append('\t\t\t\tRegimeConfirmationBars = 5;\n')
            output.append('\t\t\t\tTrendModeTarget = 25.0;\n')
            output.append('\t\t\t\tTrendModeStop = 12.0;\n')
            output.append('\t\t\t\tTrendModeMaxHold = 600;\n')
            output.append('\t\t\t\tTrendModeTrailDistance = 8.0;\n')
            i += 1
            continue

        # 7. Update display name
        if 'return "CG Scalping NT8 Native v4.1 - Short Gate";' in line:
            output.append('\t\t\treturn "CG Scalping NT8 Native v4.2 - Hybrid";\n')
            i += 1
            continue

        # 8. Update description
        if 'Description = @"NT8 Native Order Flow Scalping - v4.1 SHORT GATE";' in line:
            output.append('\t\t\t\tDescription = @"NT8 Native Order Flow Scalping - v4.2 HYBRID";\n')
            i += 1
            continue

        # 9. Update strategy name
        if 'Name = "CGScalpingStrategyNT8Native_v4_1_ShortGate";' in line:
            output.append('\t\t\t\tName = "CGScalpingStrategyNT8Native_v4_2_Hybrid";\n')
            i += 1
            continue

        # Default: keep line as-is
        output.append(line)
        i += 1

    # Write output
    with open('ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs', 'w') as f:
        f.writelines(output)

    print("✅ Created base v4.2 file with:")
    print("   - Updated class name and headers")
    print("   - Added MarketRegime enum")
    print("   - Added regime state variables")
    print("   - Added hybrid mode parameters")
    print()
    print("⚠️  Still need to add:")
    print("   - Regime detection methods")
    print("   - Adaptive parameter updates")
    print("   - Modified ExecuteSignal()")
    print("   - Regime-aware trailing stops")
    print()
    print("📄 File: ninjascript/CGScalpingStrategyNT8Native_v4_2_Hybrid.cs")

if __name__ == "__main__":
    build_v4_2()
