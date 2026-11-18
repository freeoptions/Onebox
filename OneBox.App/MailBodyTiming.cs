using System.Diagnostics;

namespace OneBox.App;

internal sealed class MailBodyTiming
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<string> _stageList = [];
    private string _stage = "prepare_reader";
    private long _stageStartMs;
    private bool _finished;

    public void StartStage(string stage)
    {
        lock (_gate)
        {
            if (_finished) return;
            var elapsedMs = _clock.ElapsedMilliseconds;
            RecordStage(elapsedMs);
            _stage = stage;
            _stageStartMs = elapsedMs;
        }
    }

    public string Finish(string provider, bool cacheHit, string outcome, string source = "reader")
    {
        lock (_gate)
        {
            _finished = true;
            var elapsedMs = _clock.ElapsedMilliseconds;
            RecordStage(elapsedMs);
            return $"provider={provider} cache_hit={cacheHit} outcome={outcome} total_ms={elapsedMs} stages_ms={string.Join(",", _stageList)} source={source}";
        }
    }

    private void RecordStage(long elapsedMs) => _stageList.Add($"{_stage}:{elapsedMs - _stageStartMs}");
}
