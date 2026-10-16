using System.Diagnostics;
using Core.DTOs;
using Core.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Evaluación comparativa EN MEMORIA (sin BD, sin efectos) del motor 8.182/8.184
/// ("nearest bidireccional + piso de margen reactivo + salto al escalón superior")
/// contra el algoritmo quirúrgico por techo del spike pre-8.183:
///   P_ves = ceil( costo x (1 + margen/100) x tasa / K ) x K   con K en {1, 5, 10}
///
/// 8.185-perf: tras el readonly record struct + techo directo, la paridad 6-args ≡ 4-args es exacta
/// (1000/1000) y los cuatro caminos miden zero-allocation; estas aserciones son la regresión.
/// Mide: (1) equivalencia e invariantes de margen sobre 1.000 combinaciones determinísticas;
/// (2) latencia y allocations por 10.000 llamadas por camino (incluye el path de display 4-args
/// que usan Catálogo/POS/Carrito); (3) impacto porcentual del techo en artículos baratos.
/// Es un banco de evaluación: solo imprime métricas y asserts de invariantes determinísticos
/// (sin aserciones de tiempo). No muta nada.
/// </summary>
public class RoundingStrategyBenchmarkTests
{
    private const int DatasetSize = 1000;
    private const int LoopsPerCall = 10; // dataset(1000) x 10 = 10.000 iteraciones por camino

    private static readonly decimal[] Rates = { 40m, 100m, 400m, 880m, 2000m };
    private static readonly int[] Ks = { 1, 5, 10 };

    private readonly ITestOutputHelper _output;
    public RoundingStrategyBenchmarkTests(ITestOutputHelper output) => _output = output;

    private static RoundingRuleType RuleFor(int k) => k switch
    {
        1 => RoundingRuleType.VesCashFriendlyInteger,
        5 => RoundingRuleType.VesCashFriendlyStep5,
        _ => RoundingRuleType.VesCashFriendlyStep10
    };

    // Algoritmo quirúrgico (spike): espejo local del kernel evaluado en experiment/surgical-ceiling-rounding.
    private static decimal SurgicalCeil(decimal costUsd, decimal marginPct, decimal rate, int k)
        => Math.Ceiling(costUsd * (1m + (marginPct / 100m)) * rate / k) * k;

    // Nearest del motor (RoundToNearestStep): escalón inferior si el empate o si está más cerca.
    private static decimal NearestStep(decimal amount, int k)
    {
        decimal lower = Math.Floor(amount / k) * k;
        if (lower <= 0m) return lower + k;
        decimal upper = lower + k;
        return amount - lower <= upper - amount ? lower : upper;
    }

    private static decimal MarginPct(decimal priceBsS, decimal costBsS)
        => costBsS > 0m ? (priceBsS / costBsS - 1m) * 100m : 0m;

    private static List<(decimal Cost, decimal Margin, decimal Rate, int K)> BuildDataset()
    {
        var rnd = new Random(183);
        var data = new List<(decimal, decimal, decimal, int)>(DatasetSize);
        for (int i = 0; i < DatasetSize; i++)
        {
            decimal cost = Math.Round(0.10m + (decimal)rnd.NextDouble() * 49.90m, 2);
            decimal margin = Math.Round(15m + (decimal)rnd.NextDouble() * 35m, 2);
            decimal rate = Rates[rnd.Next(Rates.Length)];
            int k = Ks[rnd.Next(Ks.Length)];
            data.Add((cost, margin, rate, k));
        }
        return data;
    }

