using System;
using System.Collections.Generic;
using System.Linq;
using TruthOrDare.Models;

namespace TruthOrDare;

public sealed class GameSession
{
    private readonly Random random = new();
    private readonly Queue<Guid> drawPile = new();
    public Card? CurrentCard { get; private set; }
    public int Remaining => drawPile.Count;

    public void Reset(Deck deck, CardCategory category)
    {
        drawPile.Clear();
        foreach (var id in deck.Cards.Where(card => card.Category.HasFlag(category)).Select(card => card.Id).OrderBy(_ => random.Next())) drawPile.Enqueue(id);
        CurrentCard = null;
    }

    public Card? Draw(Deck deck, CardCategory category)
    {
        while (drawPile.Count > 0)
        {
            var id = drawPile.Dequeue();
            var card = deck.Cards.FirstOrDefault(candidate => candidate.Id == id && candidate.Category.HasFlag(category));
            if (card is not null) return CurrentCard = card;
        }
        return CurrentCard = null;
    }
}
