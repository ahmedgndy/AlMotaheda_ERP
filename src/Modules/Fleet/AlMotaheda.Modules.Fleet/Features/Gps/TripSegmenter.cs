using AlMotaheda.Modules.Fleet.Common;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>One stored position as the segmenter reads it.</summary>
internal sealed record GpsSample(long Id, DateTime RecordedAt, double Latitude, double Longitude, int? AccuracyMeters, double? SpeedKmh, int? DriverEmployeeId = null);

/// <summary>An accepted position on a route.</summary>
internal sealed record GpsRoutePoint(DateTime At, double Latitude, double Longitude, int? AccuracyMeters, double? SpeedKmh, int? DriverEmployeeId)
{
    public static GpsRoutePoint From(GpsSample sample) =>
        new(sample.RecordedAt, sample.Latitude, sample.Longitude, sample.AccuracyMeters, sample.SpeedKmh, sample.DriverEmployeeId);
}

/// <summary>A confirmed stop: no meaningful movement for at least the stop time.</summary>
/// <param name="HasDataGap">Positions were missing for a while during the stop (the place before and after was the same).</param>
/// <param name="Ongoing">The last positions received are still in this stop.</param>
internal sealed record GpsStopInterval(DateTime StartedAt, DateTime EndedAt, double Latitude, double Longitude, int Samples, bool HasDataGap, bool Ongoing)
{
    public double Seconds => (EndedAt - StartedAt).TotalSeconds;
}

/// <summary>Positions missing for longer than the gap time, between two accepted positions. The route between them is unknown.</summary>
internal sealed record GpsDataGap(GpsRoutePoint Before, GpsRoutePoint After, double StraightLineMeters)
{
    public double Seconds => (After.At - Before.At).TotalSeconds;
}

/// <summary>
/// One trip: from the departure from a stop (or the first position) to the arrival at the next stop (or the last
/// position). <see cref="DistanceMeters"/> is measured along accepted positions only; the straight lines across
/// data gaps are in <see cref="UnmeasuredMeters"/> and never drawn as route.
/// </summary>
/// <param name="Segments">The route as runs of positions without a gap; a new run starts after each gap.</param>
/// <param name="StartedAtDataStart">No stop was seen before it: it may have started before the positions read.</param>
/// <param name="Ongoing">No stop was seen after it: the vehicle may still be moving (or stopped reporting).</param>
internal sealed record GpsTrip(
    DateTime StartedAt,
    DateTime EndedAt,
    GpsRoutePoint Start,
    GpsRoutePoint End,
    double DistanceMeters,
    double UnmeasuredMeters,
    double MovingSeconds,
    int Samples,
    int Gaps,
    double? MaxSpeedKmh,
    bool StartedAtDataStart,
    bool Ongoing,
    IReadOnlyList<IReadOnlyList<GpsRoutePoint>> Segments);

/// <summary>What the positions say about the vehicle now: <c>Moving</c>, <c>SuspectedStop</c>, <c>Stopped</c> or <c>Unknown</c>, and since when.</summary>
internal sealed record GpsMotion(string State, DateTime? Since);

/// <summary>The segmenter's answer for a series of positions.</summary>
internal sealed record GpsTimeline(
    IReadOnlyList<GpsRoutePoint> Accepted,
    IReadOnlyList<GpsTrip> Trips,
    IReadOnlyList<GpsStopInterval> Stops,
    IReadOnlyList<GpsDataGap> Gaps,
    int Total,
    int Inaccurate,
    int Implausible,
    int Duplicates,
    GpsMotion Motion);

/// <summary>The thresholds the segmenter follows (<see cref="VehicleGpsOptions"/>).</summary>
internal sealed record MotionSettings(int MaxAccuracyMeters, int StopRadiusMeters, int StopMinutes, int MinTripMeters, int GapMinutes, int MaxSpeedKmh)
{
    public static MotionSettings From(VehicleGpsOptions options) =>
        new(options.MaxAccuracyMeters, options.StopRadiusMeters, options.StopMinutes, options.MinTripMeters, options.GapMinutes, options.MaxSpeedKmh);
}

