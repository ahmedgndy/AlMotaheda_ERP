using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Features.Gps;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>
/// The route, trip and stop rules with injected times and coordinates (no GPS, no database): one position a minute
/// moving, GPS jitter while parked, stops and departures, data gaps, inaccurate and impossible positions, duplicates
/// and positions that arrive out of order.
/// </summary>
public sealed class TripSegmenterTests
{
    private static readonly MotionSettings Settings = MotionSettings.From(new VehicleGpsOptions());
    private static readonly DateTime T0 = new(2026, 10, 21, 6, 0, 0, DateTimeKind.Utc);

    // Cairo; 0.005° of latitude ≈ 556 m: one minute at about 33 km/h.
    private const double Lat0 = 30.0444, Lng0 = 31.2357, Step = 0.005;

    private static long _ids;

    private static GpsSample At(double minute, double latitude, double longitude, int? accuracy = 8) =>
        new(Interlocked.Increment(ref _ids), T0.AddMinutes(minute), latitude, longitude, accuracy, null);

    /// <summary>Moving north one step a minute from <paramref name="fromMinute"/>, starting at <paramref name="latitude"/>.</summary>
    private static IEnumerable<GpsSample> Drive(int fromMinute, int minutes, double latitude) =>
        Enumerable.Range(0, minutes + 1).Select(index => At(fromMinute + index, latitude + index * Step, Lng0));

    /// <summary>Parked with GPS jitter of up to about 20 m, one position a minute.</summary>
    private static IEnumerable<GpsSample> Park(int fromMinute, int minutes, double latitude)
    {
        double[] jitter = [0, 0.00012, -0.00015, 0.00008, -0.00005, 0.00017, -0.00011];
        return Enumerable.Range(0, minutes + 1).Select(index => At(fromMinute + index, latitude + jitter[index % jitter.Length], Lng0 - jitter[(index + 3) % jitter.Length]));
    }

    [Fact]
    public void A_one_position_a_minute_while_moving_is_one_ordered_route_with_its_measured_distance()
    {
        var timeline = TripSegmenter.Segment(Drive(0, 9, Lat0), Settings);

        var trip = Assert.Single(timeline.Trips);
        Assert.Empty(timeline.Stops);
        Assert.Equal((10, 10, 0, true, true), (timeline.Accepted.Count, trip.Samples, trip.Gaps, trip.StartedAtDataStart, trip.Ongoing));
        Assert.InRange(trip.DistanceMeters, 4_950, 5_060);
        Assert.Equal(T0, trip.StartedAt);
        Assert.Equal(T0.AddMinutes(9), trip.EndedAt);
        Assert.Equal(timeline.Accepted.Select(point => point.At).Order(), Assert.Single(trip.Segments).Select(point => point.At));
        Assert.Equal("Moving", timeline.Motion.State);
    }

    [Fact]
    public void B_jitter_while_parked_is_no_movement_and_five_minutes_of_it_is_one_confirmed_stop()
    {
        var timeline = TripSegmenter.Segment(Park(0, 5, Lat0), Settings);

        Assert.Empty(timeline.Trips);
        var stop = Assert.Single(timeline.Stops);
        Assert.Equal((T0, T0.AddMinutes(5), 6, true), (stop.StartedAt, stop.EndedAt, stop.Samples, stop.Ongoing));
        Assert.Equal(("Stopped", (DateTime?)T0), (timeline.Motion.State, timeline.Motion.Since));
    }

    [Fact]
    public void Four_minutes_parked_is_only_a_suspected_stop_never_a_trip()
    {
        var timeline = TripSegmenter.Segment(Park(0, 4, Lat0), Settings);

        Assert.Empty(timeline.Trips);
        Assert.Empty(timeline.Stops);
        Assert.Equal("SuspectedStop", timeline.Motion.State);
    }

