using System.Text.Json;

namespace NINA.Headless.Services;

public class SequencerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private bool _isRunning;
    private string? _currentTarget;
    private double _progress;
    private readonly List<SequencePlanDto> _sequences = new();

    public event Action<SequencerStateDto>? StateChanged;

    public bool IsRunning => _isRunning;

    public string? CurrentTarget => _currentTarget;

    public double Progress => _progress;

    public bool LoadSequence(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var loaded = ParseSequencePlans(json);
            if (loaded.Count == 0)
            {
                return false;
            }

            lock (_lock)
            {
                _sequences.Clear();
                _sequences.AddRange(loaded);
                _isRunning = false;
                _progress = 0;
                _currentTarget = _sequences[0].Name;
                NotifyStateChanged();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return false;
            }

            _isRunning = true;

            var target = _sequences.FirstOrDefault();
            if (target != null)
            {
                _currentTarget = target.Name;
                target.Status = "Running";
            }

            NotifyStateChanged();
            return true;
        }
    }

    public bool Stop()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                return false;
            }

            _isRunning = false;
            _progress = 0;
            _currentTarget = null;

            foreach (var sequence in _sequences)
            {
                if (sequence.Status == "Running")
                {
                    sequence.Status = "Idle";
                }
            }

            NotifyStateChanged();
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _isRunning = false;
            _progress = 0;
            _currentTarget = null;

            foreach (var seq in _sequences)
            {
                seq.Status = "Idle";
                seq.TotalProgress = 0;

                foreach (var item in seq.Items)
                {
                    item.Status = "Idle";
                    item.Progress = 0;
                    item.CompletedCount = 0;
                }
            }

            NotifyStateChanged();
        }
    }

    public SequencerStateDto GetStatus()
    {
        lock (_lock)
        {
            return new SequencerStateDto
            {
                Running = _isRunning,
                Progress = _progress,
                CurrentTarget = _currentTarget,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    public List<SequencePlanDto> GetSequenceList()
    {
        lock (_lock)
        {
            return _sequences.Select(CloneSequence).ToList();
        }
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke(GetStatus());
    }

    private static List<SequencePlanDto> ParseSequencePlans(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return Normalize(JsonSerializer.Deserialize<List<SequencePlanDto>>(json, JsonOptions));
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetSequencesProperty(root, out var sequencesElement))
            {
                return Normalize(sequencesElement.Deserialize<List<SequencePlanDto>>(JsonOptions));
            }

            var single = JsonSerializer.Deserialize<SequencePlanDto>(json, JsonOptions);
            return Normalize(single == null ? null : new List<SequencePlanDto> { single });
        }

        return new List<SequencePlanDto>();
    }

    private static bool TryGetSequencesProperty(JsonElement root, out JsonElement sequencesElement)
    {
        if (root.TryGetProperty("Sequences", out sequencesElement))
        {
            return true;
        }

        if (root.TryGetProperty("sequences", out sequencesElement))
        {
            return true;
        }

        sequencesElement = default;
        return false;
    }

    private static List<SequencePlanDto> Normalize(List<SequencePlanDto>? sequences)
    {
        if (sequences == null)
        {
            return new List<SequencePlanDto>();
        }

        foreach (var sequence in sequences)
        {
            if (sequence.Id == Guid.Empty)
            {
                sequence.Id = Guid.NewGuid();
            }

            sequence.Name ??= string.Empty;
            sequence.Status = string.IsNullOrWhiteSpace(sequence.Status) ? "Idle" : sequence.Status;
            sequence.Items ??= new List<SequenceItemDto>();

            foreach (var item in sequence.Items)
            {
                if (item.Id == Guid.Empty)
                {
                    item.Id = Guid.NewGuid();
                }

                item.Name ??= string.Empty;
                item.Type = string.IsNullOrWhiteSpace(item.Type) ? "Other" : item.Type;
                item.Status = string.IsNullOrWhiteSpace(item.Status) ? "Idle" : item.Status;
                if (item.TotalCount <= 0)
                {
                    item.TotalCount = 1;
                }
            }
        }

        return sequences;
    }

    private static SequencePlanDto CloneSequence(SequencePlanDto sequence)
    {
        return new SequencePlanDto
        {
            Id = sequence.Id,
            Name = sequence.Name,
            Status = sequence.Status,
            StartTime = sequence.StartTime,
            EstimatedEndTime = sequence.EstimatedEndTime,
            TotalProgress = sequence.TotalProgress,
            Items = sequence.Items.Select(item => new SequenceItemDto
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.Type,
                Status = item.Status,
                Progress = item.Progress,
                TotalCount = item.TotalCount,
                CompletedCount = item.CompletedCount,
                ExposureTime = item.ExposureTime,
                Filter = item.Filter
            }).ToList()
        };
    }
}

public class SequencerStateDto
{
    public bool Running { get; set; }

    public double Progress { get; set; }

    public string? CurrentTarget { get; set; }

    public DateTime Timestamp { get; set; }
}

public class SequencePlanDto
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<SequenceItemDto> Items { get; set; } = new();

    public string Status { get; set; } = "Idle";

    public DateTime? StartTime { get; set; }

    public DateTime? EstimatedEndTime { get; set; }

    public double TotalProgress { get; set; }
}

public class SequenceItemDto
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "Other";

    public string Status { get; set; } = "Idle";

    public double Progress { get; set; }

    public int TotalCount { get; set; } = 1;

    public int CompletedCount { get; set; }

    public double? ExposureTime { get; set; }

    public string? Filter { get; set; }
}
