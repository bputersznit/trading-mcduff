# CG_CH_NT8_Playback_Pipeline_v1 — Recommended Execution Order

---

# PHASE 1 — Linux Local Export (ClickHouse)

## Step 1:

Configure environment:

```bash id="llv2dy"
source .env
```

Verify:

```bash id="my5zrr"
echo $CH_HOST
echo $CH_DATABASE
```

---

## Step 2:

Run Parquet export:

```bash id="1msjvk"
python3 CG_CH_Parquet_Exporter_v1.py
```

### Output:

```text id="g9gfva"
/exports/
 ├── 2025-09-23.parquet.zst
 ├── 2025-09-24.parquet.zst
 ├── ...
 ├── manifest.csv
 └── checksums.sha256
```

---

## Step 3:

(Optional smoke test)

Export one NT CSV day:

```bash id="odj74t"
python3 CG_CH_NT8_CSV_Smoke_Exporter.py
```

---

# PHASE 2 — Compression Verification

## Step 4:

Validate:

```bash id="f2g4e2"
sha256sum -c checksums.sha256
```

---

# PHASE 3 — Transfer to VPS

## Step 5:

Configure rclone:

```bash id="c1iw0s"
rclone config
```

---

## Step 6:

Upload:

```bash id="8g1qf5"
bash CG_Rclone_Upload.sh
```

or:

```bash id="jl8jlj"
rclone sync /exports remote:NT8_Playback/
```

---

# PHASE 4 — VPS Staging

## Step 7:

On VPS:

Install:

```powershell id="bnr8ak"
python
rclone
pyarrow
pandas
```

---

## Step 8:

Pull data:

```powershell id="bdg9f2"
rclone sync remote:NT8_Playback C:\NT8_Staging\
```

---

# PHASE 5 — Convert for NT8

## Step 9:

Run:

```powershell id="zb2jfd"
python CG_Parquet_To_NT8_CSV_Converter.py
```

### Output:

```text id="x8wzjv"
C:\NT8_Imports\
 ├── MNQ_20250923.csv
 ├── MNQ_20250924.csv
```

---

# PHASE 6 — NinjaTrader Import

## Step 10:

Inside NinjaTrader 8:

```text id="vk7xhs"
Tools
→ Historical Data
→ Import
→ Select CSV
→ Instrument: MNQ 12-25
```

---

# PHASE 7 — Playback Validation

## Step 11:

Playback settings:

```text id="tx57xk"
Playback speed: 1x
T3 disabled
Telemetry disabled
Print diagnostics disabled
Single day
```

---

## Step 12:

Run:

```text id="ymmv3e"
CG_MNQ_Flagship_Hybrid_v1_1
```

---

# PHASE 8 — Validation Sequence

## Confirm:

```text id="obgdxi"
- ORB forms correctly
- Trades trigger
- No freeze
- No excessive CPU
- Proper timestamps
- Correct session windows
```

---

# PHASE 9 — Gradual Feature Enablement

```text id="v9hgm8"
1. ORB only
2. ORB + T2
3. Padder
4. T3
5. Telemetry
```

---

# Suggested First Operational Test

## Use:

```text id="ldjlwm"
Date: 2025-09-23
Instrument: MNQ 12-25
Mode: Tick import
Playback: 1x
```

---

# Strategic Safety Rules

```text id="x3g8vz"
DO NOT:
- Bulk import all Sep/Oct immediately
- Run full flagship at 50x+
- Enable telemetry first
- Trust T3 without depth
```

---

# Operational Command Chain

```text id="5xqg6z"
Export
→ Compress
→ Verify
→ Upload
→ Stage
→ Convert
→ Import
→ Playback
→ Validate
→ Scale
```

---

# McDuff Directive

```text id="1xgq0o"
Start with one day.
Prove import integrity.
Prove NT stability.
Then scale to full archive.
```

---

# Bottom Line

## Correct order:

```text id="xb6nys"
Exporter
→ Verify
→ rclone
→ VPS sync
→ Convert
→ NT import
→ Playback smoke test
→ Expand
```

This minimizes:

* Data corruption
* VPS overload
* NT playback freezes
* Contract mismatches
* Strategy false negatives

