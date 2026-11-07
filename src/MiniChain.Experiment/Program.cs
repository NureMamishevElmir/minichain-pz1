using System.Diagnostics;
using System.Globalization;
using MiniChain.Core;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

// Експерименти:
//   dotnet run -c Release --project src/MiniChain.Experiment -- difficulty [runs] [bits...]
//   dotnet run -c Release --project src/MiniChain.Experiment -- adjust [blocks] [targetMs]
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var mode = args.FirstOrDefault() ?? "difficulty";
var outDir = Path.GetFullPath("results");
Directory.CreateDirectory(outDir);

if (mode == "difficulty") DifficultyExperiment();
else if (mode == "adjust") AdjustExperiment();
else Console.WriteLine("режими: difficulty | adjust");

// ---------------------------------------------------------------- 1. час майнінгу від складності
void DifficultyExperiment()
{
    int runs = args.Length > 1 ? int.Parse(args[1]) : 10;
    int[] levels = args.Length > 2 ? args[2..].Select(int.Parse).ToArray() : new[] { 12, 16, 20, 24 };
    var rnd = new Random(2026);
    var rows = new List<(int bits, int run, double sec, long attempts)>();
    Console.WriteLine($"Майнінг блоку: {runs} запусків на кожен рівень складності, 1 потік");
    Console.WriteLine($"{"біт",4} {"hex-нулів",9} {"запуск",6} {"час, с",10} {"спроб",14}");
    foreach (var bits in levels)
        for (int run = 1; run <= runs; run++)
        {
            // кожен запуск — новий випадковий заголовок, щоб результати були незалежними
            var prev = new byte[32]; rnd.NextBytes(prev);
            var header = new BlockHeader
            {
                Index = run, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), PrevHash = Hash.ToHex(prev),
                MerkleRoot = Hash.Sha256Hex($"run-{bits}-{run}"), Difficulty = bits
            };
            var sw = Stopwatch.StartNew();
            long attempts = ProofOfWork.Mine(header);
            sw.Stop();
            rows.Add((bits, run, sw.Elapsed.TotalSeconds, attempts));
            Console.WriteLine($"{bits,4} {bits / 4.0,9:0.#} {run,6} {sw.Elapsed.TotalSeconds,10:F4} {attempts,14:N0}");
        }

    File.WriteAllLines(Path.Combine(outDir, "difficulty_runs.csv"),
        rows.Select(r => $"{r.bits},{r.run},{r.sec:F6},{r.attempts}").Prepend("bits,run,seconds,attempts"));

    double rate = rows.Sum(r => r.attempts) / rows.Sum(r => r.sec);   // хешів за секунду
    var summary = levels.Select(b =>
    {
        var s = rows.Where(r => r.bits == b).Select(r => r.sec).ToArray();
        double mean = s.Average();
        double sd = Math.Sqrt(s.Sum(x => (x - mean) * (x - mean)) / (s.Length - 1));
        return (bits: b, mean, sd, min: s.Min(), max: s.Max(), theory: Math.Pow(2, b) / rate,
                attempts: rows.Where(r => r.bits == b).Average(r => r.attempts));
    }).ToList();

    Console.WriteLine($"\nШвидкість хешування: {rate / 1e6:F2} млн хешів/с");
    Console.WriteLine($"{"біт",4} {"hex",4} {"середнє, с",11} {"σ, с",9} {"мін",9} {"макс",9} {"теорія, с",10} {"сер. спроб",14} {"2^біт",14} {"×до попер.",10}");
    double? prevMean = null;
    var lines = new List<string> { "bits,hex_zeros,mean_s,sd_s,min_s,max_s,theory_s,mean_attempts,expected_attempts,ratio_to_prev" };
    foreach (var s in summary)
    {
        string ratio = prevMean is null ? "—" : $"{s.mean / prevMean:F1}";
        Console.WriteLine($"{s.bits,4} {s.bits / 4,4} {s.mean,11:F4} {s.sd,9:F4} {s.min,9:F4} {s.max,9:F4} {s.theory,10:F4} {s.attempts,14:N0} {Math.Pow(2, s.bits),14:N0} {ratio,10}");
        lines.Add($"{s.bits},{s.bits / 4.0},{s.mean:F6},{s.sd:F6},{s.min:F6},{s.max:F6},{s.theory:F6},{s.attempts:F0},{Math.Pow(2, s.bits):F0},{(prevMean is null ? "" : (s.mean / prevMean.Value).ToString("F2"))}");
        prevMean = s.mean;
    }
    lines.Add($"# hash_rate_per_s,{rate:F0}");
    File.WriteAllLines(Path.Combine(outDir, "difficulty_summary.csv"), lines);

    // графік: вісь часу логарифмічна
    var model = new PlotModel { Title = "Час майнінгу блоку залежно від складності", Background = OxyColors.White };
    model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopLeft, LegendBorder = OxyColors.Gray });
    model.Axes.Add(new LinearAxis
    {
        Position = AxisPosition.Bottom, Title = "Складність, нульових бітів (у дужках — hex-нулів)",
        Minimum = levels.Min() - 2, Maximum = levels.Max() + 2, MajorStep = 4, MinorStep = 1,
        LabelFormatter = v => $"{v:0} ({v / 4:0.#})", MajorGridlineStyle = LineStyle.Dot
    });
    model.Axes.Add(new LogarithmicAxis
    {
        Position = AxisPosition.Left, Title = "Час, с (логарифмічна шкала)",
        MajorGridlineStyle = LineStyle.Dot, MinorGridlineStyle = LineStyle.None, StringFormat = "0.####"
    });
    var runsSeries = new ScatterSeries { Title = $"окремі запуски ({runs} на рівень)", MarkerType = MarkerType.Circle, MarkerSize = 3, MarkerFill = OxyColor.FromAColor(110, OxyColors.SteelBlue) };
    foreach (var r in rows) runsSeries.Points.Add(new ScatterPoint(r.bits + (r.run - runs / 2.0) * 0.08, Math.Max(r.sec, 1e-5)));
    var meanSeries = new ScatterErrorSeries { Title = "середнє ± стандартне відхилення", MarkerType = MarkerType.Square, MarkerSize = 5, MarkerFill = OxyColors.DarkRed, ErrorBarColor = OxyColors.DarkRed, ErrorBarStopWidth = 4, ErrorBarStrokeThickness = 1.5 };
    foreach (var s in summary) meanSeries.Points.Add(new ScatterErrorPoint(s.bits, s.mean, 0, Math.Min(s.sd, s.mean * 0.95)));
    var meanLine = new LineSeries { Color = OxyColors.DarkRed, StrokeThickness = 1.2, LineStyle = LineStyle.Solid };
    foreach (var s in summary) meanLine.Points.Add(new DataPoint(s.bits, s.mean));
    var theory = new LineSeries { Title = $"теорія: 2^біт / {rate / 1e6:F1} млн хеш/с", Color = OxyColors.Gray, LineStyle = LineStyle.Dash };
    for (double b = levels.Min() - 1; b <= levels.Max() + 1; b += 0.5) theory.Points.Add(new DataPoint(b, Math.Pow(2, b) / rate));
    model.Series.Add(theory); model.Series.Add(runsSeries); model.Series.Add(meanLine); model.Series.Add(meanSeries);
    var png = Path.Combine(outDir, "difficulty_plot.png");
    OxyPlot.SkiaSharp.PngExporter.Export(model, png, 1100, 650, 96);
    Console.WriteLine($"\nCSV і графік: {outDir}");
}