    [Fact]
    public void C_drive_stop_drive_is_two_trips_around_one_stop_with_no_duplicates()
    {
        var samples = Drive(0, 5, Lat0)
            .Concat(Park(6, 6, Lat0 + 5 * Step).Skip(0))
            .Concat(Drive(13, 5, Lat0 + 5 * Step).Skip(1))
            .ToList();

        var timeline = TripSegmenter.Segment(samples, Settings);

        var stop = Assert.Single(timeline.Stops);
        Assert.Equal(2, timeline.Trips.Count);
        var (first, second) = (timeline.Trips[0], timeline.Trips[1]);
        Assert.Equal(T0, first.StartedAt);
        Assert.Equal(stop.StartedAt, first.EndedAt);
        Assert.Equal(stop.EndedAt, second.StartedAt);
        Assert.True(stop.Seconds >= 5 * 60);
        Assert.False(first.Ongoing);
        Assert.True(second.Ongoing);
        Assert.False(second.StartedAtDataStart);

        // Segmenting the same positions again (a re-read, a late copy) gives the same answer: nothing is added twice.
        var again = TripSegmenter.Segment(samples.Concat(samples.Select(sample => sample with { Id = sample.Id + 1_000_000 })), Settings);
        Assert.Equal((2, 1, samples.Count), (again.Trips.Count, again.Stops.Count, again.Duplicates));
    }

    [Fact]
    public void A_traffic_light_of_two_minutes_stays_inside_the_trip()
    {
        var samples = Drive(0, 4, Lat0).Concat(Park(5, 2, Lat0 + 4 * Step).Skip(1)).Concat(Drive(7, 4, Lat0 + 4 * Step).Skip(1));

        var timeline = TripSegmenter.Segment(samples, Settings);

        Assert.Empty(timeline.Stops);
        Assert.Single(timeline.Trips);
    }

    [Fact]
    public void D_a_gap_while_moving_is_marked_and_nothing_is_drawn_or_measured_across_it()
    {
        // 5 minutes driving, 15 minutes with no position (out of coverage), 5 more minutes driving 5 km further.
        var samples = Drive(0, 5, Lat0).Concat(Drive(20, 5, Lat0 + 15 * Step)).ToList();

        var timeline = TripSegmenter.Segment(samples, Settings);

        var trip = Assert.Single(timeline.Trips);
        var gap = Assert.Single(timeline.Gaps);
        Assert.Equal((T0.AddMinutes(5), T0.AddMinutes(20)), (gap.Before.At, gap.After.At));
        Assert.Equal((1, 2), (trip.Gaps, trip.Segments.Count));
        Assert.InRange(trip.DistanceMeters, 2 * 5 * 540, 2 * 5 * 570);
        Assert.InRange(trip.UnmeasuredMeters, 5_400, 5_700);
        Assert.Equal(10 * 60, trip.MovingSeconds);
        Assert.Equal(T0.AddMinutes(5), trip.Segments[0][^1].At);
        Assert.Equal(T0.AddMinutes(20), trip.Segments[1][0].At);
    }

    [Fact]
    public void A_gap_while_parked_at_the_same_place_is_one_stop_marked_as_having_a_gap_not_a_trip()
    {
        var samples = Park(0, 3, Lat0).Concat(Park(120, 3, Lat0));

        var timeline = TripSegmenter.Segment(samples, Settings);

        Assert.Empty(timeline.Trips);
        var stop = Assert.Single(timeline.Stops);
        Assert.True(stop.HasDataGap);
        Assert.Equal(TimeSpan.FromMinutes(123), stop.EndedAt - stop.StartedAt);
    }

    [Fact]
    public void E_a_working_day_is_its_trips_and_stops_in_order()
    {
        var samples = Park(0, 10, Lat0)                       // at the yard
            .Concat(Drive(11, 10, Lat0).Skip(1))              // to site A
            .Concat(Park(21, 30, Lat0 + 10 * Step).Skip(1))   // at site A
            .Concat(Drive(51, 6, Lat0 + 10 * Step).Skip(1))   // to the fuel station
            .Concat(Park(57, 8, Lat0 + 16 * Step).Skip(1))    // refuelling
            .Concat(Drive(65, 16, Lat0 + 16 * Step).Skip(1))  // back past the yard
            .Concat(Park(81, 20, Lat0 + 32 * Step).Skip(1))   // parked
            .Reverse()                                        // arrival order never matters
            .ToList();

        var timeline = TripSegmenter.Segment(samples, Settings);

        Assert.Equal(4, timeline.Stops.Count);
        Assert.Equal(3, timeline.Trips.Count);
        Assert.True(timeline.Trips.Zip(timeline.Trips.Skip(1)).All(pair => pair.First.EndedAt < pair.Second.StartedAt));
        Assert.True(timeline.Stops.Zip(timeline.Trips).All(pair => pair.First.EndedAt == pair.Second.StartedAt));
        Assert.Equal([10, 6, 16], timeline.Trips.Select(trip => (int)Math.Round(trip.DistanceMeters / 556)));
        Assert.Equal("Stopped", timeline.Motion.State);
    }

