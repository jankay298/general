using System;
using System.Collections;
using System.Collections.Generic;

namespace Daytrading.Strategies.Indicators;

/// <summary>
/// Ringpuffer fester Größe. <c>this[0]</c> ist der zuletzt hinzugefügte Wert.
/// </summary>
/// <remarks>
/// Basis für inkrementelle Indikatoren: konstanter Speicher und konstante Kosten je Bar,
/// unabhängig von der Länge des Backtests. Indikatoren werden hier selbst implementiert,
/// damit die Strategie-Bibliothek ohne Fremdabhängigkeit und ohne cAlgo auskommt.
/// </remarks>
public sealed class RollingWindow<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _nextIndex;

    public RollingWindow(int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Fenstergröße muss positiv sein.");
        }

        _buffer = new T[size];
        Size = size;
    }

    /// <summary>Kapazität des Fensters.</summary>
    public int Size { get; }

    /// <summary>Anzahl aktuell enthaltener Werte (höchstens <see cref="Size"/>).</summary>
    public int Count { get; private set; }

    public bool IsFull => Count == Size;

    /// <summary>Gesamtzahl jemals hinzugefügter Werte.</summary>
    public long TotalAdded { get; private set; }

    /// <summary>Zugriff rückwärts: 0 ist der neueste Wert.</summary>
    public T this[int offset]
    {
        get
        {
            if (offset < 0 || offset >= Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, $"Fenster enthält {Count} Werte.");
            }

            var index = ((_nextIndex - 1 - offset) % Size + Size) % Size;
            return _buffer[index];
        }
    }

    public void Add(T value)
    {
        _buffer[_nextIndex] = value;
        _nextIndex = (_nextIndex + 1) % Size;
        if (Count < Size)
        {
            Count++;
        }

        TotalAdded++;
    }

    public void Reset()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _nextIndex = 0;
        Count = 0;
        TotalAdded = 0;
    }

    /// <summary>Iteriert vom neuesten zum ältesten Wert.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var offset = 0; offset < Count; offset++)
        {
            yield return this[offset];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