    [Fact]
    public void Fase2_EquivalenciaYMargenes()
    {
        var data = BuildDataset();
        int equivalent6 = 0, equivalent4 = 0, aonBumps = 0, bMarginOk = 0, aonMarginOk = 0, aoffDown = 0, aoffMarginOk = 0;
        decimal aoffMinMargin = decimal.MaxValue, bOverGainTotal = 0m, bOverGainMax = 0m, aoffDeltaTotal = 0m, aonMaxDeficit = 0m;

        foreach (var (cost, margin, rate, k) in data)
        {
            var rule = RuleFor(k);
            decimal usdBase = cost * (1m + margin / 100m);
            decimal baseBsS = usdBase * rate;
            decimal costBsS = cost * rate;

            decimal b = SurgicalCeil(cost, margin, rate, k);
            decimal nearest = NearestStep(baseBsS, k);

            var aon = PricingRoundingPolicy.ProjectCashFriendlyBsS(usdBase, rate, rule, cost, margin, true)!.Value.RoundedValue;
            var aoff = PricingRoundingPolicy.ProjectCashFriendlyBsS(usdBase, rate, rule, cost, margin, false)!.Value.RoundedValue;
            var a4 = PricingRoundingPolicy.ProjectCashFriendlyBsS(usdBase, rate, rule, true)!.Value.RoundedValue;

            if (aon == b) equivalent6++;
            if (a4 == b) equivalent4++;
            if (aon > nearest) aonBumps++; // el nearest cayó hacia abajo y el piso forzó el salto

            if (MarginPct(b, costBsS) >= margin - 0.0001m) bMarginOk++;
            if (MarginPct(aon, costBsS) >= margin - 0.0001m) aonMarginOk++;
            if (MarginPct(aoff, costBsS) >= margin - 0.0001m) aoffMarginOk++;

            if (aoff < baseBsS) aoffDown++;
            aoffMinMargin = Math.Min(aoffMinMargin, MarginPct(aoff, costBsS));
            aonMaxDeficit = Math.Max(aonMaxDeficit, margin - MarginPct(aon, costBsS)); // borde 2dp del margen

            bOverGainTotal += b - baseBsS;
            bOverGainMax = Math.Max(bOverGainMax, b - baseBsS);
            aoffDeltaTotal += aoff - baseBsS;
        }

        _output.WriteLine($"Dataset: {DatasetSize} combos (costo 0.10-50.00 USD, margen 15-50%, tasas {string.Join('/', Rates)}, K en {{1,5,10}}), seed 183");
        _output.WriteLine($"Equivalencia Surgical == 8.184 piso-ON (6-args):   {equivalent6}/{DatasetSize}");
        _output.WriteLine($"Equivalencia Surgical == display never-down (4-args): {equivalent4}/{DatasetSize}");
        _output.WriteLine($"8.184 piso-ON requirio salto hacia arriba (bump):   {aonBumps}/{DatasetSize} casos");
        _output.WriteLine($"Margen >= objetivo  | Surgical: {bMarginOk}/{DatasetSize} | 8.184 ON: {aonMarginOk}/{DatasetSize} | 8.184 OFF: {aoffMarginOk}/{DatasetSize}");
        _output.WriteLine($"8.184 OFF (nearest) redondeo hacia abajo: {aoffDown}/{DatasetSize} casos | margen minimo observado: {aoffMinMargin:F4}%");
        _output.WriteLine($"8.184 ON deficit maximo de margen (borde 2dp): {aonMaxDeficit:F4} pp (sub-tolerancia, nunca visible)");
        _output.WriteLine($"Sobre-ganancia Surgical (vs base): promedio {bOverGainTotal / DatasetSize:F4} Bs.S | maximo {bOverGainMax:F4} Bs.S");
        _output.WriteLine($"Delta 8.184 OFF (vs base): promedio {aoffDeltaTotal / DatasetSize:F4} Bs.S (negativo = perdida de margen por redondeo a la baja)");

        Assert.True(equivalent4 == DatasetSize, "El display never-down (4-args) debe ser identico al techo quirurgico en el 100% de los casos");
        Assert.True(equivalent6 == DatasetSize, $"Paridad exacta 6-args piso-ON ≡ 4-args ≡ techo quirurgico: {equivalent6}/{DatasetSize}");
        Assert.True(bMarginOk == DatasetSize, "El techo quirurgico garantiza margen >= objetivo en el 100%");
        Assert.True(aonMarginOk == DatasetSize && aonMaxDeficit <= 0.0001m, $"8.184 piso-ON: margen >= objetivo en el 100% (cero perdidas): {aonMarginOk}/{DatasetSize} | deficit {aonMaxDeficit} pp");
    }

