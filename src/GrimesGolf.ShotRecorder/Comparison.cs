using System.Globalization;
using System.Text;
using GrimesGolf.Core;
using GrimesGolf.Core.Square;

namespace GrimesGolf.ShotRecorder;

/// <summary>A captured shot flown through our physics, beside the reference simulator's numbers for it (if typed in).</summary>
public sealed record ComparedShot(CapturedShot Shot, RangeShot Launch, FlightResult Ours, ReferenceShot? Reference)
{
    public double OursApexFt => Ours.ApexYards * 3;
    public double? DCarry => Reference?.CarryYds - Ours.CarryYards;
    public double? DTotal => Reference?.TotalYds - Ours.TotalYards;
    public double? DApex => Reference?.ApexFt - OursApexFt;
    public double? DOffline => Reference?.OfflineYds - Ours.LateralYards;
    public bool IsPutt => Shot.ClubCode == "PT";

    /// <summary>Flies a shot with the physics as it is now (<see cref="ActivePhysics"/>), as the range does.</summary>
    public static ComparedShot Fly(CapturedShot s, ReferenceShot? reference)
    {
        var launch = RangeShot.FromSquare(s.Ball, s.Club);
        return new ComparedShot(s, launch, launch.Fly(), reference);
    }
}

/// <summary>Console tables and the CSV for compared shots. Differences are reference minus ours: positive = GSPro went further.</summary>
public static class ComparisonReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string TableHeader =
        "  #  club   ball   VLA    HLA    spin   axis │  ours: carry total  apex  off │ GSPro: carry total  apex  off │   Δcarry Δtotal  Δapex";

    public static string LaunchLine(ComparedShot c)
    {
        var l = c.Launch;
        return $"{c.Shot.Number,3}  {c.Shot.ClubCode ?? "?",-4} {l.BallMph,5:0.0} {l.LaunchDeg,5:0.0} {RangeShot.Side(l.DirectionDeg),6} {l.SpinRpm,6:0} {RangeShot.Side(l.SpinAxisDeg),6}";
    }

    public static string Row(ComparedShot c)
    {
        var o = c.Ours;
        var r = c.Reference;
        string ours = $"{o.CarryYards,11:0.0} {o.TotalYards,5:0.0} {c.OursApexFt,5:0} {RangeShot.Side(o.LateralYards),5}";
        string theirs = r == null ? new string(' ', 29)
            : $"{N(r.CarryYds, 12)} {N(r.TotalYds, 5)} {N(r.ApexFt, 5, "0")} {(r.OfflineYds is { } off ? RangeShot.Side(off) : "-"),5}";
        string diff = r == null ? "" : $"{S(c.DCarry, 8)} {S(c.DTotal, 6)} {S(c.DApex, 6, "0")}";
        return $"{LaunchLine(c)} │{ours} │{theirs} │ {diff}";
    }

    /// <summary>Mean and spread of the differences, per club and over every shot that has reference numbers (putts left out).</summary>
    public static string Summary(IReadOnlyList<ComparedShot> shots)
    {
        var withRef = shots.Where(s => s.Reference != null && !s.IsPutt).ToList();
        if (withRef.Count == 0) return "No GSPro numbers yet, so nothing to compare.";
        var sb = new StringBuilder();
        sb.AppendLine("club    n │ Δcarry mean (sd)    % │ Δtotal mean (sd)    % │ Δapex ft mean (sd)");
        foreach (var g in withRef.GroupBy(s => s.Shot.ClubCode ?? "?").OrderBy(g => ClubPresets.IndexOf(g.Key)))
            sb.AppendLine(SummaryLine(g.Key, g.ToList()));
        if (withRef.Select(s => s.Shot.ClubCode).Distinct().Count() > 1) sb.AppendLine(SummaryLine("all", withRef));
        return sb.ToString().TrimEnd();
    }

    private static string SummaryLine(string label, List<ComparedShot> g) =>
        $"{label,-4} {g.Count,4} │ {Stat(g.Select(s => s.DCarry))} {Pct(g, s => s.DCarry, s => s.Ours.CarryYards)} │ " +
        $"{Stat(g.Select(s => s.DTotal))} {Pct(g, s => s.DTotal, s => s.Ours.TotalYards)} │ {Stat(g.Select(s => s.DApex), "0")}";

    private static string Stat(IEnumerable<double?> values, string fmt = "0.0")
    {
        var v = values.Where(x => x != null).Select(x => x!.Value).ToList();
        if (v.Count == 0) return $"{"-",16}";
        double mean = v.Average();
        double sd = v.Count > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) : 0;
        return $"{mean.ToString("+" + fmt + ";-" + fmt, Inv),8} ({sd.ToString(fmt, Inv),5})";
    }

    private static string Pct(List<ComparedShot> g, Func<ComparedShot, double?> diff, Func<ComparedShot, double> ours)
    {
        var v = g.Where(s => diff(s) != null && ours(s) > 1).Select(s => diff(s)!.Value / ours(s) * 100).ToList();
        return v.Count == 0 ? "    " : $"{v.Average().ToString("+0;-0;0", Inv) + "%",4}";
    }

    public static void WriteCsv(string path, IReadOnlyList<ComparedShot> shots, string tuningName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("shot,time,club,ball_mph,vla,hla,spin_rpm,spin_axis,back_rpm,side_rpm,club_mph,path,face,attack,loft," +
                      "our_carry,our_total,our_apex_ft,our_offline,our_hang_s,our_descent," +
                      "gspro_carry,gspro_total,gspro_apex_ft,gspro_offline,d_carry,d_total,d_apex_ft,d_offline,tuning,raw");
        foreach (var c in shots)
        {
            var l = c.Launch; var o = c.Ours; var r = c.Reference; var b = c.Shot.Ball; var k = c.Shot.Club;
            sb.AppendLine(string.Join(",",
                c.Shot.Number.ToString(Inv), c.Shot.At.ToString("o"), c.Shot.ClubCode ?? "",
                F(l.BallMph), F(l.LaunchDeg), F(l.DirectionDeg), F(l.SpinRpm, "0"), F(l.SpinAxisDeg), b.BackSpinRpm.ToString(Inv), b.SideSpinRpm.ToString(Inv),
                F(l.ClubMph), F(k?.PathDeg), F(k?.FaceDeg), F(k?.AttackDeg), F(k?.DynamicLoftDeg),
                F(o.CarryYards), F(o.TotalYards), F(c.OursApexFt), F(o.LateralYards), F(o.HangTimeSec, "0.00"), F(o.DescentDeg),
                F(r?.CarryYds), F(r?.TotalYds), F(r?.ApexFt), F(r?.OfflineYds), F(c.DCarry), F(c.DTotal), F(c.DApex), F(c.DOffline),
                tuningName.Replace(',', ' '), Convert.ToHexString(c.Shot.Raw)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string N(double? v, int width, string fmt = "0.0") => (v?.ToString(fmt, Inv) ?? "-").PadLeft(width);
    private static string S(double? v, int width, string fmt = "0.0") => (v?.ToString("+" + fmt + ";-" + fmt + ";0", Inv) ?? "-").PadLeft(width);
    private static string F(double? v, string fmt = "0.0#") => v?.ToString(fmt, Inv) ?? "";
}
