using System.Diagnostics;
using System.Threading.Channels;
using KeyAsio.Configuration;
using KeyAsio.Configuration.Models;
using KeyAsio.Core.Audio;
using KeyAsio.Plugins.Contracts.Sync;
using KeyAsio.Sync.Sources;
using Microsoft.Extensions.Logging;
using OverlayAPI.LazerProtocol;

namespace KeyAsio.Sync.Services;

public sealed class NativeSamplePlaybackService : IDisposable
{
    private readonly LazerIpcBridge _bridge;
    private readonly SyncSessionContext _context;
    private readonly GameplayAudioService _audio;
    private readonly SfxPlaybackService _playback;
    private readonly IPlaybackEngine _engine;
    private readonly AppSettings _settings;
    private readonly ILogger<NativeSamplePlaybackService> _logger;
    private readonly Channel<PendingSamples> _queue = Channel.CreateBounded<PendingSamples>(256);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task _worker;
    private Guid _session;
    private long _sequence = -1;
    private long _generation;
    private int _processId;
    private bool _connected;
    private bool _active;
    private string _beatmap = string.Empty;

    public NativeSamplePlaybackService(LazerIpcBridge bridge, SyncSessionContext context,
        GameplayAudioService audio, SfxPlaybackService playback, IPlaybackEngine engine,
        AppSettings settings, ILogger<NativeSamplePlaybackService> logger)
    {
        _bridge = bridge;
        _context = context;
        _audio = audio;
        _playback = playback;
        _engine = engine;
        _settings = settings;
        _logger = logger;
        _bridge.SamplesReceived += OnSamples;
        _bridge.ChannelConnectionChanged += OnConnectionChanged;
        _worker = Task.Run(ProcessAsync);
    }

    // Once a producer is recognised, a lost sample pipe stays silent instead of falling back
    // to speculative hitsounds for the same lazer process.
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
                return _context.ClientType == GameClientType.Lazer &&
                       _processId > 0 && _context.ProcessId == _processId;
        }
    }

    private void OnConnectionChanged(LazerIpcChannel channel, bool oldValue, bool newValue)
    {
        if (channel != LazerIpcChannel.Samples || newValue) return;
        lock (_gate)
        {
            _connected = false;
            _active = false;
            _generation++;
        }
    }

    private void OnSamples(LazerSampleFrame frame)
    {
        lock (_gate)
        {
            if (frame.Version != LazerSampleProtocol.Version ||
                _context.ClientType != GameClientType.Lazer || _context.ProcessId != frame.ProcessId)
                return;
            if (_processId == frame.ProcessId && frame.Sequence <= _sequence) return;
            if (_session != frame.SessionId || _processId != frame.ProcessId)
            {
                _generation++;
                _session = frame.SessionId;
                _logger.LogInformation("Native lazer sample session {Session} (PID {ProcessId}).",
                    frame.SessionId, frame.ProcessId);
            }

            _sequence = frame.Sequence;
            if (_active != frame.Active || _beatmap != frame.BeatmapFilename)
                _generation++;
            _processId = frame.ProcessId;
            _connected = true;
            _active = frame.Active;
            _beatmap = frame.BeatmapFilename;

            if (!frame.Active || frame.Samples.Length == 0 || !CanPlay()) return;
            if (!_queue.Writer.TryWrite(new PendingSamples(frame, _generation, Stopwatch.GetTimestamp())))
            {
                // Invalidate queued and in-flight loads; do not play a delayed burst after overflow.
                _generation++;
                _logger.LogWarning("Native sample queue overflow; dropping pending sounds.");
            }
        }
    }

    private bool CanPlay()
        => _connected && _active && _settings.Sync.EnableSync && _context.IsStarted &&
           _engine.CurrentDevice != null && _context.ClientType == GameClientType.Lazer &&
           _context.ProcessId == _processId && _context.OsuStatus == OsuMemoryStatus.Playing &&
           string.Equals(_context.Beatmap.Filename, _beatmap, StringComparison.OrdinalIgnoreCase);

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    foreach (var sample in pending.Frame.Samples)
                    {
                        lock (_gate)
                        {
                            if (!IsCurrent(pending)) break;
                        }

                        var audio = await _audio.ResolveNativeSampleAsync(sample);
                        lock (_gate)
                        {
                            if (!IsCurrent(pending)) break;
                            if (audio != null)
                                _playback.PlayEffectsAudio(audio, sample.Volume / 100f, 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve native lazer samples.");
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private bool IsCurrent(PendingSamples pending)
        => pending.Generation == _generation && CanPlay() &&
           Stopwatch.GetElapsedTime(pending.ReceivedAt).TotalMilliseconds < 250 &&
           Math.Abs(_context.PlayTime - pending.Frame.PlayTime) < 1000;

    public void Dispose()
    {
        _bridge.SamplesReceived -= OnSamples;
        _bridge.ChannelConnectionChanged -= OnConnectionChanged;
        lock (_gate)
        {
            _connected = false;
            _generation++;
        }
        _queue.Writer.TryComplete();
        _cts.Cancel();
        _worker.GetAwaiter().GetResult();
        _cts.Dispose();
    }

    private sealed record PendingSamples(LazerSampleFrame Frame, long Generation, long ReceivedAt);
}
