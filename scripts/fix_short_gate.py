#!/usr/bin/env python3
"""
Fix Short Gate bug - ensure it can NEVER affect long trades
"""

def fix_short_gate():
    input_file = "ninjascript/CGScalpingStrategyNT8Native_v4_1_ShortGate.cs"
    output_file = "ninjascript/CGScalpingStrategyNT8Native_v4_1_ShortGate_FIXED.cs"

    with open(input_file, 'r') as f:
        lines = f.readlines()

    # Find and fix the PassesShortGate method
    fixed_lines = []
    i = 0
    while i < len(lines):
        line = lines[i]

        # Find the start of PassesShortGate
        if "private bool PassesShortGate(Signal signal, OrderFlowBar lastBar)" in line:
            # Write method signature
            fixed_lines.append(line)
            i += 1

            # Write opening brace
            fixed_lines.append(lines[i])  # {
            i += 1

            # Skip old comments and check
            while i < len(lines) and (lines[i].strip().startswith("//") or
                                     lines[i].strip() == "" or
                                     "if (signal.Direction != MarketPosition.Short)" in lines[i] or
                                      "return true;" in lines[i]):
                i += 1
                if "return true;" in lines[i-1]:
                    break

            # Insert new defensive check
            fixed_lines.append("\t\t\t// CRITICAL FIX: Check direction FIRST with null safety\n")
            fixed_lines.append("\t\t\t// Longs MUST pass immediately without ANY gate logic\n")
            fixed_lines.append("\t\t\tif (signal == null)\n")
            fixed_lines.append("\t\t\t{\n")
            fixed_lines.append('\t\t\t\tPrint("ERROR: PassesShortGate called with null signal!");\n')
            fixed_lines.append("\t\t\t\treturn false;\n")
            fixed_lines.append("\t\t\t}\n")
            fixed_lines.append("\n")
            fixed_lines.append("\t\t\tif (signal.Direction != MarketPosition.Short)\n")
            fixed_lines.append("\t\t\t{\n")
            fixed_lines.append("\t\t\t\t// Long signal - MUST pass immediately\n")
            fixed_lines.append("\t\t\t\treturn true;\n")
            fixed_lines.append("\t\t\t}\n")

            # Continue with rest
            continue

        fixed_lines.append(line)
        i += 1

    # Write fixed file
    with open(output_file, 'w') as f:
        f.writelines(fixed_lines)

    print(f"✅ Fixed file written to: {output_file}")
    print()
    print("Changes made:")
    print("1. Added null safety check at start of PassesShortGate()")
    print("2. Ensured direction check happens FIRST")
    print("3. Added defensive return for longs before ANY gate logic")
    print()
    print("Next steps:")
    print(f"1. Review the fixed file: {output_file}")
    print(f"2. If good, copy to NT8: cp {output_file} /path/to/NinjaTrader8/Strategies/")
    print("3. Recompile in NT8 (F3 -> F5)")
    print("4. Re-test on April 13 replay")

if __name__ == "__main__":
    fix_short_gate()
