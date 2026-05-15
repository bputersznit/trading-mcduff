// CG_L2_Capture_Chunked
// High-performance L2 market depth capture with automatic chunking
// Designed for NinjaTrader 8 Market Replay
// Target: MNQ full depth during RTH

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class CG_L2_Capture_Chunked : Strategy
    {
        #region Variables

        private StreamWriter currentChunk;
        private string outputFolder;
        private string sessionFolder;
        private int chunkIndex = 0;
        private int rowsInCurrentChunk = 0;
        private DateTime sessionDate;
        private bool isCapturing = false;

        // Configurable parameters
        private int maxRowsPerChunk = 50000;
        private int maxDepthLevels = 20;
        private string instrumentSymbol = "MNQ";

        // Performance tracking
        private int totalRowsCaptured = 0;
        private DateTime captureStartTime;

        #endregion

        #region Properties

        [Range(10000, 100000)]
        [Display(Name = "Max Rows Per Chunk", GroupName = "L2 Capture", Order = 1)]
        public int MaxRowsPerChunk
        {
            get { return maxRowsPerChunk; }
            set { maxRowsPerChunk = Math.Max(10000, Math.Min(100000, value)); }
        }

        [Range(5, 30)]
        [Display(Name = "Max Depth Levels", GroupName = "L2 Capture", Order = 2)]
        public int MaxDepthLevels
        {
            get { return maxDepthLevels; }
            set { maxDepthLevels = Math.Max(5, Math.Min(30, value)); }
        }

        [Display(Name = "Instrument Symbol", GroupName = "L2 Capture", Order = 3)]
        public string InstrumentSymbol
        {
            get { return instrumentSymbol; }
            set { instrumentSymbol = value; }
        }

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "L2 Market Depth Capture - Chunked for Performance";
                Name = "CG_L2_Capture_Chunked";
                Calculate = Calculate.OnEachTick;
                IsInstantiatedOnEachOptimizationIteration = false;

                // Default values
                MaxRowsPerChunk = 50000;
                MaxDepthLevels = 20;
                InstrumentSymbol = "MNQ";
            }
            else if (State == State.Configure)
            {
                // Setup output directory structure
                string baseFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "CG_L2_Capture"
                );

                if (!Directory.Exists(baseFolder))
                    Directory.CreateDirectory(baseFolder);

                outputFolder = baseFolder;
            }
            else if (State == State.DataLoaded)
            {
                // Subscribe to market depth
                if (BarsArray[0] != null && Instrument != null)
                {
                    // Verify instrument matches
                    if (Instrument.MasterInstrument.Name.Contains(InstrumentSymbol))
                    {
                        Print($"L2 Capture initialized for {Instrument.FullName}");
                        Print($"Output folder: {outputFolder}");
                        Print($"Chunk size: {MaxRowsPerChunk} rows");
                        Print($"Max Depth Levels: {MaxDepthLevels}");
                    }
                    else
                    {
                        Print($"WARNING: Instrument mismatch. Expected {InstrumentSymbol}, got {Instrument.MasterInstrument.Name}");
                    }
                }
            }
            else if (State == State.Terminated)
            {
                // Close any open chunk
                CloseCurrentChunk();

                if (totalRowsCaptured > 0)
                {
                    TimeSpan duration = DateTime.Now - captureStartTime;
                    Print($"=== L2 CAPTURE SESSION COMPLETE ===");
                    Print($"Total rows captured: {totalRowsCaptured:N0}");
                    Print($"Total chunks: {chunkIndex}");
                    Print($"Duration: {duration.TotalMinutes:F1} minutes");
                    Print($"Avg rows/second: {totalRowsCaptured / duration.TotalSeconds:F0}");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // Check if we need to start new session folder
            if (Time[0].Date != sessionDate)
            {
                CloseCurrentChunk();
                InitializeSessionFolder(Time[0].Date);
            }

            // Keep strategy alive during replay
            if (!isCapturing && State == State.Realtime)
            {
                isCapturing = true;
                captureStartTime = DateTime.Now;
                Print($"L2 capture started at {captureStartTime:yyyy-MM-dd HH:mm:ss}");
            }
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            // Only capture during configured depth levels
            if (e.Position >= MaxDepthLevels)
                return;

            // Ensure chunk is open
            if (currentChunk == null)
            {
                if (sessionFolder == null)
                    InitializeSessionFolder(Time[0].Date);
                OpenNewChunk();
            }

            // Write compact L2 record
            // Format: timestamp,side,operation,position,price,size
            // Side: B=Bid, A=Ask
            // Operation: A=Add, U=Update, R=Remove

            char side = e.MarketDataType == MarketDataType.Bid ? 'B' : 'A';
            char operation = GetOperationCode(e.Operation);

            string record = $"{e.Time:yyyy-MM-dd HH:mm:ss.fff},{side},{operation},{e.Position},{e.Price:F2},{e.Volume}";

            currentChunk.WriteLine(record);
            rowsInCurrentChunk++;
            totalRowsCaptured++;

            // Check if we need to rotate
            if (rowsInCurrentChunk >= MaxRowsPerChunk)
            {
                RotateChunk();
            }
        }

        #endregion

        #region Chunk Management

        private void InitializeSessionFolder(DateTime date)
        {
            sessionDate = date;
            string dateFolderName = date.ToString("yyyy-MM-dd");
            sessionFolder = Path.Combine(outputFolder, dateFolderName);

            if (!Directory.Exists(sessionFolder))
                Directory.CreateDirectory(sessionFolder);

            chunkIndex = 0;

            Print($"Session folder initialized: {sessionFolder}");
        }

        private void OpenNewChunk()
        {
            chunkIndex++;
            string chunkFileName = $"l2_chunk_{chunkIndex:D4}.csv";
            string chunkPath = Path.Combine(sessionFolder, chunkFileName);

            currentChunk = new StreamWriter(chunkPath, false, Encoding.UTF8, 65536); // 64KB buffer

            // Write header
            currentChunk.WriteLine("timestamp,side,operation,position,price,size");

            rowsInCurrentChunk = 0;

            Print($"Opened chunk {chunkIndex}: {chunkFileName}");
        }

        private void RotateChunk()
        {
            CloseCurrentChunk();
            OpenNewChunk();
        }

        private void CloseCurrentChunk()
        {
            if (currentChunk != null)
            {
                currentChunk.Flush();
                currentChunk.Close();
                currentChunk.Dispose();
                currentChunk = null;

                Print($"Closed chunk {chunkIndex} with {rowsInCurrentChunk:N0} rows");
            }
        }

        private char GetOperationCode(Operation op)
        {
            switch (op)
            {
                case Operation.Add: return 'A';
                case Operation.Update: return 'U';
                case Operation.Remove: return 'R';
                default: return '?';
            }
        }

        #endregion
    }
}
