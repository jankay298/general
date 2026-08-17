using System;
using System.Globalization;

namespace Daytrading.Execution;

/// <summary>
/// Kontostand, Equity und die daraus abgeleiteten Kennzahlen, an denen die Risikogrenzen hängen.
/// </summary>
/// <remarks>
/// Gehört dem Host: Der Backtester schreibt hier die simulierten Werte hinein, der cBot die
/// echten vom Broker. Die Ausführungsschicht liest nur. Strategien sehen dieses Objekt nie.
///
/// Bezugsgröße aller Tagesprozente ist <see cref="DayStartBalance"/> - der Kontostand beim
/// ersten Aufruf von <see cref="BeginTradingDay"/> an diesem Tag.
/// </remarks>
public sealed class AccountState
{
    public AccountState(decimal startingBalance)
    {
        if (startingBalance <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startingBalance), startingBalance, "Startkapital muss positiv sein.");
        }

        Balance = startingBalance;
        Equity = startingBalance;
        DayStartBalance = startingBalance;
        DayEquityHigh = startingBalance;
        PeakEquity = startingBalance;
        TradingDay = DateTime.MinValue;
    }

    /// <summary>Realisierter Kontostand ohne offene Positionen.</summary>
    public decimal Balance { get; private set; }

    /// <summary>Kontostand einschließlich der unrealisierten Ergebnisse offener Positionen.</summary>
    public decimal Equity { get; private set; }

    public DateTime TradingDay { get; private set; }

    /// <summary>Kontostand zu Beginn des laufenden Handelstages. Bezugsgröße aller Tagesprozente.</summary>
    public decimal DayStartBalance { get; private set; }

    /// <summary>Höchster Equity-Stand des laufenden Handelstages.</summary>
    public decimal DayEquityHigh { get; private set; }

    /// <summary>Höchster Equity-Stand überhaupt. Bezugsgröße des Gesamt-Drawdowns.</summary>
    public decimal PeakEquity { get; private set; }

    public int TradesToday { get; private set; }

    /// <summary>Realisierter Tagesverlust in Prozent. Nie negativ - ein Gewinntag ergibt 0.</summary>
    public decimal DailyLossPercent => Percent(DayStartBalance - Balance, DayStartBalance);

    /// <summary>
    /// Rückgang vom Tageshoch der Equity in Prozent, realisiert und unrealisiert zusammen.
    /// Ein Tag mit +3 % und Rückfall auf −1 % ergibt hier 4 %, obwohl realisiert erst 1 % fehlt.
    /// </summary>
    public decimal DailyDrawdownPercent => Percent(DayEquityHigh - Equity, DayStartBalance);

    /// <summary>Rückgang vom historischen Equity-Hoch in Prozent.</summary>
    public decimal TotalDrawdownPercent => Percent(PeakEquity - Equity, PeakEquity);

    /// <summary>
    /// Beginnt einen neuen Handelstag: setzt Tagesbezug, Tageshoch und Trade-Zähler zurück.
    /// Mehrfachaufrufe mit demselben Tag sind wirkungslos.
    /// </summary>
    public void BeginTradingDay(DateTime tradingDay)
    {
        if (tradingDay.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Handelstag muss UTC sein.", nameof(tradingDay));
        }

        if (tradingDay == TradingDay)
        {
            return;
        }

        if (tradingDay < TradingDay)
        {
            throw new InvalidOperationException(
                $"Handelstage laufen rückwärts: {tradingDay:yyyy-MM-dd} nach {TradingDay:yyyy-MM-dd}.");
        }

        TradingDay = tradingDay;
        DayStartBalance = Balance;
        DayEquityHigh = Equity;
        TradesToday = 0;
    }

    /// <summary>
    /// Setzt die aktuelle Equity und schreibt dabei Tages- und Allzeithoch fort.
    /// Vom Host je Bar aufzurufen, bevor die Ausführungsschicht entscheidet.
    /// </summary>
    public void UpdateEquity(decimal equity)
    {
        Equity = equity;
        if (equity > DayEquityHigh)
        {
            DayEquityHigh = equity;
        }

        if (equity > PeakEquity)
        {
            PeakEquity = equity;
        }
    }

    /// <summary>
    /// Übernimmt Kontostand und Equity vom Broker. Für den Livebetrieb, wo der Broker die
    /// Wahrheit hält - Tagesbezug, Tageshoch und Trade-Zähler bleiben unberührt.
    /// </summary>
    public void SyncFromBroker(decimal balance, decimal equity)
    {
        if (balance <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(balance), balance, "Kontostand muss positiv sein.");
        }

        Balance = balance;
        UpdateEquity(equity);
    }

    /// <summary>
    /// Bucht ein Ergebnis auf den Kontostand, das noch nicht in der Equity steckt - etwa die
    /// Kommission beim Eröffnen. Equity und Kontostand ändern sich um denselben Betrag.
    /// </summary>
    public void ApplyRealizedPnL(decimal pnl)
    {
        Balance += pnl;
        UpdateEquity(Equity + pnl);
    }

    /// <summary>
    /// Bucht das Ergebnis einer <b>geschlossenen</b> Position auf den Kontostand.
    /// </summary>
    /// <remarks>
    /// Hier ändert sich nur der Kontostand, nicht die Equity: Das Ergebnis war als
    /// unrealisierter Betrag längst in der Equity enthalten, es wechselt beim Schließen
    /// lediglich die Seite. Würde es zusätzlich auf die Equity gebucht, zählte jeder Gewinn
    /// doppelt - und da <see cref="UpdateEquity"/> jedes Hoch festhält, bliebe ein
    /// <see cref="PeakEquity"/> stehen, das es nie gab. Der daran gemessene Drawdown wäre zu
    /// groß und die Strategie würde zu früh abgeschaltet.
    ///
    /// Der Aufrufer bewertet danach neu (<see cref="UpdateEquity"/> mit Kontostand plus den
    /// verbleibenden offenen Positionen).
    /// </remarks>
    public void RealizeClosedPosition(decimal pnl) => Balance += pnl;

    /// <summary>Zählt einen an diesem Tag eröffneten Trade.</summary>
    public void RegisterTradeOpened() => TradesToday++;

    private static decimal Percent(decimal amount, decimal basis)
    {
        if (basis <= 0m || amount <= 0m)
        {
            return 0m;
        }

        return amount / basis * 100m;
    }

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Balance={0} Equity={1} Tag={2:yyyy-MM-dd} Tagesverlust={3:0.00}% Tages-DD={4:0.00}% Gesamt-DD={5:0.00}% Trades={6}",
            Balance, Equity, TradingDay, DailyLossPercent, DailyDrawdownPercent, TotalDrawdownPercent, TradesToday);
}
