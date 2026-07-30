/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Reconstructs the logical 100 Hz DualSense microphone clock without
    /// treating bursty Bluetooth input-report arrivals as presentation ticks.
    /// Each 16-report block contributes only its earliest clock intercept,
    /// making the fitted epoch insensitive to delayed/batched reports. Output
    /// uses fifteen uniformly-spaced deadlines across each sixteen-tick block,
    /// avoiding a periodic 20 ms omission while remaining phase-locked.
    /// </summary>
    public sealed class DualSenseMicrophonePresentationClock
    {
        internal const int SequenceBlockSize = 16;
        internal const int SpeakerFramesPerSequenceBlock = 15;
        internal const int HistoryCapacity = 32;
        internal const int RequiredReliableBlocks = 4;
        internal const double NominalPeriodSeconds = 0.010;
        internal const double DefaultTargetPhaseMicroseconds = 8_100.0;
        internal const double MicrophonePeriodsPerSpeakerFrame =
            (double)SequenceBlockSize / SpeakerFramesPerSequenceBlock;
        internal const double FirstPresentationMicrophoneCoordinate = 1.0;

        private const int MinimumObservationsPerReliableBlock = 8;
        private const int MaximumForwardSequenceDelta = 64;
        private const int MinimumBlocksForPeriodFit = 8;
        private const double MaximumPeriodError = 0.005;
        private const double PeriodBlend = 0.25;
        private const double MaximumPeriodSlewPpmPerBlock = 25.0;
        private const double MaximumPhaseSlewMicrosecondsPerBlock = 20.0;
        private const double LateAnchorToleranceMilliseconds = 1.5;
        private const double MaximumObservationGapSeconds = 2.0;

        private readonly object sync = new object();
        private readonly long clockFrequency;
        private readonly double nominalPeriodTicks;
        private readonly double targetPhaseTicks;
        private readonly double maximumPhaseSlewTicks;
        private readonly double lateAnchorToleranceTicks;
        private readonly long maximumObservationGapTicks;
        private readonly BlockAnchor[] history =
            new BlockAnchor[HistoryCapacity];
        private readonly BlockAnchor[] orderedAnchors =
            new BlockAnchor[HistoryCapacity];
        private readonly double[] slopeScratch =
            new double[HistoryCapacity * (HistoryCapacity - 1) / 2];
        private readonly double[] residualScratch =
            new double[HistoryCapacity];
        private readonly bool[] acceptedScratch = new bool[HistoryCapacity];

        private bool initialized;
        private byte previousRawSequence;
        private long latestLogicalSequence;
        private long previousArrivalQpc;
        private long currentBlockIndex;
        private int currentBlockObservations;
        private int currentBlockFirstSlot;
        private int currentBlockLastSlot;
        private long currentBlockMinimumSequence;
        private long currentBlockMinimumArrivalQpc;
        private double currentBlockMinimumNominalIntercept;
        private int historyHead;
        private int historyCount;
        private int reliableBlockCount;
        private int acceptedAnchorCount;
        private bool locked;
        private long modelReferenceSequence;
        private double modelLowerEnvelopeAtReferenceQpc;
        private double modelPeriodTicks;
        private bool nextSlotInitialized;
        private long nextPresentationIndex;
        private int generation;

        public DualSenseMicrophonePresentationClock(
            double targetPhaseMicroseconds =
                DefaultTargetPhaseMicroseconds)
            : this(Stopwatch.Frequency, targetPhaseMicroseconds)
        {
        }

        internal DualSenseMicrophonePresentationClock(long clockFrequency,
            double targetPhaseMicroseconds =
                DefaultTargetPhaseMicroseconds)
        {
            if (clockFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clockFrequency));
            }

            if (!double.IsFinite(targetPhaseMicroseconds) ||
                targetPhaseMicroseconds < 0.0 ||
                targetPhaseMicroseconds >= 20_000.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetPhaseMicroseconds));
            }

            this.clockFrequency = clockFrequency;
            nominalPeriodTicks = clockFrequency * NominalPeriodSeconds;
            targetPhaseTicks = clockFrequency *
                targetPhaseMicroseconds / 1_000_000.0;
            maximumPhaseSlewTicks = clockFrequency *
                MaximumPhaseSlewMicrosecondsPerBlock / 1_000_000.0;
            lateAnchorToleranceTicks = clockFrequency *
                LateAnchorToleranceMilliseconds / 1_000.0;
            maximumObservationGapTicks = Math.Max(1,
                (long)Math.Round(clockFrequency *
                    MaximumObservationGapSeconds));
            modelPeriodTicks = nominalPeriodTicks;
        }

        public bool IsLocked
        {
            get
            {
                lock (sync)
                {
                    return locked;
                }
            }
        }

        public int Generation
        {
            get
            {
                lock (sync)
                {
                    return generation;
                }
            }
        }

        /// <summary>
        /// Adds one microphone input report. Duplicate sequence values are
        /// ignored. A backward host timestamp, an ambiguous sequence jump, or
        /// a long input outage begins a fresh acquisition window.
        /// </summary>
        /// <returns>True when a new logical sequence was accepted.</returns>
        public bool Observe(byte sequence, long arrivalQpc)
        {
            if (arrivalQpc <= 0)
            {
                return false;
            }

            lock (sync)
            {
                if (!initialized)
                {
                    Initialize(sequence, arrivalQpc);
                    return true;
                }

                int sequenceDelta = unchecked((byte)(sequence -
                    previousRawSequence));
                if (sequenceDelta == 0)
                {
                    return false;
                }

                bool invalidArrival = arrivalQpc < previousArrivalQpc ||
                    arrivalQpc - previousArrivalQpc >
                        maximumObservationGapTicks;
                bool ambiguousSequence = sequenceDelta >
                    MaximumForwardSequenceDelta;
                if (invalidArrival || ambiguousSequence)
                {
                    ResetCore();
                    Initialize(sequence, arrivalQpc);
                    return true;
                }

                latestLogicalSequence += sequenceDelta;
                previousRawSequence = sequence;
                previousArrivalQpc = arrivalQpc;
                AddBlockObservation(latestLogicalSequence, arrivalQpc);
                return true;
            }
        }

        /// <summary>
        /// Gets the current active rational presentation slot. On first use
        /// after lock, the cursor starts at the earliest uniform 93.75 Hz
        /// deadline that has not already elapsed. Subsequent calls return the
        /// same slot until <see cref="Advance"/> is called.
        /// </summary>
        public bool TryGetNextSlot(long nowQpc, out long logicalSequence,
            out long deadlineQpc)
        {
            lock (sync)
            {
                logicalSequence = 0;
                deadlineQpc = 0;
                if (!locked)
                {
                    return false;
                }

                if (nowQpc < previousArrivalQpc ||
                    nowQpc - previousArrivalQpc >
                        maximumObservationGapTicks)
                {
                    // Observe cannot notice an input clock that simply stops.
                    // Fail open to the caller's absolute scheduler instead of
                    // extrapolating a dead controller clock indefinitely.
                    ResetCore();
                    return false;
                }

                if (!nextSlotInitialized)
                {
                    nextPresentationIndex =
                        FirstPresentationIndexAtOrAfter(nowQpc);
                    nextSlotInitialized = true;
                }
                else
                {
                    long currentDeadline = PredictDeadline(
                        nextPresentationIndex);
                    if (currentDeadline < nowQpc)
                    {
                        // Any spent interval must not become a catch-up burst.
                        // Preserve controller phase and resume at the first
                        // future point on the same rational lattice.
                        nextPresentationIndex =
                            FirstPresentationIndexAtOrAfter(nowQpc);
                    }
                }

                logicalSequence = nextPresentationIndex;
                deadlineQpc = PredictDeadline(nextPresentationIndex);
                return true;
            }
        }

        /// <summary>
        /// Advances the owned presentation cursor after the caller has sent or
        /// deliberately spent the supplied slot. Stale completions cannot move
        /// the cursor backward.
        /// </summary>
        public void Advance(long logicalSequence)
        {
            lock (sync)
            {
                if (!locked)
                {
                    return;
                }

                long candidate = logicalSequence + 1;
                if (!nextSlotInitialized ||
                    candidate > nextPresentationIndex)
                {
                    nextPresentationIndex = candidate;
                    nextSlotInitialized = true;
                }
            }
        }

        public bool TryGetPresentationDeadline(long logicalSequence,
            out long deadlineQpc)
        {
            lock (sync)
            {
                if (!locked)
                {
                    deadlineQpc = 0;
                    return false;
                }

                deadlineQpc = PredictDeadline(logicalSequence);
                return true;
            }
        }

        public ClockSnapshot GetSnapshot()
        {
            lock (sync)
            {
                return new ClockSnapshot(locked, generation,
                    latestLogicalSequence, reliableBlockCount,
                    acceptedAnchorCount, modelReferenceSequence,
                    modelLowerEnvelopeAtReferenceQpc,
                    modelPeriodTicks, targetPhaseTicks, clockFrequency);
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                ResetCore();
            }
        }

        public static bool IsScheduledPresentationSlot(long logicalSequence)
        {
            // Retained for source compatibility with the former 15-of-16
            // cursor. Rational presentation indices have no omitted phase.
            _ = logicalSequence;
            return true;
        }

        private void Initialize(byte sequence, long arrivalQpc)
        {
            initialized = true;
            previousRawSequence = sequence;
            latestLogicalSequence = sequence;
            previousArrivalQpc = arrivalQpc;
            StartBlock(latestLogicalSequence, arrivalQpc);
        }

        private void AddBlockObservation(long logicalSequence,
            long arrivalQpc)
        {
            long blockIndex = logicalSequence / SequenceBlockSize;
            if (blockIndex != currentBlockIndex)
            {
                CompleteCurrentBlock();
                StartBlock(logicalSequence, arrivalQpc);
                return;
            }

            AddToCurrentBlock(logicalSequence, arrivalQpc);
        }

        private void StartBlock(long logicalSequence, long arrivalQpc)
        {
            currentBlockIndex = logicalSequence / SequenceBlockSize;
            currentBlockObservations = 0;
            currentBlockFirstSlot = SequenceBlockSize;
            currentBlockLastSlot = -1;
            currentBlockMinimumSequence = logicalSequence;
            currentBlockMinimumArrivalQpc = arrivalQpc;
            currentBlockMinimumNominalIntercept = double.PositiveInfinity;
            AddToCurrentBlock(logicalSequence, arrivalQpc);
        }

        private void AddToCurrentBlock(long logicalSequence, long arrivalQpc)
        {
            int slot = (int)(logicalSequence &
                (SequenceBlockSize - 1));
            currentBlockObservations++;
            currentBlockFirstSlot = Math.Min(currentBlockFirstSlot, slot);
            currentBlockLastSlot = Math.Max(currentBlockLastSlot, slot);

            double nominalIntercept = arrivalQpc -
                nominalPeriodTicks * logicalSequence;
            if (nominalIntercept < currentBlockMinimumNominalIntercept)
            {
                currentBlockMinimumNominalIntercept = nominalIntercept;
                currentBlockMinimumSequence = logicalSequence;
                currentBlockMinimumArrivalQpc = arrivalQpc;
            }
        }

        private void CompleteCurrentBlock()
        {
            bool reliable = currentBlockObservations >=
                    MinimumObservationsPerReliableBlock &&
                currentBlockFirstSlot <= 3 &&
                currentBlockLastSlot >= 12;
            if (!reliable)
            {
                return;
            }

            history[historyHead] = new BlockAnchor(
                currentBlockMinimumSequence,
                currentBlockMinimumArrivalQpc);
            historyHead = (historyHead + 1) % HistoryCapacity;
            historyCount = Math.Min(historyCount + 1, HistoryCapacity);
            reliableBlockCount++;
            UpdateModel();
        }

        private void UpdateModel()
        {
            int oldest = (historyHead - historyCount +
                HistoryCapacity) % HistoryCapacity;
            for (int offset = 0; offset < historyCount; offset++)
            {
                orderedAnchors[offset] = history[(oldest + offset) %
                    HistoryCapacity];
            }

            double candidatePeriod = EstimateRobustPeriod(historyCount);
            if (historyCount < MinimumBlocksForPeriodFit)
            {
                candidatePeriod = nominalPeriodTicks;
            }

            long referenceSequence =
                orderedAnchors[historyCount - 1].LogicalSequence;
            int accepted = SelectLowerEnvelopeAnchors(historyCount,
                referenceSequence, candidatePeriod);
            if (accepted < RequiredReliableBlocks)
            {
                acceptedAnchorCount = accepted;
                return;
            }

            if (historyCount >= MinimumBlocksForPeriodFit)
            {
                candidatePeriod = FitAcceptedPeriod(historyCount,
                    referenceSequence, candidatePeriod);
                accepted = SelectLowerEnvelopeAnchors(historyCount,
                    referenceSequence, candidatePeriod);
                if (accepted < RequiredReliableBlocks)
                {
                    acceptedAnchorCount = accepted;
                    return;
                }
            }

            acceptedAnchorCount = accepted;
            candidatePeriod = Math.Clamp(candidatePeriod,
                nominalPeriodTicks * (1.0 - MaximumPeriodError),
                nominalPeriodTicks * (1.0 + MaximumPeriodError));

            if (!locked)
            {
                modelPeriodTicks = candidatePeriod;
                modelReferenceSequence = referenceSequence;
                modelLowerEnvelopeAtReferenceQpc =
                    FitArrivalAtReference(historyCount,
                        referenceSequence, modelPeriodTicks);
                locked = true;
                nextSlotInitialized = false;
                return;
            }

            double blendedPeriod = modelPeriodTicks +
                (candidatePeriod - modelPeriodTicks) * PeriodBlend;
            double maximumPeriodStep = nominalPeriodTicks *
                MaximumPeriodSlewPpmPerBlock / 1_000_000.0;
            blendedPeriod = Math.Clamp(blendedPeriod,
                modelPeriodTicks - maximumPeriodStep,
                modelPeriodTicks + maximumPeriodStep);
            blendedPeriod = Math.Clamp(blendedPeriod,
                nominalPeriodTicks * (1.0 - MaximumPeriodError),
                nominalPeriodTicks * (1.0 + MaximumPeriodError));

            double currentAtReference =
                modelLowerEnvelopeAtReferenceQpc +
                modelPeriodTicks *
                    (referenceSequence - modelReferenceSequence);
            double candidateAtReference = FitArrivalAtReference(
                historyCount, referenceSequence, blendedPeriod);
            double phaseStep = Math.Clamp(
                candidateAtReference - currentAtReference,
                -maximumPhaseSlewTicks, maximumPhaseSlewTicks);

            modelReferenceSequence = referenceSequence;
            modelLowerEnvelopeAtReferenceQpc =
                currentAtReference + phaseStep;
            modelPeriodTicks = blendedPeriod;
        }

        private double EstimateRobustPeriod(int count)
        {
            int slopeCount = 0;
            for (int first = 0; first < count - 1; first++)
            {
                for (int second = first + 1; second < count; second++)
                {
                    long sequenceDelta =
                        orderedAnchors[second].LogicalSequence -
                        orderedAnchors[first].LogicalSequence;
                    if (sequenceDelta <= 0)
                    {
                        continue;
                    }

                    double slope =
                        (orderedAnchors[second].ArrivalQpc -
                            orderedAnchors[first].ArrivalQpc) /
                        sequenceDelta;
                    if (double.IsFinite(slope))
                    {
                        slopeScratch[slopeCount++] = slope;
                    }
                }
            }

            if (slopeCount == 0)
            {
                return nominalPeriodTicks;
            }

            Array.Sort(slopeScratch, 0, slopeCount);
            int middle = slopeCount / 2;
            double median = (slopeCount & 1) != 0 ?
                slopeScratch[middle] :
                (slopeScratch[middle - 1] + slopeScratch[middle]) * 0.5;
            return Math.Clamp(median,
                nominalPeriodTicks * (1.0 - MaximumPeriodError),
                nominalPeriodTicks * (1.0 + MaximumPeriodError));
        }

        private int SelectLowerEnvelopeAnchors(int count,
            long referenceSequence, double periodTicks)
        {
            for (int index = 0; index < count; index++)
            {
                residualScratch[index] =
                    orderedAnchors[index].ArrivalQpc -
                    periodTicks * (orderedAnchors[index].LogicalSequence -
                        referenceSequence);
            }

            Array.Sort(residualScratch, 0, count);
            int middle = count / 2;
            double median = (count & 1) != 0 ?
                residualScratch[middle] :
                (residualScratch[middle - 1] +
                    residualScratch[middle]) * 0.5;
            double lateCutoff = median + lateAnchorToleranceTicks;
            int accepted = 0;
            for (int index = 0; index < count; index++)
            {
                double residual = orderedAnchors[index].ArrivalQpc -
                    periodTicks * (orderedAnchors[index].LogicalSequence -
                        referenceSequence);
                bool include = residual <= lateCutoff;
                acceptedScratch[index] = include;
                if (include)
                {
                    accepted++;
                }
            }

            return accepted;
        }

        private double FitAcceptedPeriod(int count,
            long referenceSequence, double fallback)
        {
            int accepted = 0;
            double sumX = 0.0;
            double sumY = 0.0;
            double baseArrival =
                orderedAnchors[count - 1].ArrivalQpc;
            for (int index = 0; index < count; index++)
            {
                if (!acceptedScratch[index])
                {
                    continue;
                }

                double x = orderedAnchors[index].LogicalSequence -
                    referenceSequence;
                double y = orderedAnchors[index].ArrivalQpc - baseArrival;
                accepted++;
                sumX += x;
                sumY += y;
            }

            if (accepted < 2)
            {
                return fallback;
            }

            double meanX = sumX / accepted;
            double meanY = sumY / accepted;
            double covariance = 0.0;
            double variance = 0.0;
            for (int index = 0; index < count; index++)
            {
                if (!acceptedScratch[index])
                {
                    continue;
                }

                double centeredX =
                    orderedAnchors[index].LogicalSequence -
                    referenceSequence - meanX;
                double centeredY =
                    orderedAnchors[index].ArrivalQpc -
                    baseArrival - meanY;
                covariance += centeredX * centeredY;
                variance += centeredX * centeredX;
            }

            if (variance <= 0.0)
            {
                return fallback;
            }

            return Math.Clamp(covariance / variance,
                nominalPeriodTicks * (1.0 - MaximumPeriodError),
                nominalPeriodTicks * (1.0 + MaximumPeriodError));
        }

        private double FitArrivalAtReference(int count,
            long referenceSequence, double periodTicks)
        {
            double sum = 0.0;
            int accepted = 0;
            for (int index = 0; index < count; index++)
            {
                if (!acceptedScratch[index])
                {
                    continue;
                }

                sum += orderedAnchors[index].ArrivalQpc -
                    periodTicks * (orderedAnchors[index].LogicalSequence -
                        referenceSequence);
                accepted++;
            }

            return accepted > 0 ? sum / accepted :
                orderedAnchors[count - 1].ArrivalQpc;
        }

        private long FirstPresentationIndexAtOrAfter(long nowQpc)
        {
            double presentationPeriodTicks = modelPeriodTicks *
                MicrophonePeriodsPerSpeakerFrame;
            double firstDeadline = PredictDeadlineDouble(0);
            double candidateValue = Math.Ceiling(
                (nowQpc - firstDeadline) / presentationPeriodTicks);
            long candidate;
            if (candidateValue >= long.MaxValue)
            {
                candidate = long.MaxValue;
            }
            else if (candidateValue <= long.MinValue)
            {
                candidate = long.MinValue;
            }
            else
            {
                candidate = (long)candidateValue;
            }

            // Correct a possible one-slot floating-point error at an exact
            // boundary without changing the fitted phase.
            while (candidate < long.MaxValue &&
                PredictDeadline(candidate) < nowQpc)
            {
                candidate++;
            }

            while (candidate > long.MinValue &&
                PredictDeadline(candidate - 1) >= nowQpc)
            {
                candidate--;
            }

            return candidate;
        }

        private long PredictDeadline(long presentationIndex)
        {
            double predicted = PredictDeadlineDouble(presentationIndex);
            if (predicted >= long.MaxValue)
            {
                return long.MaxValue;
            }

            if (predicted <= long.MinValue)
            {
                return long.MinValue;
            }

            return (long)Math.Round(predicted);
        }

        private double PredictDeadlineDouble(long presentationIndex)
        {
            double microphoneCoordinate =
                FirstPresentationMicrophoneCoordinate +
                presentationIndex * MicrophonePeriodsPerSpeakerFrame;
            return modelLowerEnvelopeAtReferenceQpc +
                modelPeriodTicks *
                    (microphoneCoordinate - modelReferenceSequence) +
                targetPhaseTicks;
        }

        private void ResetCore()
        {
            initialized = false;
            previousRawSequence = 0;
            latestLogicalSequence = 0;
            previousArrivalQpc = 0;
            currentBlockIndex = 0;
            currentBlockObservations = 0;
            currentBlockFirstSlot = SequenceBlockSize;
            currentBlockLastSlot = -1;
            currentBlockMinimumSequence = 0;
            currentBlockMinimumArrivalQpc = 0;
            currentBlockMinimumNominalIntercept = double.PositiveInfinity;
            Array.Clear(history, 0, history.Length);
            historyHead = 0;
            historyCount = 0;
            reliableBlockCount = 0;
            acceptedAnchorCount = 0;
            locked = false;
            modelReferenceSequence = 0;
            modelLowerEnvelopeAtReferenceQpc = 0.0;
            modelPeriodTicks = nominalPeriodTicks;
            nextSlotInitialized = false;
            nextPresentationIndex = 0;
            generation++;
        }

        private readonly struct BlockAnchor
        {
            public BlockAnchor(long logicalSequence, long arrivalQpc)
            {
                LogicalSequence = logicalSequence;
                ArrivalQpc = arrivalQpc;
            }

            public long LogicalSequence { get; }
            public long ArrivalQpc { get; }
        }

        public readonly struct ClockSnapshot
        {
            internal ClockSnapshot(bool isLocked, int generation,
                long latestLogicalSequence, int reliableBlockCount,
                int acceptedAnchorCount, long referenceSequence,
                double lowerEnvelopeAtReferenceQpc, double periodTicks,
                double targetPhaseTicks, long clockFrequency)
            {
                IsLocked = isLocked;
                Generation = generation;
                LatestLogicalSequence = latestLogicalSequence;
                ReliableBlockCount = reliableBlockCount;
                AcceptedAnchorCount = acceptedAnchorCount;
                ReferenceSequence = referenceSequence;
                LowerEnvelopeAtReferenceQpc =
                    lowerEnvelopeAtReferenceQpc;
                PeriodTicks = periodTicks;
                TargetPhaseTicks = targetPhaseTicks;
                ClockFrequency = clockFrequency;
            }

            public bool IsLocked { get; }
            public int Generation { get; }
            public long LatestLogicalSequence { get; }
            public int ReliableBlockCount { get; }
            public int AcceptedAnchorCount { get; }
            public long ReferenceSequence { get; }
            public double LowerEnvelopeAtReferenceQpc { get; }
            public double PeriodTicks { get; }
            public double TargetPhaseTicks { get; }
            public long ClockFrequency { get; }

            public double PeriodMilliseconds =>
                PeriodTicks * 1_000.0 / ClockFrequency;

            public long GetDeadlineQpc(long presentationIndex)
            {
                if (!IsLocked)
                {
                    throw new InvalidOperationException(
                        "The microphone presentation clock is not locked.");
                }

                double microphoneCoordinate =
                    FirstPresentationMicrophoneCoordinate +
                    presentationIndex *
                        MicrophonePeriodsPerSpeakerFrame;
                return (long)Math.Round(
                    LowerEnvelopeAtReferenceQpc +
                    PeriodTicks *
                        (microphoneCoordinate - ReferenceSequence) +
                    TargetPhaseTicks);
            }
        }
    }
}
