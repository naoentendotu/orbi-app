namespace Orbi.Core;

public enum HoldAction { None, Open, Close }

/// <summary>Pure state machine: trigger key held alone for Delay opens; release (or any other key while open) closes.</summary>
public sealed class HoldDetector(TimeSpan delay)
{
    enum State { Idle, Pending, Tainted, Open }

    State _state;
    DateTime _downAt;

    public TimeSpan Delay { get; set; } = delay;
    public bool IsIdle => _state == State.Idle;
    public bool IsOpen => _state == State.Open;

    public HoldAction Down(DateTime now)
    {
        // Ignore auto-repeat: only the first down starts the hold.
        if (_state == State.Idle) { _state = State.Pending; _downAt = now; }
        return HoldAction.None;
    }

    public HoldAction OtherKey()
    {
        var was = _state;
        if (_state != State.Idle) _state = State.Tainted; // key+Tab, key+F4, shortcuts: never open
        return was == State.Open ? HoldAction.Close : HoldAction.None;
    }

    public HoldAction Tick(DateTime now)
    {
        if (_state != State.Pending || now - _downAt < Delay) return HoldAction.None;
        _state = State.Open;
        return HoldAction.Open;
    }

    public HoldAction Up()
    {
        var was = _state;
        _state = State.Idle;
        return was == State.Open ? HoldAction.Close : HoldAction.None;
    }
}