    [Fact]
    public void Fase2_LatenciaYAllocations()
    {
        var data = BuildDataset();

        decimal SurgicalPass()
        {
            decimal acc = 0m;
            foreach (var (cost, margin, rate, k) in data) acc += SurgicalCeil(cost, margin, rate, k);
            return acc;
        }

        decimal Engine6OnPass()
        {
            decimal acc = 0m;
            foreach (var (cost, margin, rate, k) in data)
                acc += PricingRoundingPolicy.ProjectCashFriendlyBsS(cost * (1m + margin / 100m), rate, RuleFor(k), cost, margin, true)!.Value.RoundedValue;
            return acc;
        }

        decimal Engine6OffPass()
        {
            decimal acc = 0m;
            foreach (var (cost, margin, rate, k) in data)
                acc += PricingRoundingPolicy.ProjectCashFriendlyBsS(cost * (1m + margin / 100m), rate, RuleFor(k), cost, margin, false)!.Value.RoundedValue;
            return acc;
        }

        decimal Engine4Pass()
        {
            decimal acc = 0m;
            foreach (var (cost, margin, rate, k) in data)
                acc += PricingRoundingPolicy.ProjectCashFriendlyBsS(cost * (1m + margin / 100m), rate, RuleFor(k), true)!.Value.RoundedValue;
            return acc;
        }

        var b = Measure("SurgicalCeil (local)", SurgicalPass);
        var a6on = Measure("8.184 6-args piso-ON     ", Engine6OnPass);
        var a6off = Measure("8.184 6-args piso-OFF    ", Engine6OffPass);
        var a4 = Measure("8.184 4-args display     ", Engine4Pass);

        _output.WriteLine($"Latencia y allocations por 10.000 llamadas (Release; mejor de 3 pasadas tras 3 de warmup):");
        _output.WriteLine($"  SurgicalCeil: {b.Ms:F2} ms | {b.Bytes} bytes/10k");
        _output.WriteLine($"  8.184 ON:     {a6on.Ms:F2} ms | {a6on.Bytes} bytes/10k");
        _output.WriteLine($"  8.184 OFF:    {a6off.Ms:F2} ms | {a6off.Bytes} bytes/10k");
        _output.WriteLine($"  8.184 4-args: {a4.Ms:F2} ms | {a4.Bytes} bytes/10k");

        // O(1) zero-allocation: los cuatro caminos son aritmetica decimal pura sobre un readonly
        // record struct; la tolerancia cubre cualquier artefacto de instrumentacion.
        Assert.True(b.Bytes <= 512, $"SurgicalCeil debe ser zero-allocation: {b.Bytes} bytes/10k");
        Assert.True(a6on.Bytes <= 512, $"6-args piso-ON debe ser zero-allocation: {a6on.Bytes} bytes/10k");
        Assert.True(a6off.Bytes <= 512, $"6-args piso-OFF debe ser zero-allocation: {a6off.Bytes} bytes/10k");
        Assert.True(a4.Bytes <= 512, $"4-args display debe ser zero-allocation: {a4.Bytes} bytes/10k");
    }

    private (double Ms, long Bytes) Measure(string name, Func<decimal> body)
    {
        // 3 pasadas de warmup (promoción tier-1 del JIT) + mejor de 3 pasadas medidas:
        // número representativo de régimen, no de la primera ejecución en tier-0.
        for (int i = 0; i < 3; i++) _ = body();

        double best = double.MaxValue;
        long bytes = 0;
        for (int run = 0; run < 3; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            decimal sink = 0m;
            for (int loop = 0; loop < LoopsPerCall; loop++) sink += body();
            sw.Stop();
            long runBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(sink);
            if (sw.Elapsed.TotalMilliseconds < best)
            {
                best = sw.Elapsed.TotalMilliseconds;
                bytes = runBytes;
            }
        }

        return (best, bytes);
    }

    [Fact]
    public void Fase2_ImpactoPreciosBajos()
    {
        decimal[] prices = { 0.10m, 0.25m, 0.49m };
        decimal[] rates = { 40m, 880m, 2000m };

        _output.WriteLine("Impacto del techo en articulos baratos (base = precio USD x tasa; delta sobre el escalon elegido):");
        foreach (var rate in rates)
        {
            foreach (var price in prices)
            {
                decimal baseBsS = price * rate;
                foreach (var k in Ks)
                {
                    decimal eff = Math.Ceiling(baseBsS / k) * k;
                    decimal delta = eff - baseBsS;
                    decimal pct = baseBsS > 0m ? delta / baseBsS * 100m : 0m;
                    _output.WriteLine($"  ${price:F2} @ tasa {rate,-5} K={k,-2} -> base {baseBsS,9:F2} | techo {eff,9:F2} | +{delta,7:F2} Bs.S (+{pct:F2}%)");
                }
            }
        }
    }
}
