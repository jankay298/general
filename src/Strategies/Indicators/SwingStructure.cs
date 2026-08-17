using System;
using System.Collections.Generic;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Indicators;

/// <summary>Ein bestätigter Wendepunkt der Kursstruktur.</summary>
public sealed class SwingPoint
{
    public SwingPoint(DateTime timeUtc, decimal price, bool isLow, long barIndex)
    {
        TimeUtc = timeUtc;
        Price = price;
        IsLow = isLow;
        BarIndex = barIndex;
        Touches = 1;
    }

    public DateTime TimeUtc { get; }

    /// <summary>Laufende Nummer der Bar, auf der der Wendepunkt liegt - fuer sein Alter.</summary>
    public long BarIndex { get; internal set; }

    public decimal Price { get; }

    public bool IsLow { get; }

    /// <summary>Wie oft dieses Niveau angelaufen wurde. Mehrfach heißt: mehr Aufträge dahinter.</summary>
    public int Touches { get; internal set; }
}

/// <summary>
/// Findet die Wendepunkte der Wellenbewegung - die Hochs und Tiefs, die im Chart auffallen.
/// </summary>
/// <remarks>
/// Der Unterschied zum tiefsten Tief der letzten n Bars ist der ganze Punkt. Dieses Tief ist
/// oft ein einzelner Docht mitten in einer Bewegung, an dem niemand eine Order liegen hat. Ein
/// <b>Wendepunkt</b> ist eine Stelle, an der der Markt gedreht hat und die deshalb sichtbar ist -
/// und unter sichtbaren Tiefs liegen die Stops.
///
/// Bestätigt wird ein Tief erst, wenn danach <c>RightBars</c> Bars ohne neues Tief vergangen
/// sind. Diese Verzögerung ist keine Schwäche, sondern die Bedingung dafür, dass die Erkennung
/// nicht in die Zukunft schaut: Vorher ist schlicht nicht entscheidbar, ob der Markt dort
/// gedreht hat. Die Wartezeit ist auch der Grund, warum das Niveau überhaupt bekannt sein kann,
/// wenn es später angelaufen wird.
///
/// Zwei Wendepunkte auf fast gleicher Höhe werden zu einem zusammengefasst und zählen als
/// mehrfach berührt. Ein Doppeltief ist im Chart auffälliger als ein einzelnes - und damit die
/// bessere Vermutung darüber, wo sich Aufträge sammeln.
/// </remarks>
public sealed class SwingStructure
{
    private readonly int _leftBars;
    private readonly int _rightBars;
    private readonly int _maxLevels;
    private readonly List<Candle> _buffer = new List<Candle>();
    private readonly List<SwingPoint> _lows = new List<SwingPoint>();
    private readonly List<SwingPoint> _highs = new List<SwingPoint>();
    private long _barsSeen;

    /// <param name="mergeTolerance">
    /// Preisabstand, bis zu dem zwei Wendepunkte als dasselbe Niveau gelten. In Preiseinheiten,
    /// vom Aufrufer üblicherweise aus der ATR abgeleitet.
    /// </param>
    public SwingStructure(int leftBars = 3, int rightBars = 3, decimal mergeTolerance = 0m, int maxLevels = 12)
    {
        if (leftBars < 1 || rightBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(leftBars), "Es braucht mindestens eine Bar auf jeder Seite.");
        }

        _leftBars = leftBars;
        _rightBars = rightBars;
        MergeTolerance = Math.Max(0m, mergeTolerance);
        _maxLevels = Math.Max(1, maxLevels);
    }

    /// <summary>Bestätigte Tiefs, das jüngste zuerst.</summary>
    public IReadOnlyList<SwingPoint> Lows => _lows;

    /// <summary>Bestätigte Hochs, das jüngste zuerst.</summary>
    public IReadOnlyList<SwingPoint> Highs => _highs;

    public bool IsReady => _barsSeen > _leftBars + _rightBars;

    /// <summary>
    /// Preisabstand, bis zu dem zwei Wendepunkte als dasselbe Niveau gelten.
    /// </summary>
    /// <remarks>
    /// Verstellbar, weil die sinnvolle Toleranz an der Schwankungsbreite haengt: Auf Gold sind
    /// 30 Cent dasselbe Niveau, auf Bitcoin nicht. Der Aufrufer leitet sie aus der ATR ab,
    /// sobald die vorliegt - eine feste Zahl waere fuer ein Instrument immer falsch.
    /// </remarks>
    public decimal MergeTolerance { get; set; }

    /// <summary>Laufende Nummer der zuletzt aufgenommenen Bar.</summary>
    public long BarsSeen => _barsSeen;

    /// <summary>Nimmt eine abgeschlossene Bar auf und bestätigt dabei ggf. einen Wendepunkt.</summary>
    public void Update(Candle candle)
    {
        _barsSeen++;
        _buffer.Add(candle);

        var needed = _leftBars + _rightBars + 1;
        if (_buffer.Count < needed)
        {
            return;
        }

        // Der Kandidat liegt so weit zurück, dass rechts genug Bars stehen.
        var index = _buffer.Count - 1 - _rightBars;
        var candidate = _buffer[index];

        var isLow = true;
        var isHigh = true;
        for (var i = index - _leftBars; i <= index + _rightBars; i++)
        {
            if (i == index)
            {
                continue;
            }

            if (_buffer[i].Low <= candidate.Low)
            {
                isLow = false;
            }

            if (_buffer[i].High >= candidate.High)
            {
                isHigh = false;
            }
        }

        if (isLow)
        {
            Register(_lows, new SwingPoint(candidate.OpenTimeUtc, candidate.Low, isLow: true, _barsSeen - _rightBars));
        }

        if (isHigh)
        {
            Register(_highs, new SwingPoint(candidate.OpenTimeUtc, candidate.High, isLow: false, _barsSeen - _rightBars));
        }

        // Der Puffer braucht nur so viel Vergangenheit, wie die Erkennung sieht.
        if (_buffer.Count > needed * 4)
        {
            _buffer.RemoveRange(0, _buffer.Count - (needed * 2));
        }
    }

    private void Register(List<SwingPoint> levels, SwingPoint point)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            if (Math.Abs(levels[i].Price - point.Price) <= MergeTolerance)
            {
                // Dasselbe Niveau ein weiteres Mal angelaufen - auffaelliger, nicht neuer.
                // Der Zeitstempel wandert mit, sonst altert ein mehrfach bestaetigtes Niveau
                // schneller aus als eines, das nur einmal angelaufen wurde.
                levels[i].Touches++;
                levels[i].BarIndex = point.BarIndex;
                return;
            }
        }

        levels.Insert(0, point);
        if (levels.Count > _maxLevels)
        {
            levels.RemoveAt(levels.Count - 1);
        }
    }
}
