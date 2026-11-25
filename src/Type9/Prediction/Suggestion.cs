namespace Type9.Prediction;

/// <summary>Correction — исправление опечатки: набранное слово стирается целиком.</summary>
public enum SuggestionKind { Completion, NextWord, Abbreviation, Correction }

/// <summary>
/// Вариант подсказки. <see cref="Backspaces"/> символов стирается, затем печатается <see cref="Insert"/>.
/// <see cref="Typed"/>/<see cref="Rest"/> — для отображения (набранная часть приглушена).
/// </summary>
public sealed record Suggestion(SuggestionKind Kind, string Typed, string Rest, string Insert, int Backspaces)
{
    public string Display => Typed + Rest;
}
