namespace SarnautCore.NativeHud;

/// <summary>Bounded session adapter for tests, tools, and offline HUD playback.</summary>
public sealed class InMemoryHudSession : IHudSession
{
    private readonly HudEvent[] _events;
    private readonly HudCommand[] _commands;
    private int _eventHead;
    private int _eventCount;
    private int _commandHead;
    private int _commandCount;
    private int _droppedEvents;

    public InMemoryHudSession(
        int eventCapacity = 256,
        int commandCapacity = 128,
        HudSessionCapabilities? capabilities = null)
    {
        if (eventCapacity <= 0 || commandCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventCapacity));
        }

        _events = new HudEvent[eventCapacity];
        _commands = new HudCommand[commandCapacity];
        Capabilities = capabilities ?? new HudSessionCapabilities(HudEventFamilies.All, HudCommandFamilies.All);
    }

    public HudSessionCapabilities Capabilities { get; }

    public HudSessionState State { get; private set; } = HudSessionState.Open;

    public int PendingEvents => _eventCount;

    public int PendingCommands => _commandCount;

    public bool TryQueue(in HudEvent item)
    {
        if (State != HudSessionState.Open || _eventCount == _events.Length)
        {
            _droppedEvents++;
            State = HudSessionState.Faulted;
            return false;
        }

        int tail = (_eventHead + _eventCount) % _events.Length;
        _events[tail] = item;
        _eventCount++;
        return true;
    }

    public HudSessionRead Read(Span<HudEvent> destination)
    {
        int count = Math.Min(destination.Length, _eventCount);
        for (int index = 0; index < count; index++)
        {
            destination[index] = _events[_eventHead];
            _eventHead = (_eventHead + 1) % _events.Length;
            _eventCount--;
        }

        int dropped = _droppedEvents;
        _droppedEvents = 0;
        return new HudSessionRead(count, dropped, State);
    }

    public bool TryWrite(in HudCommand command)
    {
        if (State != HudSessionState.Open || _commandCount == _commands.Length)
        {
            return false;
        }

        int tail = (_commandHead + _commandCount) % _commands.Length;
        _commands[tail] = command;
        _commandCount++;
        return true;
    }

    public bool TryReadCommand(out HudCommand command)
    {
        if (_commandCount == 0)
        {
            command = default;
            return false;
        }

        command = _commands[_commandHead];
        _commandHead = (_commandHead + 1) % _commands.Length;
        _commandCount--;
        return true;
    }

    public void Close(bool faulted = false) => State = faulted ? HudSessionState.Faulted : HudSessionState.Closed;
}

/// <summary>Fixed-capacity world projection adapter for deterministic tests and playback.</summary>
public sealed class InMemoryHudWorld : IHudWorld
{
    private readonly WorldEntry[] _entries;

    public InMemoryHudWorld(int capacity = 128)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _entries = new WorldEntry[capacity];
    }

    public void SetProjection(ulong entityId, HudProjection projection)
    {
        if (entityId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityId));
        }

        int free = -1;
        for (int index = 0; index < _entries.Length; index++)
        {
            if (_entries[index].Occupied && _entries[index].EntityId == entityId)
            {
                _entries[index].Projection = projection;
                return;
            }

            if (!_entries[index].Occupied && free < 0)
            {
                free = index;
            }
        }

        if (free < 0)
        {
            throw new InvalidOperationException("In-memory HUD world capacity exceeded.");
        }

        _entries[free] = new WorldEntry { Occupied = true, EntityId = entityId, Projection = projection };
    }

    public bool Remove(ulong entityId)
    {
        for (int index = 0; index < _entries.Length; index++)
        {
            if (_entries[index].Occupied && _entries[index].EntityId == entityId)
            {
                _entries[index] = default;
                return true;
            }
        }

        return false;
    }

    public bool TryProject(in HudWorldQuery query, out HudProjection projection)
    {
        for (int index = 0; index < _entries.Length; index++)
        {
            if (_entries[index].Occupied && _entries[index].EntityId == query.EntityId)
            {
                projection = _entries[index].Projection;
                return true;
            }
        }

        projection = default;
        return false;
    }

    private struct WorldEntry
    {
        public bool Occupied;
        public ulong EntityId;
        public HudProjection Projection;
    }
}