// ---------------------------------------------------------------- 2. автоматичне коригування складності
void AdjustExperiment()
{
    int blocks = args.Length > 1 ? int.Parse(args[1]) : 80;
    long targetMs = args.Length > 2 ? long.Parse(args[2]) : 1000;
    var p = ChainParams.Default with { InitialDifficulty = 14, TargetBlockMs = targetMs, MinDifficulty = 4 };
    var chain = new Blockchain(p);
    // генезис має фіксовану мітку часу в минулому — щоб перший інтервал не враховувався, він і так пропускається
    var miner = Wallet.Create().Address;
    var rows = new List<(long h, int diff, double sec)>();
    Console.WriteLine($"Ціль: {targetMs} мс на блок, K = {p.AdjustInterval}, старт зі складності {p.InitialDifficulty} біт");
    for (int i = 1; i <= blocks; i++)
    {
        var sw = Stopwatch.StartNew();
        var b = chain.CreateCandidate(miner, Array.Empty<Transaction>());
        ProofOfWork.Mine(b.Header);
        b.Hash = b.Header.ComputeHash();
        sw.Stop();
        // мітка часу = початок майнінгу, тому інтервал між сусідніми мітками = фактичний час майнінгу блоку
        var err = chain.TryAddBlock(b);
        if (err is not null) { Console.WriteLine($"помилка: {err}"); return; }
        rows.Add((b.Index, b.Header.Difficulty, sw.Elapsed.TotalSeconds));
        Console.WriteLine($"блок {b.Index,3}: складність {b.Header.Difficulty,2} біт, час {sw.Elapsed.TotalSeconds,7:F3} с{(b.Index % p.AdjustInterval == 0 ? "   ← точка коригування" : "")}");
    }
    File.WriteAllLines(Path.Combine(outDir, "adjust.csv"), rows.Select(r => $"{r.h},{r.diff},{r.sec:F4}").Prepend("height,difficulty,seconds"));
    foreach (var g in rows.GroupBy(r => (r.h - 1) / p.AdjustInterval))
        Console.WriteLine($"блоки {g.First().h,3}–{g.Last().h,3}: складність {g.First().diff,2}, середній час {g.Average(r => r.sec):F3} с");

    var model = new PlotModel { Title = $"Автоматичне коригування складності (ціль {targetMs / 1000.0:0.##} с, K = {p.AdjustInterval})", Background = OxyColors.White };
    model.Legends.Add(new Legend { LegendPosition = LegendPosition.LeftTop, LegendPlacement = LegendPlacement.Inside, LegendBorder = OxyColors.Gray });
    model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Номер блоку", MajorGridlineStyle = LineStyle.Dot });
    model.Axes.Add(new LogarithmicAxis { Position = AxisPosition.Left, Title = "Час блоку, с (лог.)", Key = "t", MajorGridlineStyle = LineStyle.Dot });
    model.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Title = "Складність, біт", Key = "d", Minimum = 0 });
    var times = new ScatterSeries { Title = "час блоку", YAxisKey = "t", MarkerType = MarkerType.Circle, MarkerSize = 3, MarkerFill = OxyColors.SteelBlue };
    foreach (var r in rows) times.Points.Add(new ScatterPoint(r.h, Math.Max(r.sec, 1e-4)));
    var target = new LineSeries { Title = "ціль", YAxisKey = "t", Color = OxyColors.Gray, LineStyle = LineStyle.Dash };
    target.Points.Add(new DataPoint(1, targetMs / 1000.0)); target.Points.Add(new DataPoint(blocks, targetMs / 1000.0));
    var diff = new StairStepSeries { Title = "складність", YAxisKey = "d", Color = OxyColors.DarkRed, StrokeThickness = 2 };
    foreach (var r in rows) diff.Points.Add(new DataPoint(r.h, r.diff));
    model.Series.Add(target); model.Series.Add(times); model.Series.Add(diff);
    OxyPlot.SkiaSharp.PngExporter.Export(model, Path.Combine(outDir, "adjust_plot.png"), 1100, 600, 96);
    Console.WriteLine($"CSV і графік: {outDir}");
}
