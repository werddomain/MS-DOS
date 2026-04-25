// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MsDos.Core.Memory;

namespace MsDos.Core.Hardware;

/// <summary>
/// PC Speaker emulation — reads PIT Channel 2 frequency and port 0x61 gating.
/// The speaker is controlled by two mechanisms:
///   1) PIT Timer Channel 2 sets the tone frequency (reload value).
///   2) Port 0x61 bits 0-1 gate the speaker: bit 0 = PIT gate, bit 1 = speaker data.
/// When both bits are set, the speaker produces a square wave at the PIT channel 2 frequency.
/// </summary>
public sealed class PcSpeaker
{
    private readonly IOPortBus _ioBus;
    private byte _port61Value;
    private ushort _pitChannel2Reload = 0; // PIT reload value for channel 2
    private bool _speakerActive;

    /// <summary>
    /// Raised when speaker state changes. Parameters: (frequency in Hz, active).
    /// Frequency is 0 when speaker is off.
    /// </summary>
    public event Action<double, bool>? SpeakerStateChanged;

    /// <summary>Current tone frequency in Hz (1193182 / reload). 0 when off.</summary>
    public double CurrentFrequency { get; private set; }

    /// <summary>Whether the speaker is currently producing a tone.</summary>
    public bool IsActive => _speakerActive;

    public PcSpeaker(IOPortBus ioBus)
    {
        _ioBus = ioBus;
    }

    /// <summary>
    /// Register I/O port handlers. Port 0x61 is the system control port B.
    /// PIT channel 2 ports (0x42, 0x43) are already handled by PitController;
    /// this component observes the output via UpdatePitChannel2().
    /// </summary>
    public void RegisterPorts()
    {
        // Port 0x61: System Control Port B (keyboard controller / speaker gate)
        _ioBus.Register(0x61, port => ReadPort61(), (port, val) => WritePort61(val));
    }

    /// <summary>
    /// Called by PIT controller when channel 2 reload value changes.
    /// </summary>
    public void UpdatePitChannel2(ushort reloadValue)
    {
        _pitChannel2Reload = reloadValue;
        UpdateSpeakerState();
    }

    private byte ReadPort61()
    {
        // Bit 0: Timer 2 gate (speaker timer)
        // Bit 1: Speaker data enable
        // Bit 4: Toggles with refresh (RAM refresh indicator) — toggle for compatibility
        // Bit 5: Timer channel 2 output
        byte val = _port61Value;

        // Toggle refresh bit for programs that poll it
        _port61Value ^= 0x10;

        return val;
    }

    private void WritePort61(byte value)
    {
        _port61Value = (byte)((_port61Value & 0xFC) | (value & 0x03));
        UpdateSpeakerState();
    }

    private void UpdateSpeakerState()
    {
        bool gate = (_port61Value & 0x01) != 0;
        bool enable = (_port61Value & 0x02) != 0;
        bool active = gate && enable && _pitChannel2Reload > 0;

        double freq = 0;
        if (active && _pitChannel2Reload > 0)
        {
            freq = 1193182.0 / _pitChannel2Reload;
        }

        if (active != _speakerActive || (active && Math.Abs(freq - CurrentFrequency) > 0.1))
        {
            _speakerActive = active;
            CurrentFrequency = freq;
            SpeakerStateChanged?.Invoke(freq, active);
        }
        else
        {
            _speakerActive = active;
            CurrentFrequency = freq;
        }
    }
}
