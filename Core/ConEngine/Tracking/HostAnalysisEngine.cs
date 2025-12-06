using ConCore;
using System;
using System.Collections.Generic;
using System.Net;
using System.Reactive.Subjects;
using System.Text;
using ConEngine.Configuration;

namespace ConEngine.Tracking;

internal class HostAnalysisEngine
{
    public HostAnalysisEngine(HostTrackingOptions trackingOptions, IPAddress hostAddress)
    {
        _trackingOptions = trackingOptions;
        _hostAddress = hostAddress;
    }

    private readonly HostTrackingOptions _trackingOptions;
    private readonly IPAddress _hostAddress;

    public HostState CurrentState { get; private set; } = HostState.Initializing;
    public HostState PreviousState { get; private set; } = HostState.Initializing;

    public DateTimeOffset TrackingStarted { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }

    // Measured values

    public int SampleSize { get; private set; } = 0;
    public int NumSuccess { get; private set; } = 0;
    public int NumFailed { get; private set; } = 0;

    private float _sumRoundtripTime = 0.0f;

    // Calculated values
    public float SuccessRate { get; private set; } = 0.0f;
    public float FailureRate { get; private set; } = 0.0f;
    public float AverageRoundtripTime { get; private set; } = 0.0f;

    // Observable host status
    private readonly ReplaySubject<HostStatus> _hostStatusSubject = new(bufferSize: 1);
    public IObservable<HostStatus> HostStatusSource => _hostStatusSubject;
    private readonly ReplaySubject<HostStatus> _hostStateChangeSubject = new(bufferSize: 1);
    public IObservable<HostStatus> HostStateChangeSource => _hostStateChangeSubject;


    public void Recalculate(IEnumerable<PingHistoryEntry> samples)
    {
        // Zero out measured values
        SampleSize = 0;
        NumSuccess = 0;
        NumFailed = 0;

        _sumRoundtripTime = 0.0f;
        foreach(PingHistoryEntry item in samples)
        {
            // We're getting data now.
            if (CurrentState== HostState.Initializing)
            {
                TrackingStarted = item.Timestamp;
                CurrentState = HostState.Starting;
            }

            if (item.Timestamp > LastUpdated)
            {
                LastUpdated = item.Timestamp;
            }

            // Accumulate stats
            SampleSize++;
            if(item.Success)
                NumSuccess++;
            else
                NumFailed++;
            _sumRoundtripTime += item.RoundtripTime;
        }

        // Calculate derived values
        SuccessRate = SampleSize > 0 ? (float)NumSuccess / (float)SampleSize : 0.0f;
        FailureRate = SampleSize > 0 ? (float)NumFailed / (float)SampleSize : 0.0f;
        AverageRoundtripTime = SampleSize > 0 ? (float)_sumRoundtripTime / (float)SampleSize : 0.0f;

        // Determine current state
        switch (CurrentState)
        {
            case HostState.Initializing:
                // Do nothing, wait for data
                break;

            case HostState.Starting:
                if (SampleSize >= _trackingOptions.MinUsableSample)
                {
                    if(SuccessRate >= _trackingOptions.RespondingThreshold)
                    {
                        CurrentState = HostState.Responding;
                    }
                }
                break;

            case HostState.Responding:
                if (SuccessRate < _trackingOptions.FalteringThreshold)
                {
                    CurrentState = HostState.Faltering;
                }
                break;

            case HostState.Faltering:
                if(SuccessRate >= _trackingOptions.RespondingThreshold)
                {
                    CurrentState = HostState.Responding;
                }

                if (SuccessRate < _trackingOptions.DownThreshold)
                {
                    CurrentState = HostState.Down;
                }

                break;

            case HostState.Down:
                if (SuccessRate >= _trackingOptions.RecoveringThreshold)
                {
                    CurrentState = HostState.Recovering;
                }
                break;

            case HostState.Recovering:
                if(SuccessRate >= _trackingOptions.RespondingThreshold)
                {
                    CurrentState = HostState.Responding;
                }

                if(SuccessRate < _trackingOptions.DownThreshold)
                {
                    CurrentState = HostState.Down;
                }
                break;
        }

        // Notify observers of status change
        HostStatus newStatus = new() {
            HostAddress = _hostAddress,
            State = CurrentState,
            TrackingStarted = TrackingStarted,
            SampleSize = SampleSize,
            SuccessRate = SuccessRate,
            FailureRate = FailureRate,
            AverageRoundtripTime = AverageRoundtripTime,
            Timestamp = LastUpdated
        };
        _hostStatusSubject.OnNext(newStatus);
        if (CurrentState != PreviousState)
        {
            _hostStateChangeSubject.OnNext(newStatus);
        }
        PreviousState = CurrentState;
    }
}