/// <summary>
/// Turns stored positions into a route, trips, stops and data gaps. Pure and deterministic: no clock, no database,
/// so every rule is tested with injected times and coordinates. The rules (documented for the owner):
/// <list type="number">
/// <item>Positions are ordered by the time the phone took them, whatever order they arrived in. Two positions with
/// the same capture time count once (the more accurate one).</item>
/// <item>A position without an accuracy, or less accurate than <see cref="MotionSettings.MaxAccuracyMeters"/>, is
/// kept in the database but not used. So is a jump that implies more than <see cref="MotionSettings.MaxSpeedKmh"/>
/// from the previous accepted position, unless three positions in a row agree on the new place.</item>
/// <item>Movement is never read from small coordinate changes: consecutive positions that stay within
/// <see cref="MotionSettings.StopRadiusMeters"/> (widened by their reported accuracy) of their running centre are
/// "not moving". Not moving for <see cref="MotionSettings.StopMinutes"/> or longer is a confirmed stop, one interval
/// however many positions it holds; a shorter halt (a traffic light) stays part of the trip.</item>
/// <item>No accepted position for longer than <see cref="MotionSettings.GapMinutes"/> is a data gap. If the place
/// before and after is the same, the stop simply continues (marked as having a gap); otherwise the route is
/// interrupted: nothing is drawn or measured across it.</item>
/// <item>A trip runs from the last position of one stop to the first position of the next. Its distance is the sum
/// of great-circle distances between consecutive accepted positions (not across gaps). A movement shorter than
/// <see cref="MotionSettings.MinTripMeters"/> between two stops at the same place is drift: the two stops are one.</item>
/// </list>
/// The speed the phone reports is only shown, never used to decide movement.
/// </summary>
internal static class TripSegmenter
{
    private const double EarthRadiusMeters = 6_371_008.8;

