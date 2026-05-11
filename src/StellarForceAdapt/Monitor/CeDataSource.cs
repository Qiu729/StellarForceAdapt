using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace StellarForceAdapt.Monitor;

/// <summary>
/// Reads game state from Cheat Engine's binary output file.
/// CE Lua script writes 36-byte packets at ~200Hz; we poll and expose the latest.
/// </summary>
public class CeDataSource : IDisposable
{
    private readonly string _stateFile;
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private int _lastStamp = -1;
    private DateTime _lastFresh = DateTime.MinValue;

    /// <summary>Fires when a fresh CE state packet is parsed successfully.</summary>
    public event EventHandler<CeGameState>? StateReceived;

    /// <summary>True when CE data has been received within the last 200ms.</summary>
    public bool IsConnected =>
        (DateTime.UtcNow - _lastFresh).TotalMilliseconds < 200;

    public CeDataSource()
    {
        // Use %ProgramData% for locale-independent, non-C-drive-safe path
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "StellarForceAdapt");
        Directory.CreateDirectory(dir);
        _stateFile = Path.Combine(dir, "ce_state.bin");
    }

    public void Start()
    {
        if (_pollThread != null) return;
        _cts = new CancellationTokenSource();
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "CE-DataSource"
        };
        _pollThread.Start();
        Debug.WriteLine("[CE] Polling started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pollThread = null;
        _lastStamp = -1;
        Debug.WriteLine("[CE] Polling stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private void PollLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var buffer = new byte[64]; // generous size for future fields

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (TryRead(buffer, out var state))
                {
                    _lastFresh = DateTime.UtcNow;
                    StateReceived?.Invoke(this, state);
                }
            }
            catch { /* Next poll will retry */ }

            token.WaitHandle.WaitOne(5);
        }
    }

    private bool TryRead(byte[] buffer, out CeGameState state)
    {
        state = default;
        try
        {
            using var fs = new FileStream(_stateFile, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64,
                FileOptions.SequentialScan);
            int read = fs.Read(buffer, 0, 36);
            if (read < 36) return false;

            int stamp = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(32));
            if (stamp == _lastStamp) return false; // no new data
            _lastStamp = stamp;

            state = new CeGameState
            {
                Health        = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(0)),
                MaxHealth     = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(4)),
                BetaEnergy    = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(8)),
                MaxBetaEnergy = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(12)),
                BurstEnergy   = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(16)),
                MaxBurstEnergy= BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(20)),
                TachyEnergy   = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(24)),
                MaxTachyEnergy= BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(28)),
            };
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

/// <summary>
/// Game state values read from Cheat Engine (via NidasBot pointer chain).
/// </summary>
public struct CeGameState
{
    public float Health;
    public float MaxHealth;
    public float BetaEnergy;
    public float MaxBetaEnergy;
    public float BurstEnergy;
    public float MaxBurstEnergy;
    public float TachyEnergy;
    public float MaxTachyEnergy;

    // Derived properties
    public readonly float HealthPercent => MaxHealth > 0 ? Health / MaxHealth : 1f;
    public readonly bool TachyModeActive => TachyEnergy > 0;
    public readonly bool BetaSkillAvailable => BetaEnergy > 0;
    public readonly bool BurstSkillAvailable => BurstEnergy > 0;
}
