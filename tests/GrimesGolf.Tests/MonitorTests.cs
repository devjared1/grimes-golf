using GrimesGolf.Core;
using GrimesGolf.Core.Monitors;
using GrimesGolf.Core.Square;
using GrimesGolf.Core.Storage;
using Xunit;

namespace GrimesGolf.Tests;

public class MonitorAbstractionTests
{
    /// <summary>Square shots now go through the device-neutral form: they must come out exactly as before.</summary>
    [Fact]
    public void SquareShotsAreTheSameThroughTheNeutralForm()
    {
        var rng = new Random(11);
        var cases = new List<(BallMetricsEvent, ClubMetricsEvent?)>
        {
            (new BallMetricsEvent(50, 15, 2, 6000, -8, 5900, -800), new ClubMetricsEvent(0xff, 1, 2, -3, 20, null, null, 37.5, null)),
            (new BallMetricsEvent(67.12, 12.34, -1.5, 2800, 5.25, 2788, 256), new ClubMetricsEvent(0x41, null, null, null, null, null, null, 45.01, 1.49)),
            (new BallMetricsEvent(2.1, 0.5, 0, 0, 0, 0, 0), null),                        // a putt
            (new BallMetricsEvent(0, 0, 0, 0, 0, 0, 0), null),                            // a misread the Square still sends
            (new BallMetricsEvent(30, -5, 95, -200, 120, 0, 0), new ClubMetricsEvent(0x40, null, null, null, null, null, null, 0, null)),
        };
        for (int i = 0; i < 200; i++)
            cases.Add((new BallMetricsEvent(rng.Next(0, 9000) / 100.0, rng.Next(-1000, 6000) / 100.0, rng.Next(-2000, 2000) / 100.0,
                                            rng.Next(0, 12000), rng.Next(-4500, 4500) / 100.0, rng.Next(0, 12000), rng.Next(-3000, 3000)),
                       rng.Next(3) == 0 ? null : new ClubMetricsEvent(0x41, null, null, null, null, null, null, rng.Next(0, 6000) / 100.0,
                                                                      rng.Next(2) == 0 ? null : rng.Next(100, 155) / 100.0)));
        foreach (var (ball, club) in cases)
        {
            var before = RangeShot.FromSquare(ball, club);
            var after = RangeShot.FromLaunchMonitor(SquareShots.ToMonitorShot(ball, club), out _);
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public void SquareShotsKeepTheirClubData()
    {
        var shot = SquareShots.ToMonitorShot(new BallMetricsEvent(50, 15, 2, 6000, -8, 5900, -800),
            new ClubMetricsEvent(0xff, 1, 2, -3, 20, null, null, 37.5, 1.3), leftHanded: true);
        var i = shot.Input;
        Assert.Equal(SquareShots.Device, i.Device);
        Assert.Equal(8, i.SpinAxisDegrees);
        Assert.Equal(800, i.SidespinRpm);   // device −800 (right) → +800
        Assert.Equal(5900, i.BackspinRpm);
        Assert.Equal((1.0, 2.0, -3.0, 20.0), (i.ClubPathDegrees!.Value, i.FaceAngleDegrees!.Value, i.AttackAngleDegrees!.Value, i.DynamicLoftDegrees!.Value));
        Assert.Equal(1.3, shot.ReportedSmash);
        Assert.True(i.LeftHanded);
    }

    [Fact]
    public void SquareDetailsAreTheOnesTheDrawerAlwaysShowed()
    {
        var info = SquareMonitor.Info(new SquareDeviceInfo("S1", "3.1", "{\"launcher\":\"1.0.1\",\"mmi\":\"1.4.0\",\"lm\":\"1.5.8\"}"));
        Assert.Equal("Square Omni (hardware 3.1)", info.Description);
        Assert.True(info.Identified);
        Assert.Equal(new[] { "Serial", "Hardware", "Firmware (LM)", "Firmware (MMI)", "Firmware (launcher)" }, info.Stats.Select(s => s.Label));
        Assert.Equal("1.5.8", info.Stats[2].Value);
        var unknown = SquareMonitor.Info(new SquareDeviceInfo(null, null, null));
        Assert.False(unknown.Identified);
        Assert.All(unknown.Stats, s => Assert.Equal("—", s.Value));   // the em dash the drawer has always shown
        Assert.Equal(new MonitorBattery(60, "charging"), SquareMonitor.Battery(new BatteryEvent(60, 2)));
    }

    [Theory]
    [InlineData("SquareGolf(A1B2)", null, LaunchMonitorType.Square)]
    [InlineData("squaregolf", null, LaunchMonitorType.Square)]
    [InlineData("Approach R10", null, LaunchMonitorType.GarminR10)]
    [InlineData("My Approach R10", null, LaunchMonitorType.GarminR10)]
    [InlineData("", (ushort)3622, LaunchMonitorType.GarminR10)]       // renamed, but its manufacturer data says R10
    [InlineData("Garage R10", (ushort)3622, LaunchMonitorType.GarminR10)]
    [InlineData("fenix 7", (ushort)3906, null)]                        // another Garmin device
    [InlineData("Headphones", null, null)]
    [InlineData(null, null, null)]
    public void ClassifiesAdvertisements(string? name, ushort? product, LaunchMonitorType? want) =>
        Assert.Equal(want, MonitorIdentity.Classify(name, product));

    [Fact]
    public void ReadsTheGarminProductNumber()
    {
        Assert.Equal((ushort)3622, MonitorIdentity.GarminProduct(new byte[] { 0x26, 0x0E, 0x01 }));
        Assert.Null(MonitorIdentity.GarminProduct(new byte[] { 0x26 }));
    }

    [Fact]
    public void OffersOnlyTheMonitorsChosenInSettings()
    {
        Assert.True(MonitorIdentity.Wanted(LaunchMonitorType.GarminR10, "Approach R10", LaunchMonitorType.Auto, null));
        Assert.True(MonitorIdentity.Wanted(LaunchMonitorType.Square, "SquareGolf(A1)", LaunchMonitorType.Square, " "));
        Assert.False(MonitorIdentity.Wanted(LaunchMonitorType.Square, "SquareGolf(A1)", LaunchMonitorType.GarminR10, null));
        Assert.True(MonitorIdentity.Wanted(LaunchMonitorType.Square, "SquareGolf(A1)", LaunchMonitorType.Auto, "squaregolf(a1) "));
        Assert.False(MonitorIdentity.Wanted(LaunchMonitorType.Square, "SquareGolf(B2)", LaunchMonitorType.Auto, "SquareGolf(A1)"));
    }

    [Fact]
    public async Task TheFakeSquareSendsShotsThroughTheSquareConversion()
    {
        var fake = new FakeSquareSession(_ => { });
        var connected = new TaskCompletionSource<MonitorDeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shot = new TaskCompletionSource<MonitorShot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Connected += i => connected.TrySetResult(i);
        fake.Shot += s => shot.TrySetResult(s);
        var running = Task.Run(() => fake.RunAsync("DR", false));
        Assert.Equal("Square Omni (hardware 3.1)", (await connected.Task.WaitAsync(TimeSpan.FromSeconds(5))).Description);
        fake.Hit(new RangeShot(150, 12, -1.5, 2800, 4, 102, null));
        var range = RangeShot.FromLaunchMonitor(await shot.Task.WaitAsync(TimeSpan.FromSeconds(5)), out _)!;
        Assert.Equal(150, range.BallMph, 6);
        Assert.Equal(4, range.SpinAxisDeg, 6);
        fake.Stop();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class MonitorSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-monitor-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void OlderSettingsLoadWithAnyMonitor()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName), "{ \"AutoConnect\": true, \"LastMonitor\": 12648430 }");
        var s = AppSettings.Load(_dir);
        Assert.Equal(LaunchMonitorType.Auto, s.MonitorType);
        Assert.Null(s.MonitorName);
        Assert.Equal(12648430UL, s.LastMonitor);
    }

    [Fact]
    public void TheMonitorChoiceIsSaved()
    {
        (AppSettings.Defaults with { MonitorType = LaunchMonitorType.GarminR10, MonitorName = "Approach R10" }).Save(_dir);
        Assert.Contains("\"MonitorType\": \"GarminR10\"", File.ReadAllText(Path.Combine(_dir, AppSettings.FileName)).Replace("\":\"", "\": \""));
        var s = AppSettings.Load(_dir);
        Assert.Equal(LaunchMonitorType.GarminR10, s.MonitorType);
        Assert.Equal("Approach R10", s.MonitorName);
    }
}