    public static GpsTimeline Segment(IEnumerable<GpsSample> samples, MotionSettings settings)
    {
        var ordered = samples.OrderBy(sample => sample.RecordedAt).ThenBy(sample => sample.Id).ToList();
        var (accepted, inaccurate, implausible, duplicates) = Filter(ordered, settings);
        if (accepted.Count == 0)
        {
            return new GpsTimeline([], [], [], [], ordered.Count, inaccurate, implausible, duplicates, new GpsMotion("Unknown", null));
        }

        var gapSeconds = settings.GapMinutes * 60.0;
        var clusters = Clusters(accepted, settings, gapSeconds);
        var stopSeconds = settings.StopMinutes * 60.0;

        // Clusters long enough are stops; the rest is movement between them.
        var stops = clusters.Where(cluster => cluster.Seconds >= stopSeconds).ToList();
        stops = MergeDrift(accepted, stops, settings, gapSeconds);

        var trips = new List<GpsTrip>();
        var startIndex = 0;
        var startedAtDataStart = true;
        foreach (var stop in stops)
        {
            if (stop.First > startIndex)
            {
                AddTrip(trips, accepted, startIndex, stop.First, settings, gapSeconds, startedAtDataStart, ongoing: false);
            }

            startIndex = stop.Last;
            startedAtDataStart = false;
        }

        if (startIndex < accepted.Count - 1)
        {
            AddTrip(trips, accepted, startIndex, accepted.Count - 1, settings, gapSeconds, startedAtDataStart, ongoing: true);
        }

        var gaps = new List<GpsDataGap>();
        for (var index = 1; index < accepted.Count; index++)
        {
            if ((accepted[index].At - accepted[index - 1].At).TotalSeconds > gapSeconds)
            {
                gaps.Add(new GpsDataGap(accepted[index - 1], accepted[index], Meters(accepted[index - 1], accepted[index])));
            }
        }

        var lastIndex = accepted.Count - 1;
        var stopViews = stops.Select(stop => new GpsStopInterval(
            accepted[stop.First].At, accepted[stop.Last].At, stop.Latitude, stop.Longitude, stop.Last - stop.First + 1, stop.HasGap, stop.Last == lastIndex)).ToList();

        return new GpsTimeline(accepted, trips, stopViews, gaps, ordered.Count, inaccurate, implausible, duplicates, Motion(accepted, clusters, stopViews, trips, stopSeconds));
    }

    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double Meters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180.0;
        var dLat = Radians(latitude2 - latitude1);
        var dLng = Radians(longitude2 - longitude1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Radians(latitude1)) * Math.Cos(Radians(latitude2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static double Meters(GpsRoutePoint from, GpsRoutePoint to) => Meters(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

    private static (List<GpsRoutePoint> Accepted, int Inaccurate, int Implausible, int Duplicates) Filter(List<GpsSample> ordered, MotionSettings settings)
    {
        var accepted = new List<GpsRoutePoint>();
        int inaccurate = 0, implausible = 0, duplicates = 0;
        var refusedInARow = new List<GpsRoutePoint>();
        foreach (var sample in ordered)
        {
            if (sample.AccuracyMeters is not { } accuracy || accuracy > settings.MaxAccuracyMeters)
            {
                inaccurate++;
                continue;
            }

            var point = GpsRoutePoint.From(sample);
            if (accepted.Count > 0 && accepted[^1].At == point.At)
            {
                duplicates++;
                if (accuracy < (accepted[^1].AccuracyMeters ?? int.MaxValue))
                {
                    accepted[^1] = point;
                }

                continue;
            }

            if (accepted.Count > 0 && Implausible(accepted[^1], point, settings))
            {
                // Three positions in a row that agree with each other confirm the new place (the vehicle did get
                // there, for example after a gap): they are accepted. A lone jump is not.
                if (refusedInARow.Count > 0 && Implausible(refusedInARow[^1], point, settings))
                {
                    refusedInARow.Clear();
                }

                refusedInARow.Add(point);
                if (refusedInARow.Count >= 3)
                {
                    accepted.AddRange(refusedInARow);
                    implausible -= refusedInARow.Count - 1;
                    refusedInARow.Clear();
                    continue;
                }

                implausible++;
                continue;
            }

            refusedInARow.Clear();
            accepted.Add(point);
        }

        return (accepted, inaccurate, implausible, duplicates);
    }

    private static bool Implausible(GpsRoutePoint from, GpsRoutePoint to, MotionSettings settings)
    {
        var seconds = (to.At - from.At).TotalSeconds;
        if (seconds <= 0)
        {
            return false;
        }

        var meters = Meters(from, to);
        var slack = (from.AccuracyMeters ?? 0) + (to.AccuracyMeters ?? 0) + settings.StopRadiusMeters;
        return meters > slack && meters / seconds * 3.6 > settings.MaxSpeedKmh;
    }

    private sealed class ClusterState(int first, GpsRoutePoint point)
    {
        public int First { get; } = first;
        public int Last { get; set; } = first;
        public double SumLatitude { get; set; } = point.Latitude;
        public double SumLongitude { get; set; } = point.Longitude;
        public int Count { get; set; } = 1;
        public int WorstAccuracy { get; set; } = point.AccuracyMeters ?? 0;
        public bool HasGap { get; set; }
        public DateTime StartedAt { get; } = point.At;
        public DateTime EndedAt { get; set; } = point.At;

        public double Latitude => SumLatitude / Count;
        public double Longitude => SumLongitude / Count;
        public double Seconds => (EndedAt - StartedAt).TotalSeconds;
    }

    private sealed record Cluster(int First, int Last, double Latitude, double Longitude, bool HasGap, double Seconds);

    /// <summary>Runs of consecutive positions that stay within the stop radius of their running centre.</summary>
    private static List<Cluster> Clusters(List<GpsRoutePoint> accepted, MotionSettings settings, double gapSeconds)
    {
        var clusters = new List<Cluster>();
        var current = new ClusterState(0, accepted[0]);
        for (var index = 1; index < accepted.Count; index++)
        {
            var point = accepted[index];
            var radius = Math.Max(settings.StopRadiusMeters, Math.Min(settings.MaxAccuracyMeters, Math.Max(point.AccuracyMeters ?? 0, current.WorstAccuracy)));
            var gap = (point.At - accepted[index - 1].At).TotalSeconds > gapSeconds;
            if (Meters(current.Latitude, current.Longitude, point.Latitude, point.Longitude) <= radius)
            {
                current.Last = index;
                current.EndedAt = point.At;
                current.SumLatitude += point.Latitude;
                current.SumLongitude += point.Longitude;
                current.Count++;
                current.WorstAccuracy = Math.Max(current.WorstAccuracy, point.AccuracyMeters ?? 0);
                current.HasGap |= gap;
                continue;
            }

            clusters.Add(Close(current));
            current = new ClusterState(index, point);
        }

        clusters.Add(Close(current));
        return clusters;

        static Cluster Close(ClusterState state) => new(state.First, state.Last, state.Latitude, state.Longitude, state.HasGap, state.Seconds);
    }

    /// <summary>Two stops at the same place with only a drift between them are one stop.</summary>
    private static List<Cluster> MergeDrift(List<GpsRoutePoint> accepted, List<Cluster> stops, MotionSettings settings, double gapSeconds)
    {
        var merged = new List<Cluster>();
        foreach (var stop in stops)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                var samePlace = Meters(previous.Latitude, previous.Longitude, stop.Latitude, stop.Longitude) <= 2 * settings.StopRadiusMeters;
                var (distance, _, _) = Measure(accepted, previous.Last, stop.First, gapSeconds);
                if (samePlace && distance < settings.MinTripMeters)
                {
                    var first = previous.First;
                    merged[^1] = new Cluster(
                        first, stop.Last,
                        (previous.Latitude + stop.Latitude) / 2, (previous.Longitude + stop.Longitude) / 2,
                        previous.HasGap || stop.HasGap || HasGapBetween(accepted, previous.Last, stop.First, gapSeconds),
                        (accepted[stop.Last].At - accepted[first].At).TotalSeconds);
                    continue;
                }
            }

            merged.Add(stop);
        }

        return merged;
    }

    private static bool HasGapBetween(List<GpsRoutePoint> accepted, int from, int to, double gapSeconds)
    {
        for (var index = from + 1; index <= to; index++)
        {
            if ((accepted[index].At - accepted[index - 1].At).TotalSeconds > gapSeconds)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Distance along accepted positions (not across gaps), the straight lines across gaps, and the gap time.</summary>
    private static (double Measured, double Unmeasured, double GapSeconds) Measure(List<GpsRoutePoint> accepted, int from, int to, double gapSeconds)
    {
        double measured = 0, unmeasured = 0, gapTime = 0;
        for (var index = from + 1; index <= to; index++)
        {
            var seconds = (accepted[index].At - accepted[index - 1].At).TotalSeconds;
            var meters = Meters(accepted[index - 1], accepted[index]);
            if (seconds > gapSeconds)
            {
                unmeasured += meters;
                gapTime += seconds;
            }
            else
            {
                measured += meters;
            }
        }

        return (measured, unmeasured, gapTime);
    }

    private static void AddTrip(List<GpsTrip> trips, List<GpsRoutePoint> accepted, int from, int to, MotionSettings settings, double gapSeconds, bool startedAtDataStart, bool ongoing)
    {
        if (to <= from)
        {
            return;
        }

        var (measured, unmeasured, gapTime) = Measure(accepted, from, to, gapSeconds);

        // Drift at the edge of the data (before the first stop, after the last one) is not a trip.
        if ((startedAtDataStart || ongoing) && measured + unmeasured < settings.MinTripMeters)
        {
            return;
        }

        var segments = new List<IReadOnlyList<GpsRoutePoint>>();
        var run = new List<GpsRoutePoint> { accepted[from] };
        var gaps = 0;
        double? maxSpeed = null;
        for (var index = from + 1; index <= to; index++)
        {
            var seconds = (accepted[index].At - accepted[index - 1].At).TotalSeconds;
            if (seconds > gapSeconds)
            {
                gaps++;
                segments.Add(run);
                run = [];
            }
            else if (seconds > 0)
            {
                var computed = Meters(accepted[index - 1], accepted[index]) / seconds * 3.6;
                var reported = accepted[index].SpeedKmh;
                var speed = reported is { } value && value >= 0 ? value : computed;
                maxSpeed = Math.Max(maxSpeed ?? 0, speed);
            }

            run.Add(accepted[index]);
        }

        segments.Add(run);
        var total = (accepted[to].At - accepted[from].At).TotalSeconds;
        trips.Add(new GpsTrip(
            accepted[from].At, accepted[to].At, accepted[from], accepted[to], measured, unmeasured, Math.Max(0, total - gapTime), to - from + 1, gaps,
            maxSpeed is null ? null : Math.Round(maxSpeed.Value, 1), startedAtDataStart, ongoing, segments));
    }

    private static GpsMotion Motion(List<GpsRoutePoint> accepted, List<Cluster> clusters, List<GpsStopInterval> stops, List<GpsTrip> trips, double stopSeconds)
    {
        if (stops.Count > 0 && stops[^1].Ongoing)
        {
            return new GpsMotion("Stopped", stops[^1].StartedAt);
        }

        var last = clusters[^1];
        if (last.Last == accepted.Count - 1 && last.Last > last.First && last.Seconds < stopSeconds)
        {
            return new GpsMotion("SuspectedStop", accepted[last.First].At);
        }

        if (trips.Count > 0 && trips[^1].Ongoing)
        {
            return new GpsMotion("Moving", trips[^1].StartedAt);
        }

        return new GpsMotion("Unknown", null);
    }
}