    [Fact]
    public void Two_stops_at_the_same_place_with_drift_between_them_are_one_stop()
    {
        // Parked 6 minutes, the fix wanders 70 m away for 2 minutes, then back for 6 minutes.
        var samples = Park(0, 6, Lat0)
            .Concat([At(7, Lat0 + 0.0006, Lng0), At(8, Lat0 + 0.0006, Lng0)])
            .Concat(Park(9, 6, Lat0));

        var timeline = TripSegmenter.Segment(samples, Settings);

        Assert.Empty(timeline.Trips);
        Assert.Single(timeline.Stops);
    }

    [Fact]
    public void A_trip_across_midnight_is_one_trip()
    {
        var midnight = new DateTime(2026, 10, 21, 22, 0, 0, DateTimeKind.Utc); // 00:00 in Cairo (UTC+2)
        var samples = Enumerable.Range(0, 21).Select(index => new GpsSample(index + 1, midnight.AddMinutes(index - 10), Lat0 + index * Step, Lng0, 8, null));

        var trip = Assert.Single(TripSegmenter.Segment(samples, Settings).Trips);

        Assert.Equal((midnight.AddMinutes(-10), midnight.AddMinutes(10)), (trip.StartedAt, trip.EndedAt));
    }

    [Fact]
    public void K_inaccurate_positions_and_a_lone_impossible_jump_are_kept_out_of_the_route()
    {
        var samples = Drive(0, 6, Lat0).ToList();
        samples.Add(At(2.5, Lat0 + 0.9, Lng0));                 // 100 km away for 30 seconds: impossible
        samples.Add(At(3.5, Lat0 + 2.5 * Step, Lng0, 500));     // accuracy 500 m
        samples.Add(At(4.5, Lat0 + 4.5 * Step, Lng0, null));    // no accuracy reported
        samples.Add(At(5, Lat0 + 5 * Step, Lng0, 20));          // same moment as another position: counted once

        var timeline = TripSegmenter.Segment(samples, Settings);

        Assert.Equal((11, 2, 1, 1, 7), (timeline.Total, timeline.Inaccurate, timeline.Implausible, timeline.Duplicates, timeline.Accepted.Count));
        Assert.All(timeline.Accepted, point => Assert.True(point.Latitude < Lat0 + 0.1));
        Assert.InRange(Assert.Single(timeline.Trips).DistanceMeters, 3_300, 3_380);
    }

    [Fact]
    public void Three_positions_that_agree_on_a_new_place_are_believed()
    {
        // The phone was off for a minute in a fast car: the jump is real, and the next positions confirm it.
        var samples = Drive(0, 3, Lat0).Concat(Drive(4, 3, Lat0 + 0.9)).ToList();

        var timeline = TripSegmenter.Segment(samples, Settings);

        Assert.Equal(8, timeline.Accepted.Count);
        Assert.Equal(0, timeline.Implausible);
    }

    [Fact]
    public void No_positions_or_only_rough_ones_say_unknown()
    {
        Assert.Equal("Unknown", TripSegmenter.Segment([], Settings).Motion.State);
        var rough = TripSegmenter.Segment(Drive(0, 5, Lat0).Select(sample => sample with { AccuracyMeters = 900 }), Settings);
        Assert.Equal((0, 6, "Unknown"), (rough.Accepted.Count, rough.Inaccurate, rough.Motion.State));
        Assert.Empty(rough.Trips);
    }
}
